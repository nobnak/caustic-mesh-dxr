# 変更履歴

大きな機能追加、設計変更、公開挙動の変更を記録します。
内容は `main` のコミット履歴と、そこへ統合された開発ブランチをもとに整理しています。

## 2026-10-06

### Added

- 自動ベンチマークおよび比較キャプチャ生成機能 (`CausticBenchmarkRunner`) を追加
- 技術論文・プレプリント（日本語・英語）および実機検証キャプチャを公開（`docs/preprint.md`, `docs/preprint_ja.md`）
- 日本語版 README (`README.ja.md`) を追加

### Changed

- `README.md` を英語表記に統一し、先頭に英語/日本語の言語切り替えリンクを設置
- プレプリント本文へのリンク、境界テアリング解消の視覚的比較、パイプライン構成図、RTX 5070 ベンチマークを掲載
- `CausticRayQueryTest` の非推奨 API を `RayTracingMode` に更新し、外部評価用の `ForceEvaluateAndDispatch` を追加

## 2026-07-22

### Added

- ノイズで変形する動的 caustics receiver geometry を追加
- 静的 receiver の回転テストコンポーネントを追加

### Changed

- receiver の primitive normal 生成処理を共通化

関連ブランチ: `codex/dynamic-receiver-geometry`, `codex/static-receiver-rotation`

## 2026-07-21

### Added

- URP で caustic source surface を描画する Renderer Feature を追加

### Changed

- 投影された caustic intensity を補間するよう変更

関連ブランチ: `codex/source-surface-display`

## 2026-07-20

### Added

- GPU height field simulation を追加
- ノイズで wave source を移動できるよう変更
- caustics blending に変調を追加

関連ブランチ: `codex/height-field-input`, `codex/caustic-blend-mode`

## 2026-07-19

### Added

- カメラ姿勢の記録機能を追加
- 複数の caustics receiver に対応

### Changed

- サンプルシーンを更新
- receiver の共有エッジにまたがる caustics を分割
- receiver の鋭い境界を検出
- 共有 render normal に沿って caustics の位置をオフセット

関連ブランチ: `codex/multiple-caustic-receivers`, `codex/receiver-normal-render-offset`, `codex/receiver-boundary-diagnostics`, `codex/receiver-boundary-subdivision`

## 2026-07-18

### Added

- 可変 radial caustics grid に対応
- GPU 上で source grid のアウトラインを描画

関連ブランチ: `codex/variable-radial-wave-grid`, `codex/gpu-source-grid-outline`

## 2026-07-17

### Added

- 最小構成の DXR caustics projection を追加
- caustics input を indexed grid に拡張
- directional sine caustics input を追加

### Changed

- caustics scene object の名前を整理

関連ブランチ: `feature/minimal-dxr-caustics`, `feature/indexed-grid-caustics`, `feature/sine-deformed-grid-caustics`

## 2026-07-16

### Added

- Unity プロジェクトを作成
- プロジェクトの初期ファイルを追加

## ブランチ履歴

開発ブランチは、主に以下の機能単位で作成・統合されています。

| ブランチ | 内容 |
|---|---|
| `feature/minimal-dxr-caustics` | 最小 DXR caustics projection |
| `feature/indexed-grid-caustics` | indexed grid 入力 |
| `feature/sine-deformed-grid-caustics` | directional sine 入力 |
| `codex/variable-radial-wave-grid` | 可変 radial grid |
| `codex/gpu-source-grid-outline` | source grid outline |
| `codex/multiple-caustic-receivers` | 複数 receiver |
| `codex/receiver-normal-render-offset` | render normal によるオフセット |
| `codex/receiver-boundary-diagnostics` | receiver 境界検出 |
| `codex/receiver-boundary-subdivision` | receiver 境界での分割 |
| `codex/caustic-blend-mode` | caustics blending |
| `codex/height-field-input` | GPU height field / wave source |
| `codex/source-surface-display` | source surface の URP 描画 |
| `codex/dynamic-receiver-geometry` | 動的 receiver geometry |
| `codex/static-receiver-rotation` | 静的 receiver 回転テスト |

## 今後の記録形式

```md
## YYYY-MM-DD

### Added
- 追加した機能

### Changed
- 変更した設計や挙動

### Fixed
- 修正した問題

関連ブランチ: `branch-name`
```
