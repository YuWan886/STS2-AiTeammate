# dsh 接管游戏操作 —— 开发参考

> 读者：维护 AiTeammate 的开发者。
> 游戏侧代码：`gameplay/AiTeammate/AiTeammateCode/Agent/`；独立插件：npm 包 `dsh-sts2`（另仓 `YuWan886/dsh-sts2`，不随 Mod 打包）。
> 本文只描述已实现的行为；与代码冲突时以代码为准，关键处已标注修正。

## 1. 目标与两种接管形态

给 AiTeammate 开一条外部 Agent 接管通道：**dsh（DeepSeek Harness）** 通过本地 TCP 桥接读取完整局面、调用 `sts2_*` 动作工具，像人一样打完一整局。桥接本身是**被动**的（请求/响应），所有"事件"（AI 入局邀请、玩家聊天）都只能靠 dsh 主动轮询获得。

选 dsh 插件而不是 MCP，只因为两件 MCP 给不了的事：工具名是原生的 `sts2_*`（走 dsh 自己的工具流水线/并行/取消策略），以及插件能用 `ctx.agents` 在 agent 空闲时主动排队续玩消息。代价是游戏侧协议得自己定（见第 3 节）。

| | `controlled.kind=local` | `controlled.kind=ai` |
|---|---|---|
| 谁在玩 | dsh 直接操作本地真人玩家 | dsh 当 AI 玩家的大脑，Mod 当手 |
| 触发条件 | 桥接运行 + `AgentBridgeAllowActions` + 至少一个客户端 | 同上，且本局存在 `AiTeammateManager.AiPlayer`（单人爬塔或多人 AI 队友） |
| 工具面 | 出牌/药水/结束回合/`choose`/`select_cards`/`proceed` | 只有 `sts2_decide`；UI 动作在 `AgentActions.Start` 因 `snap.AiControlled` 直接失败（`AgentActions.cs:124`） |
| 选牌 | `NPlayerHand.SelectCards` 被换成 `kind=hand_select` 决策 | AI 自己的选牌也走决策，但只在单人局；多人局各端跑同一份确定性启发式 |
| 多人局语义 | 只驱动自己那份；别人的回合/投票不受影响 | 计划经 `externalBrain` 字段同步，各端执行模式必须一致 |

`AgentBridgeAllowActions=false` 为只读：`sts2_hello/takeover/state/wait/read/note/set_nickname/screenshot/chat_poll/chat_send` 仍可用，其余返回 `Action tools are disabled in the mod settings.`（`AgentToolExecutor.cs:53`）。

## 2. 架构与服务器生命周期

```
Godot 主线程                                桥接线程（Task）
AgentSnapshotBuilder.Capture  <── MainThread.Invoke ── AgentBridgeServer（TcpListener）
AgentActions.Start            ── MainThread.Invoke ──> 127.0.0.1:<port> 行 JSON
AgentDecisionBroker.RequestAsync（挂起，等 sts2_decide）
```

- **只监听** `IPAddress.Loopback`（`AgentBridgeServer.cs:30`）。桥接线程只做 socket I/O、等待与 JSON；任何触碰游戏对象的操作都经 `MainThread.Invoke`。
- **看门狗**：`AgentBridgeService.EnsureAttached` 在 `NGamePatch` 里往 `NGame` 根挂 `AgentBridgeSupervisor`（`NGamePatch.cs:176`），后者每 1 秒调一次 `SuperviseTick`（`AgentBridgeSupervisor.cs:11`）。
- **重启判据**是设置指纹 `{enabled}|{port}|{token}|{allowActions}`；指纹不变就立即返回（`AgentBridgeService.cs:170`）。端口 `Math.Clamp(settings.AgentBridgePort, 1024, 65535)`。
- **启动守卫**：`enabled && NGame.Instance == null` 时不启动，记 `The game is not ready yet.`（`AgentBridgeService.cs:182`）——否则桥接线程会在 `MainThread` 捕获上下文之前碰到游戏代码。
- `Restart()` 清指纹重跑一次（设置页「重启」按钮）；`_ExitTree` 调 `Shutdown()`。
- **客户端计数**：`SuperviseTick` 第一件事是 `NotifyClientCount()`（早于指纹短路），数量变化时在主线程触发 `ClientCountChanged`（聊天标题栏 dsh 徽标靠它）。数量降到 0 时立刻 `AgentDecisionBroker.Abort` + `AgentChatRelay.AbortPending`（`AgentBridgeService.cs:113`）。
- **活动打点**：每个请求 `BeginClientRequest/EndClientRequest` 刷新 `ClientIdleSeconds`；正在走的 `sts2_wait` 也算活着（in-flight 时返回 0）。`sts2_takeover`/`sts2_chat_poll`/`sts2_chat_send` 是**被动调用，不打点**（`AgentBridgeServer.cs:161`），否则轮询会让"静默回退"永不触发。

## 3. 线协议

TCP + UTF-8 行分隔 JSON，一行一个请求，一行一个响应。

```json
{"id": 7, "token": "<可选>", "call": "sts2_play_card", "args": {"card": "0", "target": "0"}}
{"id": 7, "ok": true, "result": {"text": "{\"phase\":\"combat\",...}"}}
{"id": 7, "ok": false, "error": "No run in progress."}
```

- 配了 token 时每个请求都要带，否则 `Unauthorized.`；单行上限 1 MB，超长/非法 JSON 回 `ok:false` 并继续服务（`AgentBridgeServer.cs:15,144`）。
- `result` 可带 `image`（base64）+ `mimeType`，仅截图用。
- **串行/并发**：动作工具过 `SemaphoreSlim(1)`（`AgentToolExecutor.cs:29`），只读工具与 `wait` 可并发。同一连接内多个请求并行处理，写回由 `SemaphoreSlim` 串行。
- **`sts2_hello`** 返回：`name`（`"sts2-agent-bridge"`）、`bridge_version`、`allow_actions`、`agent_driving`、`chat`、`port`、`instructions`（一段自述用法）。
  > 修正：旧文档写 `{name, bridge_version, game_version, allow_actions, port}`；实际**没有** `game_version`，多了 `agent_driving/chat/instructions`（`AgentToolExecutor.cs:94`）。
- **动作工具的等待**：默认 `wait=true`，即动作发起后桥接线程阻塞到"再次可行动且 token 变化"（`wait_seconds` 默认 20，0..50）；`wait=false` 只做短观察（`ObserveMs`：出牌 700ms、默认 900ms）后返回快照。
- `detail=delta` 时 `sts2_state` / `sts2_wait` 只返回与上次相比变化的顶层字段：`{changed:[...], delta:{...}, token, phase, actionable, blocked_by}`。

## 4. 快照契约

`sts2_state` / `sts2_wait` / 动作工具的返回值都是同一份 JSON。

| 字段 | 含义 |
|---|---|
| `schema` | 固定 `1` |
| `token` | 状态指纹：把整份快照剔除所有 `age_seconds` 后序列化，取 SHA-256 前 8 个十六进制字符（小写） |
| `phase` | 见下表 |
| `actionable` | 现在是否轮到本地/AI 玩家，且允许外部 Agent 操作 |
| `blocked_by` | `multiplayer` 之外的原因串：`paused` / `loading` / `enemy_turn` / `turn_ending` / `ai_busy` / `actions_disabled` / `no_local_player` / `screen:<Type>` / `rest_not_ready` … |
| `mode` | `NetGameType` 名字（`Singleplayer` / `Host` / `Client` …） |
| `controlled` | `{kind:"local",name,net_id}` 或 `{kind:"ai",name,character_id,net_id,solo_climb}` |
| `run` / `combat` / `map` / `menu` / `options` / `notes` / `last_action` | 局面各段，见下 |
| `chat` | `{capable,unread,pending[],recent[]}` |
| `ai_name` / `ai_nickname` / `ai_notes` | AI 显示名、配置昵称、本局笔记（`sts2_note`） |
| `decision` / `last_decision` | 挂起决策 / 上一份决策的校验反馈 |
| `wait_timed_out` | 仅 `sts2_wait` 超时时为 true |

**token 的稳定性**：哈希前必须剔除一切自走字段（当前是所有 `age_seconds`，含 `decision.age_seconds`、`chat.pending[].age_seconds`、`last_decision.age_seconds`），否则 token 每 0.1 秒抖一次，`sts2_wait` 会立刻返回（`AgentSnapshot.cs:127-137`）。`sts2_wait` 的唤醒条件是 `actionable && token != after_token`。

### 4.1 phase 一览

| phase | 来源 | 可用动作 |
|---|---|---|
| `menu` | 主菜单/子菜单（`menu.screen` 区分 `main/solo/multiplayer/host/join_friend/other`） | `choose`（按下枚举按钮）、`proceed`（返回上层）；开局/建房/续档仅在主菜单根 |
| `char_select` | 角色选择大厅（单人角色选择、标准多人房） | `choose`（角色头像、`ai_join`/`ai_remove`、Embark）、`proceed` |
| `daily_lobby` / `custom_lobby` | `NDailyRunScreen` / `NCustomRunScreen` | `choose`（Embark/Back、`ai_character`、`modifier`）、`proceed` |
| `dread_lobby` / `blitz_lobby` | 0.113.1+ 的 `NDreadScreen` / `NBlitzScreen`，按类型名反射识别 | 同上（`menu.lobby.ascension`：Dread 固定 10、Blitz 取上限） |
| `ai_decision` | 经纪上挂着决策（AI 决策、local 的 `hand_select`、单人局 AI 的 `card_selection`） | `sts2_decide`；`combat` 段是实时局面 |
| `ai_busy` | AI 正在执行上一条决策 | 无——`sts2_wait` 等下一个决策，`combat` 段可看实时手牌 |
| `paused` | 暂停菜单（capstone submenu，**不在** overlay 栈，需单独识别） | `sts2_resume` / `sts2_save` / `sts2_quit_to_menu` / `sts2_abandon` |
| `combat` | `CombatManager` 进行中 | `play_card`/`play_cards`/`use_potion`/`discard_potion`/`end_turn` |
| `map` | `NMapScreen.IsOpen` | `choose`（另带 `map` 全幕文本） |
| `rewards` | `NRewardsScreen` | `choose` / `claim_rewards` / `proceed` |
| `card_reward` | `NCardRewardSelectionScreen` | `choose`（含 `skip`：按 alternative/skip 按钮） |
| `card_select` | `NCardGridSelectionScreen` 家族 | `select_cards`；单选屏也可 `choose` |
| `card_choice` | `NChooseACardSelectionScreen` | `choose` / `proceed`（跳过） |
| `relic_select` | `NChooseARelicSelection` | `choose` / `proceed`（跳过） |
| `rune_select` | HextechRunes（ARAM: Mayhem）符文选择，纯反射驱动 | `choose`（`PresenterChoose` 立即生效）；无候选时最后一个 `proceed` 选项=确认 |
| `target_select` | `NTargetManager.IsInSelection`（休息处 MEND、部分药水/遗物） | `choose`（最后一个索引=取消） |
| `rest` | `NRestSiteRoom` | `choose`（选项用尽后退化为 `room_proceed`） |
| `shop` | `NMerchantRoom` | `choose` / `proceed`（关货架/离开） |
| `event` | 事件房间 | `choose` / `proceed`（对话/继续页） |
| `bundle_select` | `NChooseABundleSelectionScreen` | `choose`（选包/确认/取消）/ `proceed` |
| `crystal_sphere` | `NCrystalSphereScreen` | `choose`（占卜格子/切工具）/ `proceed` |
| `treasure` / `treasure_relic` | 宝箱房间 / 宝箱遗物选择 | `proceed`（开箱）/ `choose` |
| `room_proceed` | 其他房间，只剩 proceed | `proceed` |
| `game_over` | `NGameOverScreen` | `proceed` |
| `unsupported` | 未识别 overlay，`blocked_by=screen:<Type>` | 无（提示人工处理） |
| `idle` | 加载/动画/敌方回合/无本地玩家 | `sts2_wait` |

> 修正：旧表缺 `rune_select` 与 `treasure_relic`（`AgentSnapshot.cs:866,1312`）。

### 4.2 options 与索引

`options[]` 是**当前界面重新枚举**出来的可点列表，元素统一 `{index, kind, label, ...}`；动作工具只接受这里的 `index`，越界返回错误 + 最新快照。

| kind 组 | 具体值 | `sts2_choose` 的效果 |
|---|---|---|
| 菜单/大厅 | `button` `character` `ai_character` `modifier` `ai_join` `ai_remove` | 按按钮 / 选角色 / 给 AI 换角色 / 切换词条勾选 / 加 AI 并准备 / 移除 AI |
| 战斗 | `card` | 仅 `kind=local` 时有意义（AI 模式 `options` 为空，用 `sts2_decide`） |
| 地图 | `map_node` | `OnMapPointSelectedLocally` |
| 奖励 | `card_reward` `alternative` `<RewardTypeName>` | 选牌 / 跳过按钮 / 领取 |
| 选牌屏 | `card_select` `card_choice` `relic` `rune` `bundle` `bundle_confirm` `bundle_cancel` `crystal_cell` `crystal_tool` | 选择 / 跳过 / 领取 / 选符文 / 选包 / 关水晶球 |
| 事件/篝火/商店 | `event_option` `dialogue` `proceed`、篝火 option id、商店 entry 类型名 | 选项 / 推进对话 / 继续 / 购买 |
| 目标 | 候选者 `kind` + 末尾 `cancel` | hover 后完成选择；`cancel` 等价点取消 |

索引排序：地图按 `MapCoord`；奖励按钮/卡牌 holder/遗物 holder 按 UI 树顺序；商店按 `MerchantInventory.AllEntries` 过滤后顺序；事件/篝火按同步器顺序。

### 4.3 `map` 段

`phase=map` 时顶层多一个 `map` 字符串，内容即内置 AI 选路看到的 `ContextBuilder.DescribeMap` 文本（整幕节点类型、分叉、已访问标记、Boss 行），有长度软上限；要看同一份数据的工具版用 `sts2_read subject=map`。`options` 仍然只是本回合可点的节点，`map` 仅用于规划。

### 4.4 `menu` / 大厅段

`menu` 带 `screen`、`title`、`depth`、`can_start_run`/`can_start_daily`/`can_start_custom`/`can_continue`、`max_ascension`、`characters[]`、`modes[]`、`host_modes[]`、`modifiers[]`（`{id,name,kind}`）；大厅类 phase 还带 `menu.lobby`：`mode`/`host`/`singleplayer`/`players[]`（名字、角色、ready、`is_ai`、`is_local`）/`local`/`ai_in_lobby`/`can_begin`/`ascension`/`seed`/`daily_time`。有 `NModalContainer` 弹窗时 `actionable=false`、`blocked_by=modal` 并在 `notes` 点名类型。

### 4.5 `chat` 段

`{capable, unread, pending[], recent[]}`：`capable`=`AgentChatRelay.ShouldRelay`；`pending` 最多 5 条未回复玩家发言，带 `seq`（`sts2_chat_send.reply_to` 要用的就是它）与 `age_seconds`；`recent` 是最近 4 条普通聊天（`from/text/ai`）。存在未读时 `notes` 直接指向 `chat.pending`。

### 4.6 `combat` 段

`round`/`side`、`player`（`hp/max_hp/block/energy/max_energy/stars/powers[]`）、`hand[]`（`index/name/id/cost`(当前实际费用)`/star_cost/type/upgraded/playable/needs_target/description`）、`enemies[]`（`index/label/hp/max_hp/block/powers/alive/intent`，死亡敌人保留为 `alive:false`）、`piles`（draw/discard/exhaust/play 张数）。AI 模式（`ai_decision`/`ai_busy`）同样带此段，但 `options` 为空——抽到的新牌必须先让 dsh 看见。

## 5. 动作工具

| 工具 | 参数 | 语义要点 |
|---|---|---|
| `sts2_state` | `{detail?}` | 完整快照；`detail=delta` 见 3 节 |
| `sts2_wait` | `{after_token?, timeout_seconds?, detail?}` | 阻塞到可行动且 token 变化；timeout 默认 25、clamp 1..50；超时回 `wait_timed_out` |
| `sts2_play_card` | `{card, target?, wait?, wait_seconds?}` | `card` 为手牌索引或卡名。走 `CardModel.TryManualPlay`（=手牌 UI，`PlayCardAction`，正常扣能量/辉星并触发"打出一张牌"钩子）；`CardCmd.AutoPlay` 是"免费打出"，**绝不能**用于这里 |
| `sts2_play_cards` | `{cards[], end_turn?, wait?, wait_seconds?}` | 一次调用打完整个回合：按序解析手牌、逐步重取快照；遇到非 combat 界面（选牌）或 `actionable=false` 立刻停并在 `steps[]` 说明；`end_turn` 只在无失败/无中断时附加 |
| `sts2_use_potion` | `{potion, target?}` | `UsePotionAction` + `RequestEnqueue`；`AnyTime` 药水战斗外可用，`CombatOnly` 不行 |
| `sts2_discard_potion` | `{potion}` | `DiscardPotionGameAction`（与药水栏 UI 同一条路）；直接 `potion.Discard()` 只改本机、会分叉 |
| `sts2_end_turn` | `{}` | `EndPlayerTurnAction` 经 `ActionQueueSynchronizer.RequestEnqueue`；已 ready 时只回报"等待其他玩家"，绝不反向撤销。**不要**直接调 `SetReadyToEndTurn` |
| `sts2_choose` | `{index, skip?}` | 地图/奖励/选牌奖励/选牌/篝火/事件/商店/遗物/选包/水晶球/目标/符文/**主菜单与大厅** |
| `sts2_select_cards` | `{indices[]}` | `NCardGridSelectionScreen` 多选；校验屏幕自己的 `min..max`，再通过 `_completionSource` 交回 |
| `sts2_proceed` | `{}` | 继续/离开奖励/跳过/开箱/结束界面/确认选包/关水晶球；菜单类 phase 等价"返回上一层" |
| `sts2_claim_rewards` | `{}` | 一键领取不需要决策的奖励（最多 12 轮），遇到 `kind` 含 `CardReward` 的选项即停，留给 `choose` |
| `sts2_decide` | `{decision}` | 回答 `phase=ai_decision`；对象或字符串都收，字符串里允许 Markdown 围栏；必须是含 `actions/end_turn/choice/choices/indices/index/upgrade_indices/thinking/reason/chat_message/deferred_actions/reroll` 之一的 JSON 对象 |
| `sts2_start_run` | `{mode?, character?, ascension?, seed?, modifiers?}` | `standard`/`daily`/`custom`。要求无进行中的局且处于主菜单根；`daily` 忽略其它参数（时间服务器日期 + `dd_MM_yyyy_1p` 种子 + 按游戏规则摇角色/进阶/2 好 1 坏）；`dread`/`blitz` 被明确拒绝并提示改用 `sts2_host` |
| `sts2_host` | `{mode?}` | 干净主菜单根上 `FastHost` 建多人房：`standard`/`daily`/`custom`/`dread`/`blitz`（后两者需 0.113.1+）。非标准局大厅自动加入并准备 AI 队友；标准大厅用角色选择的 `ai_join` |
| `sts2_continue_run` | `{}` | 读档续玩（同样要求主菜单根） |
| `sts2_pause` / `sts2_resume` | `{}` | 开关暂停菜单；单人爬塔时同时 `AiFlowControl.SetManualPaused` 挂起自动化。暂停期间快照 `phase=paused`、不可行动 |
| `sts2_save` | `{}` | 立即写盘；仅单人/房主，客户端被拒 |
| `sts2_quit_to_menu` | `{}` | 先 `SaveRun` 再 `NGame.ReturnToMainMenu` |
| `sts2_abandon` | `{}` | 仅单人/房主；`RunManager.Abandon()` |
| `sts2_quit_app` | `{}` | `NGame.Instance.Quit()`，让游戏自己退（管理员权限下外部杀进程会被拒，自动化测试用它收尾） |
| `sts2_read` | `{subject, pile?, card?, player?, detail?}` | 深读，复用 `AiToolRunner`：`pile/relics/potions/player/run/map/deck_stats/card` |
| `sts2_note` | `{text}` | 本局长期笔记：≤20 条、每条 ≤300 字、去重；写 `user://…/agent/agent_notes.json` 并按运行种子归属，换局自动清空；每条决策提示词会带 `[AI 笔记]`，快照带 `ai_notes` |
| `sts2_set_nickname` | `{name}` | `AiNames.SetNickname`；空串恢复人格名；只读模式也可用 |
| `sts2_screenshot` | `{}` | `ScreenshotTool.Capture`，返回 base64 image；需设置里打开 `EnableScreenshotTool` |
| `sts2_chat_poll` | `{after_seq?, wait_seconds?}` | 长轮询玩家聊天（默认 15s、上限 25s）：`{seq, messages[{seq,from,text,kind,at}], wait_timed_out?}`。**插件内部方法，不注册给模型** |
| `sts2_chat_send` | `{text, reply_to?, directives?, nickname?}` | 在聊天面板发言，见第 8 节 |
| `sts2_hello` / `sts2_takeover` | `{}` | 自检 / 自动接管邀请，见 3、9 节 |

插件侧另有 `sts2_status`（hello + 一次快照的连接自检）与 `sts2_autoplay`（纯插件定时器，不访问游戏），它们不是游戏侧 call。

## 6. 决策经纪（dsh 当 AI 的大脑）

- **唯一拦截点** `DecisionService.RequestWithToolsAsync`：命中 `AgentDecisionBroker.Enabled`（=`AgentBridgeService.ExternalAgentDriving`）时不再请求 LLM 后端，而是把 prompt 挂起等 `sts2_decide`（`DecisionService.cs:156`）。dsh 的答案原样交给 `ResponseParser`，校验/兜底/执行不变。
- **快照形态**：`decision{id,kind,player,net_id,age_seconds,max_wait_seconds,system_prompt,prompt,options,answer_with}`，选牌类额外带 `min_select/max_select/allow_empty/answer_shape`。
- **等待预算** `AgentFallbackSeconds×3`，clamp 60..300 秒（`AgentFallbackSeconds=0` 时直接 300）。同一时刻只挂一个决策，新决策把旧的以 null resolve（`superseded` 日志）。
- **连着 ≠ 在驱动**：`AgentDecisionBroker.AgentWatching` 要求 `Enabled` 且 `ClientIdleSeconds <= AgentFallbackSeconds`（为 0 时用 30 秒窗口）。超过窗口的决策**不挂起**，直接走自带后端（`AgentDecisionBroker.cs:172`）——dsh 恢复调用后立刻回到 dsh 手里。
- **执行模式**：`AiTeammateManager.ExternalBrainActive` 决定"按模型原序出牌、跳过 Phase 3 清空手牌、允许更多决策轮次"。它是**全房一致项**：房主取本机 `AgentDecisionBroker.Enabled`，客户端跟随战斗计划里的 `externalBrain` 字段（`AiCombatPlanCodec`），不一致就切换（`AiTeammateManager.cs:765,805`）。两端播放顺序不同 → 弃牌堆顺序分叉 → `StateDivergence` 踢人。
- **轮次预算**：外部大脑单回合最多 6 次决策（内置 3 次），硬超时 `330 + 90×(轮次-1)` 秒（`AiTeammateManager.cs:37,770`）；仍有能量/辉星且有可打牌才会追加下一次。内置后端 `Math.Clamp(CombatTurnBudgetSeconds,15,180)` 秒。
- Phase 3（启发式清空剩余手牌）在 `ExternalBrainActive` 时关闭（`CombatDecisionExecutor.cs:182`），否则会花掉 dsh 还没看见的抽牌。
- 决策返回 `tool_calls`（`read_pile`/`card_info` …）仍有效：主线程执行只读工具、结果拼进新 prompt 再挂一次决策。
- **校验反馈**：`last_decision{id,kind,requested,dropped,resolved,valid,age_seconds}`，`resolved` 形如 `"打击->骷髅[#1]"`，对被丢弃动作在 `notes` 里提醒。
- **人格**：`Enabled` 时 system prompt 用 `PersonalityPromptBuilder.BuildExternalAgentSystemPrompt`（`prompt.system.external_agent`），只保留 JSON/语言要求与团队协作原则，人格由 dsh 自己决定。

## 7. 手牌/选牌交由 dsh

- 触发路径 `DshHandSelect.AppliesTo`（`RoomPatches.cs:1730`）：`AgentDecisionBroker.Enabled` && `AgentWatching` && 本局没有由 Mod 驱动的 AI 玩家 && 是本地玩家。满足时 **不** 打开 `NPlayerHand.SelectCards`（那会一直等真人点击并软锁战斗），改为挂 `kind=hand_select` 决策。
- 决策内容：`options[{index,card,upgradable,detail}]` + `min_select/max_select/allow_empty/answer_shape`；prompt 里每张牌带费用、当前效果、「升级后: …」和**词缀/悬停侧边栏**（`PromptTextHelper.GetHoverTipText`）。回答 `{"indices":[...],"reason":"..."}`，回填走原有 `SyncLocalChoice`。
- **区间语义**：`min==max` 正好选这么多；`min<max` 可少选（游戏确认）；`min==0` 时 `{"indices":[]}` = 不选并确认。超过 `max` 截断，少于 `min` 只补到 `min`（不再强行补到上限）。
- **超时**：`DecisionService.OutOfCombatTimeoutMs`；无人应答回退启发式——手牌默认"删牌"启发式，药水/牌堆取牌用 `PickBestForGain`（挑最强）。
- **多人红线**：AI 玩家自己的选牌只在单人局挂决策；多人局每个 peer 本地模拟 AI，而 `Enabled` 是本机标志，让 dsh 决定 AI 拿哪张会撕开各端牌堆，所以多人局一律跑确定性启发式。
- `sts2_play_card`/`sts2_end_turn` 在 `NPlayerHand.IsInCardSelection` 为真时直接拒绝，要求先回答 `hand_select`。
- `FromHandForUpgrade`、`FromChooseACardScreen`（攻击/技能/能力/无色药水、Discovery、Quasar…）、`FromCombatPile`（Droplet of Precognition、Liquid Memories、Cleanse、Headbutt…）分别走 `hand_select` / `card_selection`，`min/max` 规则相同。
- 目标选择：`NTargetManager.IsInSelection` → `phase=target_select`，选项遵守 `NTargetManager` 自己的过滤（MEND 不能选自己），最后一个索引取消；点取消让原动作返回失败、休息处重新启用选项。

## 8. 聊天中继（游戏面板由 dsh 回复）

- **门槛是"观察到能力"**：`AgentChatToDsh` && `ConnectedClients>0` && 最近 10 秒内有 `sts2_chat_poll`（`AgentChatRelay.ShouldRelay`）。旧插件、没绑定对话的 dsh、桥接关闭都自动回落到内置后端。
- 玩家发言 → `ChatManager` 记录后询问 `AgentChatRelay.TryRelay`：入队（`seq` 自增，队列 10 分钟 / 50 条上限）并**不再**请求后端，也不再触发内置 `TryAutoMessage`。
- 注入用 dsh 的 `steer()`（next-step）而**不是** `followup()`（next-turn）：自动驾驶时一轮持续整局，`followup` 会把聊天排到该轮结束之后，玩家等不到回复、Mod 先超时补答，模型随后只能猜 `reply_to`。
- **回复**：`sts2_chat_send {text, reply_to, directives?, nickname?}` → `ChatManager.ApplyDshReply` 渲染（面板/气泡/历史与内置后端一致），`directives`（战术记录）与 `nickname`（改显示名）副作用保留。
- **`reply_to` 绑定**：命中仍在等待的 `seq` → 标记已回答；命中已被内置后端补答的 `seq` → 仍渲染并注明是迟到回复（玩家更想看到 dsh 的答案）；命中不存在/已清理/猜错的 `seq` → **整条拒绝**并回传仍在等待的 seq 列表；不带 `reply_to` 且只有一条待回复 → 自动绑定；多条待回复 → 按新发言发送并提示还有哪些 seq 待回。
- **超时**：`AgentChatReplyTimeoutSeconds`（默认 120，clamp 15..600）内无回复，或客户端断开，则 `AgentChatFallbackToBackend` 打开时由内置后端补答并在面板注明。
- `sts2_chat_poll`/`sts2_chat_send` 不计入 dsh 活动打点；只读模式下聊天中继照常工作（聊天不是游戏动作）。

## 9. 自动接管邀请（`sts2_takeover`）

桥接不能主动推事件，所以是**邀请 + 认领**：AI 队友入局时 `AgentTakeoverInvite.OnAiPlayerJoined` 为这局生成 `run_token` 并开窗（`AiTeammateManager.cs:282`）；插件每 4 秒轮询一次，先到先得。

按顺序判定，返回 `{take:false,reason}` 之一：

| reason | 条件 |
|---|---|
| `settings_unavailable` / `disabled` | 设置读不到 / `AutoLaunchAgentSession` 关闭 |
| `read_only` | `AgentBridgeAllowActions=false` |
| `no_ai_teammate` | 本局没有 AI 队友（无 `run_token`） |
| `warmup` | AI 入局不足 30 秒，留给玩家手动接管 |
| `already_driving` | 最近 15 秒内有 dsh 请求 |
| `exhausted` | 本局已认领 3 次 |
| `run_changed` / `claimed` | 加锁后重读发现换了局 / 本局已被认领且未过期（60 秒） |

> 修正：旧文档列了 8 个 reason，漏掉 `settings_unavailable` 与 `run_changed`（`AgentTakeoverInvite.cs:90,128`）。

成功时返回 `{take:true, run_token, character, prompt, workspace, host:"127.0.0.1", port}`，插件据此在当前 dsh 进程里新建普通会话并 `followup` 首条消息；首条消息由 `AgentLaunchPrompt` 模板渲染（占位符 `{Host}/{Port}/{Token}/{Workspace}/{Character}`，空模板用 `DefaultPrompt`）。`workspace` 不存在时回退用户主目录。认领后 60 秒内没有真实活动即视为建会话失败，允许重试。

**要点**：会话控制器缺失（headless/ACP）时插件永久关闭自动接管；Mod 从不启动 dsh——没有开着的 dsh 就没人认领。

## 10. 设置项（设置 → 模组设置 → AI 队友 → 外部 Agent（dsh））

| 字段 | 默认 | 说明 |
|---|---|---|
| `EnableAgentBridge` | `false` | 启动 127.0.0.1 TCP 桥接 |
| `AgentBridgePort` | `26333` | 监听端口（clamp 1024..65535） |
| `AgentBridgeToken` | `""` | 可选 token，非空时每个请求都要带 |
| `AgentBridgeAllowActions` | `true` | 关掉即只读 |
| `AgentFallbackSeconds` | `30` | dsh 静默多久后回退自带后端；0 = 永不回退（滑杆 0..300） |
| `AgentChatToDsh` | `true` | 由 dsh 回复游戏聊天 |
| `AgentChatReplyTimeoutSeconds` | `120` | 一条聊天等 dsh 的秒数 |
| `AgentChatFallbackToBackend` | `true` | 超时/断开时内置后端补答 |
| `AutoLaunchAgentSession` | `true` | AI 队友入局时邀请 dsh 认领 |
| `AgentLaunchWorkspace` | `F:\sts2-mod\MiniMods` | 新会话归属目录（dsh 按它分组） |
| `AgentLaunchPrompt` | `AgentTakeoverInvite.DefaultPrompt` | 新会话首条消息模板 |

> 修正：旧文档只列了 7 项，缺 `AgentFallbackSeconds`、`AutoLaunchAgentSession`、`AgentLaunchWorkspace`、`AgentLaunchPrompt`（`AiSettings.cs:277,304,307,310`）。

设置页状态行显示运行中/端口占用/错误、已连接客户端数、聊天中继是否就绪，以及接管窗口状态；「重启」按钮走 `AgentBridgeService.Restart()`，端口或 token 改完立即生效。

游戏内可见性：聊天面板标题栏在有客户端时显示 `dsh` 徽标（可动作=亮蓝、只读=灰），tooltip 带客户端数，最小化按钮文案变为 `AI 队友 · dsh`；由 `ClientCountChanged` 驱动，普通单人局也会因此显示面板（`NGamePatch.cs:591`）。

> 注：设置页 `agent_actions` 的说明文字仍写着"Actions are singleplayer-only"，与实现不符——多人局允许驱动本地玩家自己那份（`AgentSnapshot.cs:277` 的提示语）；只有"当局的 AI 玩家由 dsh 当大脑"时才拒绝 UI 动作。

## 11. 安全与限制

- 默认关闭；开启后仅监听回环地址，不对外网开放。
- 可选 token（请求字段 `token`，等值比较）。
- 只暴露游戏动作与只读查询，不提供文件/进程/网络代理；不读取/回传 API Key、Cookie。
- 所有动作都走游戏自身 API/UI 节点（`TryManualPlay`、`ForceClick`、`ActionQueueSynchronizer.RequestEnqueue`），不直接改存档文件。
- 已知边界：`rune_select` 里"只调敌方海克斯"的界面不驱动；`unsupported` overlay 需要人工处理；`sts2_start_run` 不能开 Dread/Blitz 单人局；Dread/Blitz 仅 0.113.1+ 存在。

## 12. 行为要点（易踩的坑）

- 出牌必须 `TryManualPlay`，`AutoPlay` 免费且不扣费。
- 结束回合必须走同步 `EndPlayerTurnAction`；直接改 ready 标记会让客户端独自进入回合结束阶段一并掉线。
- 弃药水同理走 `DiscardPotionGameAction`。
- 目标寻址是 `Decision/CombatTargetResolver.cs` 单一入口：`#[n]`/`n`/`名字#n`/`名字(12/50)`/敌人名/怪物 id/自身别名；同名多只不带索引会**报歧义**而不是取第一个，解析结果在执行前锁成具体 `Creature`。
- token 剔除 `age_seconds`，否则 `sts2_wait` 立即返回、autoplay 反复空转。
- 打牌/选目标/选牌都靠当前 UI 或决策，任何"猜一个"的兜底都可能把状态带歪。
- 只读模式不含动作；`sts2_takeover`、`sts2_chat_*` 不算活动打点。
- 桥接在 `NGame` 就绪前不启动，端口冲突会记 `LastError` 并在设置页显示。

## 13. 故障排查

1. `sts2_status` 报 NOT CONNECTED：先看设置页状态行（未启用 / 端口占用 / `The game is not ready yet.`），再核对插件 `port` 与 token 是否一致。
2. 连上了但 AI 一直用自带后端：`ClientIdleSeconds` 超过 `AgentFallbackSeconds`；让 dsh 调一次 `sts2_state` 即可恢复，也检查 dsh 那边是否真的在跑 agent 轮次。
3. `sts2_play_card` 一直被拒：确认 `phase=combat`、`controlled.kind=local`、不在 `NPlayerHand.IsInCardSelection`；AI 模式下这个工具本就不该用。
4. AI 卡在 `ai_busy`：看 `last_decision` 是否把动作全丢了（卡名/费用/目标不对），或 `Phase 3` 是否被误开（`ExternalBrainActive`）。
5. 玩家聊天没被回复：确认插件在轮询（`chat.capable` 为 true）、`reply_to` 是否是 `chat.pending` 里的 seq；被内置后端补答的 seq 仍接受为迟到回复。
6. 玩家自己打牌却被吞了选牌界面：检查 `AgentWatching`（10 秒窗口）——只有 dsh 仍在持续调用才会接管手牌选择。
7. 多人局掉 `StateDivergence`：查两端 `externalBrain` 是否一致、是否有人在多人局里让 dsh 决定 AI 选牌。
8. 自动接管没反应：`AutoLaunchAgentSession` 是否开着、AI 是否入局满 30 秒、最近 15 秒是否已有 dsh 请求、本局认领次数是否用完；headless/ACP profile 没有会话控制器，插件会永久关闭该功能。
9. 改动后要在干净主菜单根上开局/建房；在任何大厅里调用会被拒绝并提示先 `sts2_proceed`。

## 14. 文件清单

| 文件 | 内容 |
|---|---|
| `Agent/AgentBridgeServer.cs` | TcpListener + 行 JSON 协议、token、被动调用判定、客户端计数 |
| `Agent/AgentBridgeService.cs` | 生命周期、设置指纹、客户端活动打点、`ClientCountChanged` |
| `Agent/AgentBridgeSupervisor.cs` | Godot `Node`，每秒 `SuperviseTick` |
| `Agent/AgentToolExecutor.cs` | 工具分发、`wait` 循环、delta、动作串行、`play_cards`/`claim_rewards` |
| `Agent/AgentSnapshot.cs` | 阶段判定、快照 JSON、token、options |
| `Agent/AgentMenuSupport.cs` | 主菜单/子菜单/大厅枚举与按下（含 Dread/Blitz 反射识别） |
| `Agent/AgentActions.cs` | 各动作的游戏 API/UI 实现、运行控制 |
| `Agent/AgentDecisionBroker.cs` | 决策挂起/应答/超时/静默回退 |
| `Agent/AgentDecisionFeedback.cs` | `last_decision`（dropped/resolved/valid） |
| `Agent/AgentChatRelay.cs` | 聊天中继、poll/send、超时回退 |
| `Agent/AgentTakeoverInvite.cs` | 自动接管邀请窗口与首条提示词 |
| `Agent/AgentNotes.cs` | `sts2_note` 笔记持久化（按种子） |
| `Agent/HextechRuneSelectSupport.cs` | Hextech 符文选择界面的反射桥 |
| `Solo/AgentRunLauncher.cs` | 标准/每日/自定单人开局（Dread/Blitz 拒绝并转 `sts2_host`） |
| `Compat/DailyRunCompat.cs`、`Compat/GameCompat.cs` | 每日种子/角色/进阶/词条复算；`IsAutoAiLobby`、`FastHost`、模式按名解析 |
| `Patches/RoomPatches.cs` | `DshHandSelect`：手牌/牌堆/选牌屏 parking 与启发式兜底 |
| `Patches/NGamePatch.cs` | 挂载 supervisor、dsh 徽标、面板可见性 |
| `Settings/AiSettings.cs`、`AiSettingsPage.cs` | 11 个字段与「外部 Agent（dsh）」页 |
| `docs/dsh-agent-bridge.md` | 本文 |
