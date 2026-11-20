# 新手引导（Guide）

一次性新手引导：**配表驱动步骤** → **Top 层遮罩挖洞高亮** → **点目标/计时推进** → **进度存服务端玩家属性（断线重连后断点续做）**。
支持「跳过引导」，跳过即把所有步骤标记完成，之后永不再弹。

## 一眼看懂

```
登录 → 进大厅（MainPanel 打开）
        │
        ▼
GuideManager.TryStart()              ① 只有 lobbyReady（MainPanel 已开）才启动，避免在转场时弹遮罩
        │                             ② 进度来自服务端属性 guide_progress（位掩码），取第一个没做完的步骤
        ▼
GuideOverlay.Show(target, tip, ...) ③ AOT 表现层：4 块 Image 拼遮罩（不用 shader）+ 金色描边 + 气泡 + 跳过按钮
        │                             ④ 洞口内不放 raycast 目标 → 里面那块 UI 照常能点
        │                             ⑤ 需要「只让点目标」时在洞口放透明代理 Button
        ▼
点击 → Advance()                     ⑥ 完成一步：mask |= 1<<Id → 本地镜像 → 上报服务端（只前进不回退）
        │
        ▼
全部完成 / 跳过 → overlay.Hide() + Cache.GuidanceDone + GuideFinishedEvent
```

断线：`PlayerOfflineEvent` → 收遮罩、暂停（进度在服务端，不丢）。
重连：服务端推 `player.info.push`（带全量属性）→ 本地与服务端 mask 按位或 → **从第一个未完成步骤继续**。

## 分层与归属（重要）

| 层 | 位置 | 为什么 |
|---|---|---|
| 表现 | `Assets/Scripts/Framework/UIFramework/GuideOverlay.cs`（AOT `GameFramework.UI`） | 遮罩最早可能在首包/热更之后就要用，且**没有美术需求**：运行时自建 UI，不占预制体、不用跑 UI 绑定生成、不用重建资源包 |
| 逻辑 | `Assets/Scripts/Game/Guide/GuideManager.cs`（热更） | 引导规则/顺序要能随热变更 |
| 内容 | `Assets/Config/Excel/Guide.xlsx` + `LocZh/LocEn` | 改步骤/文案 = 改表 + 导出 + 热更，不发版 |
| 权威 | 服务端 `PlayerData.Attributes["guide_progress"]` | 跨设备、断线续做、防改包回退（服务端只做按位或） |

> 为什么不用预制体：引导遮罩是纯色块 + 文本，做成预制体反而要多一条「asmdef/预制体改动 → 必须重建资源包」的红线。
> 要换皮时把 `GuideOverlay` 改成 `Panel` 子类 + 预制体即可，`GuideManager` 的调用接口不用动。

## 配表（Guide.xlsx）

| 列 | 类型 | group | 说明 |
|---|---|---|---|
| `Id` | int | B | 步骤 Id，**同时是位掩码的 bit 号，必须 1~62** |
| `Group` | string | B | 引导组，默认 `newbie_lobby`（`GuideManager.GroupId` 可改） |
| `Order` | int | B | 组内顺序 |
| `TargetPanel` | string | C | 目标面板名（`[UIPanel]` 注册名）；空 = 全屏对话步 |
| `TargetNode` | string | C | 目标节点（短名或相对路径，规则同 `Panel.Get<T>`）；空 = 全屏对话步 |
| `TipKey` | string | C | 文案 key，查 `LocZh/LocEn` |
| `Mask` | `enum:GuideMask` | C | `None`（只出气泡）/`RectHole`（矩形挖洞）/`Full`（全屏遮罩） |
| `Shape` | `enum:GuideShape` | C | 洞形：`Rect`（轴对齐矩形）/`RoundRect`（圆角矩形）/`Circle`（正圆，头像用） |
| `Radius` | int | C | 圆角半径（像素，仅 `RoundRect` 用；`Circle` 忽略，内部按半边长算） |
| `ClickThrough` | bool | C | true = 只允许点目标（洞口放代理；目标本身是 Button 时直接听它的 onClick） |
| `AdvanceOn` | `enum:GuideAdvance` | C | `ClickTarget`/`ClickAny`/`Timer`/`Manual`（`Event` 预留未实现） |
| `AdvanceParam` | string | C | `Timer` 用：秒数（如 `"2"`） |
| `Next` | int | C | 非 0 = 显式跳到该 Id；0 = 按 `Order` 顺延 |
| `SkipAllowed` | bool | C | 这一步能否点「跳过」（当前跳过一次即整组结束） |

当前示例（4 步，目标都是 `MainPanel` 的文本行）：全屏欢迎 → 等级（RoundRect r=10）→ 金币（Rect）→ 背包（RoundRect r=18）。

## 洞形（Shape）怎么实现的

遮罩是「4 块矩形 Image + 形状镂空贴图」拼的，**没有用 shader，也不依赖美术切图**：

| Shape | 做法 | 适合 |
|---|---|---|
| `Rect` | 4 块矩形遮罩直接围出矩形洞（最快路径） | 整行文本、方块按钮 |
| `RoundRect` | 4 块矩形遮罩围住洞的外框 + 一块**运行时生成的圆角镂空贴图**盖在洞上（四角把多余部分补成阴影），描边用生成的「边界内缩色带」贴图 | 圆角卡片、圆角按钮 |
| `Circle` | 同上，但洞先**归一成正方形**（取短边居中），生成时角半径 = 半边长 → 一定是正圆不会变椭圆 | 圆形头像、圆形按钮 |

生成的贴图是「形状内部透明、外部不透明」的 alpha 图：用**圆角矩形 SDF**逐像素算距离，边界外羽化 1~2 像素做抗锯齿；描边是同一份距离场的 `[-厚度, 0]` 色带。贴图按洞的屏幕尺寸生成（单边上限 512 px，超出等比缩小），并按 `形状_宽_高_半径` 缓存复用，`OnDestroy` 里统一销毁。

几何上还有两点是特意处理的：

- **目标矩形用 4 个角取 min/max**，不再假设 `corners[0]` 是左下、`corners[2]` 是右上 —— 节点有旋转/镜像时这两个角在世界坐标里未必是最小/最大，按 0/2 取会导致洞错位甚至退化成全屏遮罩；
- **圆形洞会被夹在屏幕内**：靠近屏幕边缘的头像，正方形洞会自动平移/裁到可视区。


### 加一步 / 加一组

1. `Guide.xlsx` 加行（`Id` 唯一，1~62；同 `Group` 内 `Order` 递增）；
2. `LocZh/LocEn` 加对应 `TipKey` 文案（缺了会显示「缺 Loc 文案」）；
3. `Tools/配置表/导出全部表`；
4. 目标节点要在面板里**真的存在**（否则 4 秒超时会记 Warning 并跳过该步）；
5. 重建资源包（`Tools/YooAsset/2` + `菜单 6` + 清 `yoo/`）—— 配置表在 bundle 里。

## 代码用法

```csharp
// 引导进行中：外部系统可以让步（比如背包打开后才继续）
GameController.Instance.Guide.Advance();

// 查询
GameController.Instance.Guide.IsRunning;      // 返回键拦截在用
GameController.Instance.Guide.IsFinished;     // 走完或跳过
GameController.Instance.Guide.CurrentStepId;
GameController.Instance.Guide.CompletedMask;  // 位掩码（调试/埋点）

// 事件（不依赖 UI，适合打点）
Listen<GuideStartedEvent>(...);  Listen<GuideStepChangedEvent>(...);  Listen<GuideFinishedEvent>(...);
```

服务端：

```csharp
// 协议：guide.progress.set（C→S，客户端上报）→ 服务端按位或写账号属性 → guide.progress.push（S→C 回送权威值）
// 登录 / 重连同步走 player.info.push 的 Attributes 列表，不需要额外协议
// 想给某个账号重置引导：server.Accounts.SetAttribute("dev", "guide_progress", 0)
```

## 已验证（编辑器内）

| 项 | 结果 |
|---|---|
| 进大厅自动启动（全屏欢迎步） | ✅ |
| 挖洞高亮 + 描边 + 气泡 + 跳过按钮 | ✅ |
| 洞口射线命中 `ClickProxy`、洞外命中 `DimBottom`（点外面被挡） | ✅ 用 `EventSystem.RaycastAll` 实测 |
| 逐步推进 → mask 2→6→14→30 → 结束收遮罩 | ✅ |
| 服务端落库（SQLite `attributes_json` = `{"guide_progress":30}`） | ✅ |
| 服务器进程被杀（心跳超时）→ 引导暂停、遮罩收起 | ✅ |
| 重新登录 → 服务端 mask=6 → 从第 3 步续做 | ✅ |
| 换账号不继承上一个账号的进度（本地镜像按账号隔离） | ✅ |
| 洞形 `Rect` / `RoundRect`（r=10、r=18）/ `Circle` 三种都实跑截图确认 | ✅ 见 `Assets/Screenshots/guide-16/17/18/19` |
| 正圆洞：圆心命中 `ClickProxy`，圆外（外接方形的角）被遮罩挡住 | ✅ `EventSystem.RaycastAll` 实测 |

## 已知限制

- **`GuideAdvance.Event` 还没实现**（需要按事件名反射派发）；用到会打 Warning 并按手动处理。
- **只支持单目标矩形/圆角/正圆**：斜切异形、多目标同时高亮、跟着节点旋转的洞都不支持（要做需上自定义 shader 或换成「形状贴图 + stencil」）。
- **洞口点击代理按外接矩形工作**：`Circle`/`RoundRect` 时，形状之外但外接矩形之内的地方点下去**也会推进**（不会漏到下层 UI，只是命中判定比视觉大一点）。
- **目标在 `ScrollRect` 里不会自动滚动**：洞会落在屏幕外，需要的话补「先 ScrollTo 再定位」。
- 遮罩是「整体内缩」的 Top 层子节点，跟随 `PlatformFramework` 的安全区；要顶到刘海需挂到 Canvas 直接子节点。
- 引导中若又打开别的面板，遮罩仍在（点击被挡），但不会自动把那个面板关掉 —— 这是有意的（避免误关玩家正在看的界面）。
- `SkipAllowed` 是「当前步」的开关，但跳过会整组结束；要「这一步不许跳过、后面允许」的精细策略需要扩展成组级开关。
- 洞形贴图是运行时生成的 `Texture2D`（每形状每尺寸一份，上限 512×512），首次出现那一步会有一次毫秒级生成开销；换成预制体换皮时建议改成美术切图 + 9-slice。
- 真机（IL2CPP + 热更 DLL）尚未实测，只在编辑器验证过。
