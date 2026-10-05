# Caustic Mesh DXR - Benchmark & Verification Report

- **Date**: 2026-10-05 23:04:17
- **GPU**: NVIDIA GeForce RTX 5070 (Direct3D12)
- **VRAM**: 11943 MB
- **Unity**: 6000.3.25f1

## 1. Visual Verification & Comparison Captures

| Case | Description | Output Image |
|---|---|---|
### Camera Pose 02 (Front View)

| Case | Description | Output Image |
|---|---|---|
| Subdivision OFF | Baseline without boundary handling (stretched / flying triangles) | `[caustic_pose02_subdivision_off.png](Captures\caustic_pose02_subdivision_off.png)` |
| Subdivision ON | Proposed adaptive edge subdivision & boundary culling | `[caustic_pose02_subdivision_on.png](Captures\caustic_pose02_subdivision_on.png)` |
| Boundary Highlight | Detected edge discontinuities colored in magenta | `[caustic_pose02_boundary_highlight.png](Captures\caustic_pose02_boundary_highlight.png)` |

### Camera Pose 03 (Close-up Diagonal)

| Case | Description | Output Image |
|---|---|---|
| Subdivision OFF | Baseline without boundary handling (stretched / flying triangles) | `[caustic_pose03_subdivision_off.png](Captures\caustic_pose03_subdivision_off.png)` |
| Subdivision ON | Proposed adaptive edge subdivision & boundary culling | `[caustic_pose03_subdivision_on.png](Captures\caustic_pose03_subdivision_on.png)` |
| Boundary Highlight | Detected edge discontinuities colored in magenta | `[caustic_pose03_boundary_highlight.png](Captures\caustic_pose03_boundary_highlight.png)` |

## 2. Grid Resolution & Performance Benchmark

| Grid Cells | Source Resolution | Total Triangles | Avg Frame Time (ms) | Approx FPS |
|---|---|---|---|---|
| 16 x 16 | CellSize = 0.2500 | 512 | 1.03 ms | 968.1 FPS |
| 32 x 32 | CellSize = 0.1250 | 2,048 | 1.26 ms | 790.6 FPS |
| 64 x 64 | CellSize = 0.0625 | 8,192 | 1.63 ms | 613.6 FPS |
| 128 x 128 | CellSize = 0.0313 | 32,768 | 1.43 ms | 699.4 FPS |

