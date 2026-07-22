# 全体構成・設計

## 目的と用語

このプロジェクトで扱う caustics は、光線が屈折面を通過した後に receiver 上へ集中する現象を、投影メッシュとして近似します。

- source: 光が入射する入力面。長方形の共有頂点グリッド
- receiver: 屈折光を受ける MeshRenderer
- projected triangle: source の三角形を receiver 上へ投影した三角形
- height field: source の高さと勾配を返す入力モデル

## 全体構成

```text
CausticRayQueryTest
 ├─ source grid / height field
 ├─ receiver geometry / ray tracing AS
 └─ GPU buffers
       ↓
CausticRayQuery.compute
 ├─ source vertices
 ├─ ray hits
 ├─ triangle results
 └─ projected triangles
       ↓
ProjectedCausticTriangle.shader
       ↓
URP camera color target
```

CPU 側の `CausticRayQueryTest` は scene object と GPU resource のライフサイクル、transform/geometry の変更検出、kernel dispatch、描画バッファの接続を担当します。面積計算、ray query、境界分割、indirect draw の引数生成は GPU 側で行います。

## GPU 処理パイプライン

1. **source grid を生成する**
   `Source Size` と `Cell Size` から最大 512 x 512 cells のグリッドを作ります。頂点は共有され、各 cell は 2 枚の三角形になります。height field があれば、各頂点の高さと勾配から source normal を作ります。

2. **height field を更新する**
   `RadialSineHeightField` と `TextureHeightField` は解析式またはテクスチャから height map を生成します。`WaveEquationHeightField` は previous/current height map から離散ラプラシアンを計算し、波源を加えて next map を生成します。対応する場合、`BuildSourceVerticesFromHeightMap` が差分から GPU 上で頂点位置と法線を生成します。

3. **receiver を acceleration structure に登録する**
   receiver ごとに position/index buffer と primitive offset を管理します。通常の mesh は static geometry として扱い、`ICausticReceiverGeometry` を実装する geometry は dynamic geometry として更新します。各 receiver の primitive normal は `BuildReceiverPrimitiveNormals` で生成し、ray hit の instance ID と primitive index から参照します。

4. **source vertex から屈折レイを追跡する**
   `TraceVertices` が source vertex ごとに inline ray query を実行します。レイ方向は source normal、入射方向、屈折率から `refract` で求めます。ヒットした receiver の位置、距離、instance ID、primitive index、receiver normal を `RayHit` として保存します。

5. **投影三角形と強度を計算する**
   `BuildTriangles` は 3 つの `RayHit` が同一 receiver に属する場合、source 側の入射面積 `A_in` と receiver 側の投影面積 `A_out` を計算します。基本強度は次式です。

   ```text
   intensity = A_in / max(A_out, Min Receiver Area)
   ```

   `Intensity Scale` を乗じた値を投影結果へ格納します。無効なヒット、裏向きの面、面積が 0 の三角形は破棄します。

6. **receiver 境界を検出・分割する**
   `MarkBoundaryEdges` は、異なる receiver にまたがるエッジ、または receiver normal の差が閾値を超えるエッジをマークします。`TraceEdgeMidpoints` で境界エッジの中点を再追跡し、`BuildProjectedTriangles` がマークされたエッジに沿って三角形を分割します。これにより、1 枚の source triangle が receiver の不連続な領域をまたいで描画されることを避けます。

7. **indirect draw する**
   `BuildOutputArgs` が生成された投影三角形数から indirect draw 引数を作ります。`ProjectedCausticTriangle.shader` は hit buffer と projected index/result buffer を参照し、receiver normal 方向へ `Surface Offset` だけ位置をずらして描画します。

## コンポーネント責務

| コンポーネント | 責務 |
|---|---|
| `CausticRayQueryTest` | source、receiver、AS、buffer、kernel、material の統合と dispatch |
| `CausticHeightField` | source の高さ、勾配、時間変化、GPU height map の抽象化 |
| `RadialSineHeightField` | 放射状 sine 波の生成 |
| `TextureHeightField` | テクスチャベースの高さ場生成 |
| `WaveEquationHeightField` | GPU 波動シミュレーション |
| `CausticReceiverGeometry` | 動的 receiver geometry の共通契約 |
| `NoiseGridReceiverGeometry` | GPU ノイズ変形グリッドの mesh/buffer 更新 |
| `CausticRendererFeature` | URP RenderGraph への描画 pass の追加 |
| `ProjectedCausticTriangle.shader` | 投影 caustics の blend、色、強度、境界表示 |
| `SourceGridOutline.shader` | source grid / source surface の可視化 |

## Receiver の更新

receiver は次の 2 経路で扱います。

- **static mesh**: `MeshFilter.sharedMesh` の position/index を buffer 化し、transform 変更時に AS の instance transform を更新する
- **dynamic geometry**: `PrepareMesh` で runtime mesh を用意し、`DispatchGeometry` で position buffer と mesh vertex buffer を更新する。topology が変化した場合は receiver resource と AS を再構築する

`NoiseGridReceiverGeometry` は `(cellsPerAxis + 1)^2` 頂点の XZ または XY グリッドを作り、`DeformNoiseGrid` で base position からノイズ変形します。アニメーション中は毎フレーム geometry を更新します。

## URP 描画経路

`CausticRendererFeature` は `AfterRenderingSkybox` に RenderGraph pass を追加します。

1. projected caustics を camera color target へ描画
2. その時点の color target を `Caustics Background Texture` へコピー
3. source surface を描画し、必要に応じて背景テクスチャを参照

projected caustics は `Additive` または `ModulatedAdditive` を選べます。後者は destination color を source blend に使い、背景に応じて caustics の見え方を変えます。

## 検証機能

`Enable Validation` を有効にすると、GPU から以下を非同期 readback して CPU 側で検証・ログ出力します。

- `RayHit` の妥当性
- source/receiver 面積と intensity
- boundary edge の数と理由
- 出力された projected triangle 数

validation 有効時は GPU height field 経路を使わず、CPU `Evaluate` 経路を基準に検証します。

## 制約と注意点

- 入射光は現在 directional light のみを対象とする
- receiver mesh は 1 submesh を要求する
- source grid の cell 数は各軸 512 が上限
- 境界分割の出力数は `Max Additional Triangles` の設定に依存する
- receiver が source ray の経路にない場合、その source vertex/triangle は無効になる
- これは投影三角形による近似であり、caustics のエネルギー保存や厚みのある媒質を完全には扱わない
- DXR、inline ray query、Compute Shader、URP RenderGraph が利用できる GPU/API が必要

大きな設計変更は [CHANGELOG.md](../CHANGELOG.md) に記録します。
