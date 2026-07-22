# caustic-mesh-dxr

## 概要

Unity と DXR を用いた、メッシュベースの caustics 表現の検証・実装プロジェクトです。

入力面をグリッドとしてサンプリングし、各頂点から屈折レイを receiver へ飛ばします。入力側の三角形面積と receiver 側へ投影された三角形面積の比から caustic intensity を求め、投影三角形として URP の画面へ描画します。

このプロジェクトは完成品の caustics ライブラリではなく、以下の技術を比較・検証するためのサンプルです。

- DXR inline ray query による屈折光の投影
- GPU height field による波面入力
- 動的・複数 receiver への投影
- receiver 境界での caustics の破綻を抑える適合分割
- URP RenderGraph からの source surface / projected caustics 描画

## 主な機能

### Caustics projection

- 長方形の入力面を共有頂点グリッドへ分割
- directional light の入射方向と入力面法線から屈折方向を計算
- DXR の acceleration structure に登録した receiver へ inline ray query を実行
- 入力三角形を receiver 上の三角形へ投影
- `incidentArea / receiverArea` を基本強度として描画
- 加算合成と、背景色で変調してから加算する合成に対応

### 入力 height field

`CausticHeightField` を基底に、入力面の高さと勾配を供給します。

- `RadialSineHeightField`: 放射状 sine 波
- `TextureHeightField`: テクスチャからの高さ場
- `WaveEquationHeightField`: GPU 上の波動方程式シミュレーション

height field は CPU の `Evaluate` 経路と、対応する場合の GPU height map 経路を持ちます。GPU 経路では height map から差分勾配を計算し、入力頂点の位置・法線を GPU buffer に直接生成します。

### Receiver geometry

- 通常の `MeshRenderer` の static mesh
- `CausticReceiverGeometry` による GPU 管理 geometry
- `NoiseGridReceiverGeometry` によるノイズ変形グリッド
- receiver の transform 更新と動的 geometry の更新を検出
- 複数 receiver を 1 つの acceleration structure に登録

### 境界処理と可視化

- 異なる receiver 間の境界を検出
- receiver 法線の差が閾値を超える共有エッジを検出
- 境界エッジの中点にもレイを飛ばし、投影三角形を分割
- receiver 境界を色付きで表示
- source grid の outline、lit、refraction 表示
- GPU readback によるレイ結果・面積計算・分割結果の検証

## 前提環境

- Unity `6000.3.20f1`
- Universal Render Pipeline `17.3.0`
- DXR / ray tracing をサポートする GPU と API
- Compute Shader、ray tracing acceleration structure、inline ray query が利用可能な環境

## 実行

1. Unity `6000.3.20f1` でプロジェクトを開く
2. `Assets/Scenes/SampleScene.unity` を開く
3. シーン上の `CausticRayQueryTest` に directional light、receiver、各シェーダーを設定する
4. `Play` を実行する

主要な入力は `CausticRayQueryTest` の Inspector から変更できます。

| 項目 | 内容 |
|---|---|
| `Source Size` / `Cell Size` | 入力グリッドの大きさと解像度 |
| `Height Field` | 入力面の高さ場 |
| `Transmitted Refractive Index` | 屈折率。水の初期値は約 `1.333` |
| `Receivers` | caustics の投影対象 |
| `Subdivide Receiver Boundaries` | 境界エッジの適合分割 |
| `Caustic Blend Mode` | additive または modulated additive |
| `Enable Validation` | GPU 結果の readback 検証 |

## ディレクトリ

```text
Assets/Caustics/
├─ Scripts/
│  ├─ CausticRayQueryTest.cs          # 全体のリソース管理と GPU dispatch
│  ├─ CausticHeightField.cs            # height field の抽象インターフェース
│  ├─ *HeightField.cs                  # 入力面の高さ場実装
│  ├─ CausticReceiverGeometry.cs       # receiver geometry の抽象インターフェース
│  ├─ NoiseGridReceiverGeometry.cs     # GPU ノイズ変形 receiver
│  └─ CausticRendererFeature.cs        # URP への描画統合
└─ Shaders/
   ├─ CausticRayQuery.compute          # height field、ray query、投影分割
   ├─ NoiseGridReceiver.compute        # receiver geometry の変形
   ├─ ProjectedCausticTriangle.shader  # 投影結果の描画
   └─ SourceGridOutline.shader         # source surface の補助描画
```

## 開発資料

- [全体構成・設計](docs/architecture.md)
- [変更履歴](CHANGELOG.md)
