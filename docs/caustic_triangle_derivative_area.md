# 投影三角形の微分から面積・照度を求める理論と仕組み

水面やレンズを透過した光線が受光面に集まる現象（コースティクス）において、**「投影された三角形の微分（エッジベクトル）から面積が求まり、その面積比から光の強さ（照度）が求まる」** という幾何学理論の解説です。

忘れたときに短時間で直感を呼び戻せるよう、数学的背景・物理的意味・GPU 実装（偏微分 `dFdx`/`dFdy` およびメッシュ外積）の対応関係を整理しています。

---

## 1. 10秒で思い出す要点（メンタルモデル）

| 概念 | 数学的な意味 | 幾何・直感 |
|---|---|---|
| **微分（偏微分）** | 接ベクトル $\frac{\partial \mathbf{h}}{\partial u}, \frac{\partial \mathbf{h}}{\partial v}$ | 入力グリッドの微小な一辺が、受光面上でどれだけの**長さと向きを持つ辺（ベクトル）**に変形したか |
| **外積の大きさ** | $\|\mathbf{e}_1 \times \mathbf{e}_2\|$ | その2本の辺が囲む**平行四辺形の面積**（三角形ならその半分 $\frac{1}{2}$） |
| **ヤコビアン行列式** | $\det J = \frac{dA_{\text{out}}}{dA_{\text{in}}}$ | 入射面の微小パッチが受光面上で**何倍に拡大／縮小されたか（面積変化率）** |
| **光束保存則** | $\Phi = E_{\text{in}} A_{\text{in}} = E_{\text{out}} A_{\text{out}}$ | 入射した光のエネルギー総量は消えないため、**面積が縮むほど光線密度（明るさ）が跳ね上がる** |

```text
[水面の微小三角形]                       [受光面の投影三角形]
    x2                                      h2
    / \   面積 A_in                             / \   面積 A_out
   /   \                                       /   \
  x0───x1                                     h0───h1
  e1 = x1 - x0                                e1' = h1 - h0 = (∂h/∂u) Δu
  e2 = x2 - x0                                e2' = h2 - h0 = (∂h/∂v) Δv

  A_in  = 0.5 * ||e1 x e2||                   A_out = 0.5 * ||e1' x e2'||
                                                    = 0.5 * ||∂h/∂u x ∂h/∂v|| Δu Δv

  ===> 局所照度 (Intensity) = A_in / A_out  (光束が狭い面積に集中すると明るくなる)
```

---

## 2. なぜ「微分」から「面積」が分かるのか？（幾何と微積分の仕組み）

### 2.1 2次元から曲面への写像

水面上の座標を $(u, v)$、屈折光線が受光面に到達する 3 次元座標を $\mathbf{h}(u, v) = (x(u,v), y(u,v), z(u,v))^T$ とします。

水面上で直交する微小な増分 $du, dv$ を考えたとき、受光面上の到達点はテイラー展開（1次近似）によって次のように移動します：

$$d\mathbf{h}_u \approx \frac{\partial \mathbf{h}}{\partial u} du, \quad d\mathbf{h}_v \approx \frac{\partial \mathbf{h}}{\partial v} dv$$

ここで現れる偏導関数 $\frac{\partial \mathbf{h}}{\partial u}$ と $\frac{\partial \mathbf{h}}{\partial v}$ は、受光面上における**微小な2辺の接線ベクトル**そのものです。

### 2.2 外積による面積の導出

高校数学・線形代数の基本通り、2 つの 3 次元ベクトル $\mathbf{a}, \mathbf{b}$ が張る平行四辺形の面積は**外積のノルム $\|\mathbf{a} \times \mathbf{b}\|$**、三角形ならその半分です：

$$S_{\text{triangle}} = \frac{1}{2} \|\mathbf{a} \times \mathbf{b}\| = \frac{1}{2} \sqrt{\|\mathbf{a}\|^2 \|\mathbf{b}\|^2 - (\mathbf{a} \cdot \mathbf{b})^2}$$

したがって、微小パッチ $(du, dv)$ が受光面上で囲む微小面積素 $dA_{\text{out}}$ は：

$$dA_{\text{out}} = \left\| \frac{\partial \mathbf{h}}{\partial u} \times \frac{\partial \mathbf{h}}{\partial v} \right\| du \, dv$$

**「写像 $\mathbf{h}$ の偏微分を取る」とは受光面上のエッジベクトルを求めることであり、「その外積を取る」ことで面積が確定します。**

### 2.3 ヤコビ行列（ヤコビアン）との関係

もし受光面が 2D 平面（例: $z = \text{const}$）であれば、写像は $(u, v) \mapsto (x, y)$ の 2 変数関数となり、ヤコビ行列 $J$ は $2 \times 2$ 行列になります：

$$J = \begin{pmatrix} \frac{\partial x}{\partial u} & \frac{\partial x}{\partial v} \\ \frac{\partial y}{\partial u} & \frac{\partial y}{\partial v} \end{pmatrix}$$

このとき、局所的な面積拡大率はヤコビアン行列式の絶対値 $|\det J|$ に一致します：

$$dA_{\text{out}} = |\det J| \, du \, dv = \left| \frac{\partial x}{\partial u}\frac{\partial y}{\partial v} - \frac{\partial x}{\partial v}\frac{\partial y}{\partial u} \right| du \, dv$$

3 次元の任意曲面の場合、この $|\det J|$ を 3 次元空間に一般化したものが接ベクトルの外積ノルム $\left\| \frac{\partial \mathbf{h}}{\partial u} \times \frac{\partial \mathbf{h}}{\partial v} \right\|$（あるいは第 1 基本形式のグラム行列式 $\sqrt{\det(J^T J)}$）です。

---

## 3. なぜ面積比が「明るさ（照度）」になるのか？（光束保存則）

### 3.1 物理的直感

水面に入射する平行光束の放射照度を $E_{\text{in}}$（単位面積あたりの光エネルギー流）とします。

水面の微小領域 $dA_{\text{in}}$ に降り注いだトータルの光エネルギー（光束 $\Phi$）は：

$$d\Phi = E_{\text{in}} \, dA_{\text{in}}$$

界面での反射ロスや媒質の吸収を無視すると、この光束 $d\Phi$ はそのまま受光面の面積 $dA_{\text{out}}$ に到達します。受光面における単位面積あたりの光エネルギー（放射照度 $E_{\text{out}}$）は：

$$E_{\text{out}} = \frac{d\Phi}{dA_{\text{out}}} = E_{\text{in}} \frac{dA_{\text{in}}}{dA_{\text{out}}} = \frac{E_{\text{in}}}{|\det J|}$$

- **光が集光する場合（凸レンズ効果）**:
  受光面積が小さくなる（$dA_{\text{out}} \ll dA_{\text{in}}$）ため、照度 $E_{\text{out}}$ は極めて高くなります。
- **光が発散する場合（凹レンズ効果）**:
  受光面積が広がる（$dA_{\text{out}} > dA_{\text{in}}$）ため、光が薄まり暗くなります。
- **特異点（焦線・Caustic Cusp）**:
  光線が折り重なる点（包絡面）では幾何学的に受光面積が $0$ に潰れ（$\det J = 0$）、幾何光学的には照度が無限大に発散します。

このように、**「受光面の面積を微分で測る」ことの本質は、「光束保存則に基づいてエネルギー密度を逆算する」こと**にあります。

---

## 4. GPU における 2 つの実装アプローチ

「投影した三角形の微分から面積を計算する」手法には、GPU の実装形態として主に 2 通りのアプローチが存在します。両者は表現が異なるだけで、数学的には全く同一の有限差分／微分です。

### アプローチ A: スクリーン空間・偏微分命令（Evan Wallace 2011）

Evan Wallace の WebGL Water では、受光面（プールの底）を 2D テクスチャとしてラスタライズし、フラグメントシェーダー内で GPU 組み込み命令 `dFdx` / `dFdy` を使いました。

```glsl
// フラグメントシェーダー内 (2x2 ピクセルクアッドの差分から自動的に偏微分が求まる)
vec3 dpos_dx = dFdx(hitPosition); // ∂h / ∂x
vec3 dpos_dy = dFdy(hitPosition); // ∂h / ∂y

// 外積の大きさ（または直交成分の積）が受光面積 dA_out
float newArea = length(cross(dpos_dx, dpos_dy)); 
// ※ Wallace の元コードでは簡易的に length(dpos_dx) * length(dpos_dy) で近似

float intensity = oldArea / max(newArea, 0.0001);
```

- **メリット**: ピクセルシェーダー内で完結し、幾何メッシュのトポロジー管理が不要。
- **制限**: 受光面が単一の解析平面やテクスチャマップに制限されやすい。

### アプローチ B: 離散三角形メッシュ・外積（Yuksel 2009 / 本プロジェクト DXR）

水面を規則的な三角形グリッドメッシュ（頂点インデックス）として扱い、各頂点からレイを飛ばして受光面上のヒット座標 $\mathbf{h}_0, \mathbf{h}_1, \mathbf{h}_2$ を得て直接三角形を描画します。

本プロジェクトの [CausticRayQuery.compute](file:///c:/Users/nakata/Repo/caustic-mesh-dxr/Assets/Caustics/Shaders/CausticRayQuery.compute#L306-L327) の実装：

```hlsl
// 水面の 3 頂点 x0, x1, x2 と、受光面のヒット座標 h0, h1, h2
const float3 areaVector = cross(x1 - x0, x2 - x0);
result.incidentArea = 0.5 * max(0, dot(-normalize(_IncidentDirection), areaVector));

// 受光面ヒット点のエッジベクトル（離散微分）の外積
const float3 receiverAreaVector = cross(h1.position - h0.position, h2.position - h0.position);
result.receiverArea = 0.5 * length(receiverAreaVector);

// 面積比から局所放射照度を算出
result.intensity = result.incidentArea / max(result.receiverArea, _MinReceiverArea);
```

- **なぜこれが「微分」なのか？**  
  メッシュのエッジ $(\mathbf{h}_1 - \mathbf{h}_0)$ および $(\mathbf{h}_2 - \mathbf{h}_0)$ は、グリッド幅 $\Delta u, \Delta v$ に対する**前進差分（有限差分近似）**：
  $$\mathbf{h}_1 - \mathbf{h}_0 \approx \frac{\partial \mathbf{h}}{\partial u} \Delta u, \quad \mathbf{h}_2 - \mathbf{h}_0 \approx \frac{\partial \mathbf{h}}{\partial v} \Delta v$$
  に他なりません。グリッドを細かくした極限は Wallace の `dFdx` / `dFdy` と完全に一致します。
- **メリット**:
  1. テクスチャ解像度に依存せず、直接 3D シーンへポリゴンとしてラスタライズできる（無限解像度）。
  2. DXR 1.1 インラインレイクエリと組み合わせることで、任意の凹凸・障害物を持つ 3D メッシュ上でも物理的に正確な面積比が得られる。

---

## 5. 混乱しやすいポイントの Q&A

### Q1. なぜ三角形の面積に $\frac{1}{2}$ が付くのに、強度比 $A_{\text{in}} / A_{\text{out}}$ では相殺されるのか？
- **A**: 入射面も受光面も同一の三角形ペア（または同じ平行四辺形）を基準にしているため、係数 $\frac{1}{2}$ は比率を取ると約分されます。ただし、受光面積がほぼゼロのときのゼロ除算ガード `max(A_out, eps)` や、物理単位としての光束計算では絶対面積として正しく評価しておく必要があります。

### Q2. 投影三角形が裏返った（反転した）場合はどうなる？
- **A**: 光線が交差して焦線を通過すると、頂点の順序が反転（ワインディングが裏返る）します。
  - 外積ベクトル $\mathbf{e}_1 \times \mathbf{e}_2$ の向きが逆（法線が反対向き）になりますが、ノルム $\|\mathbf{e}_1 \times \mathbf{e}_2\|$ は正の面積を与えます。
  - 数学的にはヤコビアン行列式の符号が反転（$\det J < 0$）した状態です。幾何光学では絶対値 $|\det J|$ を用いるため、光のエネルギーとしては反転後も正の明るさとして加算されます。

### Q3. 受光面が斜めに傾いているとき（コサイン減衰）はどう扱われる？
- **A**: レイトレースされたヒット点 $\mathbf{h}_0, \mathbf{h}_1, \mathbf{h}_2$ は受光面の斜面に沿って広がるため、受光面が傾くほどヒット点同士の間隔が伸び、**自動的に $A_{\text{out}}$ が大きくなります**。
  - $A_{\text{out}}$ が大きくなることで比率 $A_{\text{in}} / A_{\text{out}}$ が低下し、**ランベルトの余弦則（Lambert's Cosine Law）による減衰が幾何学的に自動反映**されます。斜面補正を別計算する必要はありません。

---

## 6. 関連コード・ドキュメント

- [CausticRayQuery.compute](file:///c:/Users/nakata/Repo/caustic-mesh-dxr/Assets/Caustics/Shaders/CausticRayQuery.compute#L288-L330): `BuildTriangles` 内の入射面積・受光面積・intensity 算出処理
- [docs/architecture.md](file:///c:/Users/nakata/Repo/caustic-mesh-dxr/docs/architecture.md#L47-L55): パイプラインにおける投影三角形と強度の計算仕様
- [docs/preprint_ja.md](file:///c:/Users/nakata/Repo/caustic-mesh-dxr/docs/preprint_ja.md#L18-L22): 先行研究（Evan Wallace 2011, Yuksel & Keyser 2009）と面積比集光モデルの数式背景
