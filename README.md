# caustic-mesh-dxr

> **Real-Time Mesh-Based Caustics on Dynamic 3D Environments via DXR Inline Ray Queries & Adaptive Edge Subdivision**  
> 屈折メッシュ投影モデルを任意の動的3D環境へ拡張し、幾何境界でのテアリング（Flying Polygons）を完全に解消するリアルタイムGPUパイプライン（Unity 6 / DXR 1.1 / URP）

---

## 📄 Preprint / 技術論文

本プロジェクトの理論的背景、幾何学的テアリング（Flying Polygons）の解消アルゴリズム、および NVIDIA GeForce RTX 5070 でのベンチマーク・視覚的検証をまとめたプレプリント（技術論文）を Markdown 形式で全文公開しています。

- 🇬🇧 **[Technical Paper / Preprint (English)](docs/preprint.md)**
- 🇯🇵 **[プレプリント本文 (日本語版)](docs/preprint_ja.md)**

**著者**: Nakata Nobuyuki (Nobuyuki Nakata)

### 論文概要 (Abstract)
屈折光線メッシュの入射・投影面積比から局所放射照度を直接評価するメッシュベース集光手法（Evan Wallace, 2011/2016; Yuksel & Keyser, 2009）は、物理的な光束（Flux）保存と鮮鋭な集光線を極めて低計算負荷で両立できる優れたアプローチです。しかし従来の実装は受光面を解析的な平面や球、あるいは単一ハイトフィールドに制限しており、任意の複数オブジェクトや段差が存在する実用的な3Dシーンへ一般化すると、**境界を跨ぐ際にメッシュが空中に引き裂かれて橋渡し状に伸びる致命的な幾何学的テアリング（Flying Polygons）** が発生していました。

本研究では、DirectX Raytracing (DXR) 1.1 のインラインレイクエリから受光面インスタンスIDおよび法線を抽出・分類し、エッジ単位の適応的中点細分割と境界跨ぎサブ三角形のカリング（局所的保守近似）を実行するGPU完結型パイプラインを提案します。連続面における完全なヤコビアン光束保存と、不連続境界における幾何学的テアリングの完全排除を両立させ、追加レイクエリを境界周囲長スケール（$O(\partial \Omega)$）に抑えることで、**RTX 5070 上で 1.03〜1.43 ms（>600 FPS）で動作する破綻のないリアルタイム直接描画**を実現しました。

---

## 🖼️ 境界テアリング解消の視覚的効果 (Visual Comparison)

境界適応細分割を適用しないベースライン（Wallace 手法の単純3D拡張）と、提案手法（適応細分割＋境界カリング）の比較です（動的波面シミュレーション・Camera Pose 03 キャプチャ）。

### 全体パースペクティブ比較 (Full Perspective View)
| (a) 境界細分割 OFF (Baseline: 単純拡張) | (b) 境界細分割 ON (Proposed: 提案手法) | (c) 境界検出ハイライト (Debug) |
|:---:|:---:|:---:|
| [![Subdivision OFF](Captures/caustic_pose03_subdivision_off.png)](Captures/caustic_pose03_subdivision_off.png) | [![Subdivision ON](Captures/caustic_pose03_subdivision_on.png)](Captures/caustic_pose03_subdivision_on.png) | [![Boundary Highlight](Captures/caustic_pose03_boundary_highlight.png)](Captures/caustic_pose03_boundary_highlight.png) |

### 境界拡大インセット (Cropped Insets: 中央浮遊キューブ上辺および床面境界)
| (a) 拡大: 境界細分割 OFF (Baseline) | (b) 拡大: 境界細分割 ON (Proposed) | (c) 拡大: 境界ハイライト (Debug) |
|:---:|:---:|:---:|
| [![Inset OFF](Captures/caustic_pose03_inset_subdivision_off.png)](Captures/caustic_pose03_inset_subdivision_off.png) | [![Inset ON](Captures/caustic_pose03_inset_subdivision_on.png)](Captures/caustic_pose03_inset_subdivision_on.png) | [![Inset Boundary](Captures/caustic_pose03_inset_boundary_highlight.png)](Captures/caustic_pose03_inset_boundary_highlight.png) |
| **致命的な幾何テアリング**: キューブ上辺と床面の間に巨大な不正三角形が空中に引き伸ばされ、空間を横断して描画を破壊。 | **幾何形状への精密な吸着**: 不連続境界を跨ぐサブ三角形が瞬時にカリングされ、キューブの稜線および床面に沿って集光線が自然に分離・吸着。 | **境界エッジの抽出**: 異なるインスタンス間および法線急変部（$\mathbf{n}_j \cdot \mathbf{n}_k < \cos 30^\circ$）が高精度に識別されている。 |

---

## 🔬 パイプライン構成 (Architecture Pipeline)

```
[入力屈折波面] (GPU Heightfield / 波動方程式 / テクスチャ)
       │
       ▼
 [屈折レイ生成] (Snell's Law: refract(L, n, η))
       │
       ▼
 [DXR 1.1 Inline RayQuery] (TLAS交差: ヒット座標 h, 法線 nr, Instance ID 取得)
       │
       ▼
 [エッジ不連続性検出] (Instance ID 不一致 or 法線急変角 θthresh をアトミック記録)
       │
       ▼
 [適応的中点レイ再追跡] (不連続境界エッジの中点のみ追加 RayQuery: O(∂Ω) スケール)
       │
       ▼
 [トポロジー分割 & カリング] (1〜4 サブ三角形分割, 跨ぎ不正ポリゴンをGPU上で破棄)
       │
       ▼
 [GPU Indirect 直接描画] (DrawProceduralIndirect, 中間テクスチャ不要のベクター解像度)
```

---

## ⚡ 性能ベンチマーク (Performance Benchmarks)

**測定環境**: NVIDIA GeForce RTX 5070 (VRAM 12 GB, Direct3D 12), Unity 6000.3.25f1, 解像度 $1920 \times 1080$, 波動シミュレーション稼働下

| グリッド解像度 | セルサイズ | 生成三角形数 | 平均フレーム時間 (ms) | 想定 FPS |
|---|---|---|---|---|
| $16 \times 16$ | 0.2500 | 512 | **1.03 ms** | **968.1 FPS** |
| $32 \times 32$ | 0.1250 | 2,048 | **1.26 ms** | **790.6 FPS** |
| $64 \times 64$ | 0.0625 | 8,192 | **1.63 ms** | **613.6 FPS** |
| $128 \times 128$ | 0.0313 | 32,768 | **1.43 ms** | **699.4 FPS** |

細分割とレイ再追跡の対象を境界エッジのみに絞り込んでいるため、計算負荷が面積 $O(N^2)$ ではなく境界周囲長 $O(\partial \Omega)$ にスケールし、高密度グリッドでも安定して 600 FPS 以上の超高速フレームレートを維持します。

---

## 🛠️ 主な機能 (Key Features)

### 1. Caustics Projection
- 長方形の入力面を共有頂点グリッドへ分割
- Directional Light の入射方向と入力面法線からスネルの法則で屈折方向を計算
- DXR 1.1 Top-Level Acceleration Structure (TLAS) に対してインラインレイクエリを実行
- `incidentArea / receiverArea`（ヤコビアン面積比）から物理的な局所放射照度を算出
- 加算合成（Additive）および背景変調加算（Modulated Additive）に対応

### 2. 多様な入力 Height Field
`CausticHeightField` を基底に、入力面の高さと勾配を供給：
- `RadialSineHeightField`: 放射状 sine 波
- `TextureHeightField`: テクスチャからの高さ場
- `WaveEquationHeightField`: GPU 上の波動方程式シミュレーション

### 3. 動的 Receiver Geometry
- 通常の `MeshRenderer` 静的メッシュ
- `CausticReceiverGeometry` による GPU 管理ジオメトリ
- `NoiseGridReceiverGeometry` による GPU ノイズ変形グリッド（リアルタイム変形対応）
- 複数 receiver を 1 つの TLAS に登録し、インスタンス ID による高精度トポロジー判定

### 4. 境界処理とデバッグ可視化
- 異なる receiver 間の境界および急峻な法線変化（$> 30^\circ$）の検出
- 境界エッジの中点レイ追跡による適応的サブメッシュ分割（1〜4分割）
- 境界跨ぎポリゴンの自動カリング
- 受光面境界のマゼンタ色ハイライト表示
- Source grid の outline、lit、refraction 表示
- GPU readback によるレイ結果・面積計算・分割結果の検証

---

## 💻 前提環境 (Prerequisites)

- **Unity**: `6000.3.20f1` 以降 (Universal Render Pipeline `17.3.0`)
- **Graphics API**: DirectX 12 (DXR 1.1 / Tier 1.1 Ray Tracing 対応 GPU)
- **Shader Support**: Compute Shader、Inline Ray Query (`RayQuery<RAY_FLAG_NONE>`) が利用可能な環境

---

## 🚀 クイックスタート (Getting Started)

1. Unity で本リポジトリを開く
2. `Assets/Scenes/SampleScene.unity` を開く
3. シーン上の `CausticRayQueryTest` に Directional Light、Receiver、各シェーダーが割り当てられていることを確認
4. **Play** ボタンを押して実行

### 主要パラメータ (`CausticRayQueryTest` Inspector)

| パラメータ | 説明 |
|---|---|
| `Source Size` / `Cell Size` | 入力グリッドの寸法と解像度 |
| `Height Field` | 入力面の波面タイプ（RadialSine / Texture / WaveEquation） |
| `Transmitted Refractive Index` | 屈折率（水の場合は約 `1.333`） |
| `Receivers` | 集光投影対象のジオメトリリスト |
| `Subdivide Receiver Boundaries` | 境界エッジの適応分割・カリングの有効化（ON/OFF） |
| `Caustic Blend Mode` | Additive または Modulated Additive |
| `Enable Validation` | GPU 結果の CPU readback による検証 |

---

## 📂 ディレクトリ構成 (Repository Layout)

```text
caustic-mesh-dxr/
├─ Assets/Caustics/
│  ├─ Scripts/
│  │  ├─ CausticRayQueryTest.cs          # 全体のリソース管理・GPU dispatch・Indirect Draw
│  │  ├─ CausticHeightField.cs            # height field の抽象インターフェース
│  │  ├─ *HeightField.cs                  # 入力波面の実装（WaveEquation等）
│  │  ├─ CausticReceiverGeometry.cs       # receiver geometry の抽象インターフェース
│  │  ├─ NoiseGridReceiverGeometry.cs     # GPU ノイズ変形 receiver
│  │  ├─ CausticRendererFeature.cs        # URP への描画統合
│  │  └─ Editor/
│  │     └─ CausticBenchmarkRunner.cs     # 自動ベンチマーク・検証キャプチャランナー
│  └─ Shaders/
│     ├─ CausticRayQuery.compute          # レイクエリ・境界判定・適応細分割コンピュート
│     ├─ NoiseGridReceiver.compute        # receiver ジオメトリ変形シェーダー
│     ├─ ProjectedCausticTriangle.shader  # 投影結果の直接ラスタライズシェーダー
│     └─ SourceGridOutline.shader         # source surface の補助描画
├─ Captures/                              # ベンチマーク・比較検証用キャプチャ画像
└─ docs/
   ├─ preprint.md                         # プレプリント本文 (English Markdown)
   ├─ preprint_ja.md                      # プレプリント本文 (日本語 Markdown)
   ├─ architecture.md                     # 詳細アーキテクチャ・設計仕様
   └─ benchmark_results.md                # 詳細ベンチマーク測定データ
```

---

## 📚 関連ドキュメント (Documentation)

- 🇬🇧 [Technical Paper / Preprint (English)](docs/preprint.md)
- 🇯🇵 [プレプリント本文 (日本語版)](docs/preprint_ja.md)
- 📐 [全体アーキテクチャ・設計詳細 (architecture.md)](docs/architecture.md)
- 📊 [ベンチマーク測定結果 (benchmark_results.md)](docs/benchmark_results.md)
- 📝 [変更履歴 (CHANGELOG.md)](CHANGELOG.md)

