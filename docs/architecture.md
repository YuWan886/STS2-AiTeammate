# AiTeammate 运行时架构

> 面向开发者的运行期地图：启动链路、多版本 bundle、管理器、AI 身份、线程、存储、本地化、设置。
> 子系统内部细节见文末索引，各自指向 `docs/` 下的专题文档。

## 1. 启动链路

**mod 在 `NGame._Ready` 之后才加载。** 游戏从异步的 `OneTimeInitialization.ExecuteVeryEarly`（在 `NGame._EnterTree` → `GameStartupWrapper` 里 await）加载 mod，此时主场景已经进树并跑完 `_Ready`。所以 `NGamePatch` 打在 `NGame._Ready` 上的 postfix 在真实启动里**永远不会触发**——管理器、solo 看门狗、弹幕层、dsh 桥都曾因此处于死状态（重启后点 Continue 没有 AI 在爬塔）。

`Bootstrap/ModBootstrap.cs` 补上这一环：

1. `ModBootstrap.Start()`（mod 初始化时调用一次）用 `CallDeferred(AddChild)` 在场景树 root 下挂 `ModBootstrapNode`（mod 初始化可能还在场景树自己的 setup 阶段）。
2. 每 **0.5s** 轮询 `NGame.Instance`，可用后调用一次 `NGamePatch.AfterGameReady(game)`（等同那个触发不了的 postfix）。抛异常就下个 tick 再试：LocManager / ModelDb 要过几帧才在线。
3. 就绪后每 **5s** 调 `ContentCacheService.EnsureLoaded()` 补建内容缓存，最多重试 **120s**，之后告警并停止轮询。

**新增的运行时初始化挂 `AfterGameReady`，不要挂 `NGame._Ready`。** 两条路径（真实启动的 bootstrap、设置页按钮触发的 `RefreshChatUi`）都会经由它。

`MainFile.Initialize`（content 程序集的 `[ModInitializer]`，由 loader 反射调用）顺序：

1. `RitsuLibFramework.BeginModDataRegistration(ModId)` → `AiSettingsStore.RegisterData()` + `PersonalityStore.RegisterData()`（`settings` / `personalities` 两个 Global 槽位）。
2. `AiSettingsStore.Migrate()`：改过的默认值进不了旧 `settings.json`，这里一次性迁移并升 `AiSettings.CurrentSchemaVersion`（当前 **2**）。
3. `AiSettingsPage.Register()`：内置人格文本要本地化，seed 延后到 LocManager 存在时。
4. `SafePatchInstaller.InstallAll(new Harmony(ModId))`：逐个装补丁，目标不存在只告警（替代 `Harmony.PatchAll`，见 `docs/patches.md`）。
5. `ModBootstrap.Start()`。

设置和补丁各自 try/catch：设置初始化失败 mod 仍会加载，只是设置不可用。

## 2. 多版本 bundle 与 loader

部署后的目录：

```
mods/AiTeammate/
├── AiTeammate.dll                  # loader（AssemblyName=AiTeammate）
├── AiTeammate.json                 # 清单：has_pck/has_dll，依赖 STS2-RitsuLib
├── AiTeammate.pck                  # localization JSON + mod 封面图
├── ai-teammate-variants.manifest
└── lib/<ver>/AiTeammate.Content.dll + compat-target.txt
```

`AiTeammateCode/Loader/LoaderMain.cs` 的初始化：

1. 先装 **ModTypes 桥**（`ReflectionHelper.ModTypes` getter postfix）：游戏只返回 loader 程序集的类型，不桥接的话 `ModelDb` / `ActionTypes` / `MessageTypes` 看不到 content 变体里的模型（`AiNicknameMessage` 这类 mod `INetMessage` 也靠它被发现）。
2. `LoaderHostVersion` 判定宿主版本，`LoaderVariantBundle.PickVariant` 选 **compat target ≤ 宿主版本里最新的** 变体（所以 0.113.1 宿主跑 0.113.1 构建）；如果没有任何变体 ≤ 宿主，会 best-effort 回落到 bundle 里最新的那个（老宿主可能因此跑到新 API 的变体，所以对外发布时 `min_game_version` 要和实际随包的变体对齐）。
3. `LoadFromAssemblyPath` 到 loader 所在 ALC，反射调用 content 的 `[ModInitializer]`。

`HarmonyPatchAllTypeLoadGuard`（loader 侧）给 `HarmonyMethodExtensions.GetFromType` 装 finalizer，把 `TypeLoadException` 降级成“跳过该 patch 类 + 告警”，避免某个版本缺 API 时整个 `PatchAll` 失败。content 侧仍走 `SafePatchInstaller`。

版本常量由 `AiTeammate.csproj` 按 `EffectiveVariantTarget` 注入：`STS2_API_0_111_OR_LATER`（除 0.107.1 以外全部）、`STS2_API_0_113_OR_LATER`（>= 0.113.0）。**业务代码不许用 `#if`**，版本差异全部收敛在 `AiTeammateCode/Compat/`（`isModded` 也在 `Compat/GameCompat.cs`）。唯一的例外是 `UI/LobbyAiCharacterPanel.cs` 里 `ICharacterSelectButtonDelegate.SelectCharacter` 的签名——接口本身在 0.113 变了。

0.113.1 beta 新增 `GameMode.Dread` / `Blitz` 和它们的 lobby 界面（`NDreadScreen` / `NBlitzScreen`）：这些类型按**类型名/反射**匹配（`AutoLobbyAiPatch`、`LobbyAiCharacterPatch`、`AgentMenuSupport.TryFillNewModeLobby`），所以同一份代码仍能对更老的快照编译。0.113 的 shim 在 `Compat/GameCompat.cs`（`GameMode.Custom` 移除、`Player` 版 `StartNewSingleplayerRun`、`OnTryPurchaseUnsynchronized`、`TryUndoReadyToEndTurn`、`IsCustom` lobby 标志、`SmithCount` 移除、`PlayerChanged` 参数个数、拆分的大厅进阶上限、`FastHost` 的 `isCustom`）。

## 3. AiTeammateManager

`AiTeammateManager` 是运行时单例，从 `NGamePatch.AfterGameReady` 初始化。`Initialize()` 幂等、`TryInitialize()` 不抛异常，所以任何钩子（读档、`NRun._Ready`、solo 监督器）都可以安全请求初始化。它持有后端、内容缓存、人格、决策/聊天服务、执行器、日志与 `PlayerDirectiveManager`，并跟踪 `AiPlayer`。

读档后必须重跑 `DetectAndBindAiPlayer()`：新 `RunState` / `Player` 对象意味着缓存引用过期，过期引用会让事件与地图投票抛 `IndexOutOfRange`（`GetPlayerSlotIndex` 按引用比较），AI 读档后直接冻住。

## 4. AI 身份与显示名

- NetId 区间 **`0xA1AA_A000`–`0xA1AA_FFFF`**（`Compat/AiIdentity.cs`）。AI 没有网络对端：同步补丁跳过它，大厅补丁注入/标记它。
- `Compat/AiNames` 是唯一的名字解析器：昵称取 `AiSettings.AiNickname`（Global，持久化），为空则用当前人格名。
- `PlayerNamePatches` 给 `PlatformUtil.GetPlayerNameRaw` 打 postfix，覆盖 nameplate、tooltip、能力/休息处文本和 `Creature.Name`；聊天发送者、prompt 自我介绍、角色选择卡片、聊天标题栏、直播弹幕发送者都用同一个解析器。
- 改名来源：设置页（`AI Identity` 分类）、AI 自己（`set_nickname` 决策工具或聊天 JSON 的可选 `nickname` 字段）、dsh（`sts2_set_nickname`）。
- `Compat/AiNicknameSync` 用 `AiNicknameMessage`（mod `INetMessage`）广播：host 在 AI 入队时发一次，其它 peer 存下并刷新 nameplate。消息类在 content 程序集，因此 loader 的 ModTypes 桥不能省。

## 5. 线程模型

- HTTP 在线程池跑；所有 Godot / 游戏状态的**读与写**都经 `Compat/MainThread`（`Post` 发后不管，`Invoke`/`Run` 阻塞 worker 直到主线程执行）。不要随手捕获 `SynchronizationContext.Current`。
- `LocManager.SmartFormat` 用共享对象池，**只能在主线程格式化**：worker 线程格式化卡牌/遗物描述会与 UI 冲突（`Collection was modified`、`Format string is missing a closing brace`）。`PromptTextHelper` 把 `GetFormattedText` / `GetDescriptionForPile` 包进 `MainThread.Invoke`；战斗 prompt 在 `DecisionService.DecideCombatAsync` 里一次主线程 hop 内整体构建。
- solo 自动化由 `Solo/AiFlowControl` 控制：聊天消息打断 AI，聊天面板 ⏸ 切换粘性暂停，暂停中的战斗**保持回合不结束**，直到玩家恢复。

## 6. 存储

| 路径 | 内容 |
|---|---|
| `user://steam/<id>/mod_data/AiTeammate/settings.json` | Global 设置（`AiSettingsStore`，schema v2） |
| `user://steam/<id>/mod_data/AiTeammate/personalities.json` | 人格档案（`PersonalityStore`） |
| `user://ai_teammate/memories/<profileId>.json` | 人格长期记忆 |
| `user://ai_teammate/cache/content_index.json` / `content_full.json` | 内容索引，schema **v4**，按 `sts2.dll` MVID 失效 |
| `user://ai_teammate/voice/<sha1>.mp3` | TTS 语音缓存（按文件数/体积 LRU） |
| `user://ai_teammate/logs/session.log` + `ai_traffic.log` | 会话日志 / 流量日志（轮转，保留份数可配） |

要点：

- 目录统一走 `ModPaths.EnsureDir`（递归创建）。Godot 的 `MakeDirAbsolute` 不会建父目录，早期版本因此静默没写出缓存、日志和记忆。
- `settings.json` 明文保存 LLM API key 与 Bilibili `SESSDATA` / `bili_jct`（与游戏自身一致）；设置页从不回显这些字段。
- 缓存 schema v4 把卡牌费用从印刷 `Canonical` 换成了升级修正后的 `GetWithModifiers` 值。`AutoRefreshCache` 关闭时保留旧缓存并告警。

## 7. 本地化

- 所有玩家可见文本走 `AiLoc`（`Localization/AiLoc.cs`）。表：`AiTeammate/localization/eng.json` + `zhs.json`，打包进 `AiTeammate.pck`，由 RitsuLib `I18N` 加载；非中英 locale 回落英文。
- key 前缀：`chat.` / `charselect.` / `settings.` / `toast.` / `system.` / `solo.` / `live.` / `backend.` / `prompt.` / `personality.<id>.*`。
- 普通字符串用 `AiLoc.T(key, englishFallback, ("Name", value))`，设置项用 `AiLoc.Settings(key, englishFallback)`。**不要硬编码玩家可见文本。**
- AI 输出语言跟游戏 locale（`prompt.system.output_language`）；内部决策日志/prompt 脚手架可以留中文（LLM 读得懂）。

## 8. 设置页结构

设置全部是 Global scope（`AiSettings`），一字段一个 `ModSettingsValueBinding`；服务在使用点读 `AiSettingsStore.Current`，`BackendManager.SyncFromSettings()` 按指纹变化重建 HTTP 后端。

`AiSettingsPage.Register()` 注册一个 landing page（`AiTeammate`，只有分类列表）加每类一个子页：`engine` / `decision` / `personality`（人格 + AI 昵称）/ `chat` / `voice` / `live` / `solo` / `agent` / `maintenance`，经 `RegisterCategoryPage(...).AsChildOf(ModId)` 挂载并带排序。每个分类的正文在各自的 `Configure<Name>(ModSettingsSectionBuilder)` 里，单页不超过约 30 行，某一页构建失败只记日志、不影响整个设置 UI。

常被引用的默认值：`DecisionTimeoutMs` 20s、`NonCombatDecisionTimeoutMs` 30s、`CombatTurnBudgetSeconds` 45s、`ChatTimeoutMs` 10s、`EnablePileDetail`/`EnableReadTools`/`EnableOutOfCombatReadTools` 开、`EnableScreenshotTool` 关、`EnableAgentBridge` 关、`AgentBridgePort` 26333、`AgentFallbackSeconds` 30、`EnableSoloClimb` 关、`AutoRefreshCache` 开、`EnableTrafficLog` 关。

## 9. 子系统索引

| 子系统 | 位置 | 说明 |
|---|---|---|
| Backend | `Backend/` | `IAiBackend` + DeepSeek / OpenAI / Ollama；`BackendFactory` 也供设置页“测试连接”使用。无流式 API。`ModelListClient` 拉 `GET /models`（Ollama `/api/tags`，回落 `/v1/models`），`ModelCatalog` 按后端缓存结果喂设置页下拉框。 |
| Decision | `Decision/` | 战斗/非战斗决策、执行器、校验器、read tools。见 [decision-engine.md](decision-engine.md)。 |
| Context | `Context/` | `ConversationContextManager`（跨回合记忆 + run prelude）与 `PromptSnapshot`（只给变化量）。prompt 只陈述事实，选择由人格驱动。 |
| Personality | `Personality/` | `PersonalityStore` / `PersonalityProfile` / `PersonalityPromptBuilder` / `MemoryManager` / `RunReviewService`。内置 tactician / aggressive / cautious / chaos-seeker；游戏语言变化时重播内置文本，自定义档案不动。`RunReviewService` 每局结束写一条 `RunSummary` 记忆（放弃、dsh 驱动、关掉成长或长期记忆时跳过；按 run 起始时间 + seed 去重，因为胜利路径会调两次 `OnEnded`）。 |
| Cache | `Cache/` | `ContentCacheService` + `*CacheGenerator` + `ContentQueryService`，启动预生成卡牌/遗物/药水/附魔索引，避免运行时查 DB。 |
| Chat | `Chat/` | `ChatManager` / `ChatMessage`。面板即 transcript：玩家自己的话、AI 回复、系统提示、错误都进面板，面板晚建时回放历史；语音气泡（`EnableSpeechBubble`）只是 AI 行的额外表现，绝不能是唯一落点。后端请求失败会显示一条带原因的 ⚠️ 行。`ChatMessage.Source` 记录来源，语音层据此决定读不读。 |
| Voice | `Voice/` | MiniMax TTS。见 [minimax-tts.md](minimax-tts.md)。 |
| Live | `Live/` | B 站直播弹幕。见 [live-danmaku.md](live-danmaku.md)。 |
| Solo | `Solo/` | 单人“AI 独自爬塔”。见 [solo-climb.md](solo-climb.md)。 |
| Agent (dsh) | `Agent/` | 本地 TCP 桥 + 决策接管。见 [dsh-agent-bridge.md](dsh-agent-bridge.md)。 |
| Teamwork | `Teamwork/PlayerDirectiveManager.cs` | 玩家对 AI 的临时指令（如“先打小怪”“留着药水”），随决策 prompt 下发并在满足后标记完成。 |
| UI | `UI/LobbyAiCharacterPanel.cs` | 非标准大厅（daily/custom/Dread/Blitz）里的 AI 队友卡片与全屏选人网格。 |
| Patches | `Patches/` | 全部 Harmony 补丁及目的见 [patches.md](patches.md)。 |
| Logging | `AiLogger`, `AiTrafficLogger`（根目录） | append-only 句柄（seek 到末尾）+ 缓冲 `Flush`；流量日志默认关；保留份数取设置。 |
