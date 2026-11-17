# 决策引擎

AI 的每一个决定都走 `DecisionService`：战斗内用回合循环 + `CombatDecisionExecutor`，战斗外用每房间一个 `Decide*Async` + `OutOfCombatExecutor`。所有请求都是 HTTP（线程池），所有游戏状态读写都回主线程（见 architecture.md 第 5 节）。

## 1. 战斗回合

- 触发：`CombatManagerPatch` 订阅 `TurnStarted` → 后台线程跑 `ProcessCombatTurnAsync`，带超时与取消；新回合会取消上一回合在飞的请求（`CancellationTokenSource`）。
- 四个阶段：**0 药水 → 1 铺场（非攻击牌）→ 2 协调攻击 → 3 剩余可打牌**。协调攻击用 `wait_for` 排队：`CombatDecisionExecutor` 以 `CoordinationPollMs = 120ms` 轮询，窗口 `900ms` / `2000ms`（有显式 defer 时更长）。
- 硬超时：`CombatTurnBudgetSeconds`（默认 45s）；`finally` 里如果战斗仍在跑，强制 `SetReadyToEndTurn` 结束回合，避免任何路径卡死。
- 退路：`DecisionValidator.ApplyFallback` 本地启发式，优先级 攻击 → 技能 → 能力。
- dsh 驱动时执行器跳过 Phase 3、最多 6 pass（见 [dsh-agent-bridge.md](dsh-agent-bridge.md)）；多人下这套差异必须由战斗计划的 `externalBrain` 传递（见 [multiplayer-sync.md](multiplayer-sync.md)）。

## 2. 非战斗决策

`DecisionService` 按房间提供：`DecideCardRewardAsync`、`DecideRestSiteAsync`、`DecideShopAsync`、`DecideEventAsync`、`DecideMapNavigationAsync`、`DecideTreasureRelicAsync`。

- 非战斗请求走 `SendNonCombatDecisionWithRetryAsync`：重试次数 `NonCombatRetryCount`（默认 2），第 n 次重试前等 `500ms × n`。
- `NonCombatDecisionTimeoutMs`（默认 30s，`DecisionService.OutOfCombatTimeoutMs`）既是请求超时，也是房间补丁用的外层上限。
- 休息处 SMITH 支持 `upgrade_indices` 指定升级哪几张牌（上限取 `GameCompat.SmithCount`）。

## 3. Prompt 组成

- `ContextBuilder` 负责所有 prompt 片段：`BuildCombatEssentials`（战斗）、`BuildSuggestionContext`、`AppendDeckList`（卡牌奖励 / 商店给全牌组）、`BuildMapContext` + `DescribeMap`（当前节点、可达节点、逐行边图）。
- `PromptSnapshot` 对战斗 prompt 做差分，只给与上回合不同的部分；`ConversationContextManager` 维护跨回合记忆与 run prelude。
- prompt 只陈述事实，选择由人格驱动：`PersonalityPromptBuilder` 按档案（backstory / 战斗哲学 / 沟通风格 / 决策原则）拼系统提示。dsh 接管时换成 `BuildExternalAgentSystemPrompt`（不注入 mod 人格与人格长期记忆）。

## 4. 牌堆可见性

- `ContextBuilder.BuildPileDetail` 把抽牌堆 / 弃牌堆 / 消耗堆写进战斗 prompt；同名卡合并为 `名称×N(cost+type)`，条目有上限（内联 40 / 工具 80 / 带描述 30），**永远不暴露牌序**——人类玩家也只能看到内容而不是顺序。由 `AiSettings.EnablePileDetail` 控制（关闭 ⇒ 只给数量）。
- 队友段只给手牌细节 + **各弃牌堆数量**（`FormatPileCounts`），内容要用 `read_pile` 按需取。

## 5. 只读工具（read-only tool calls）

任何决策都可以用 `{"tool_calls":[{"name":"read_pile","args":{"pile":"draw"}}]}` 代替决策字段回答。

- 单一循环：`DecisionService.RequestWithToolsAsync`，战斗和所有非战斗决策共用；`ResponseParser` 宽容解析 `tool_calls` / `tool_call` / `args` / `arguments`；`AiToolRunner` 在主线程执行（只读）；快照只进历史一次，角色严格 user/assistant 交替。
- 预算：战斗 `MaxToolRounds = 1`，跟进超时 = min(`DecisionTimeoutMs`, 12s)，仍在回合预算内；非战斗 `MaxOutOfCombatToolRounds = 2`，每个工具轮 15s，受 `EnableOutOfCombatReadTools` 控制。`hasAnswer` 判定（`actions` 非空，或 `choice` / `choices` / `indices`）让真答案优先于工具调用。未执行的工具调用（被关闭、预算用尽、solo 暂停）当作“没有方案”让调用方走退路。
- 工具：`read_pile`（`hand|draw|discard|exhaust|deck`，`detail: brief|full`）、`card_info`、`read_relics`、`read_potions`、`read_player`、`read_run`、`read_map`、`deck_stats`。每个都接受可选 `player`（显示名 / 角色 id / NetId / 1 起序号 / `我` / `自己`）以读取队友。结果上限 4000 字符，以 `AI_TOOL` 连参数记录日志。
- 公平性：任何工具都不暴露抽牌顺序、洗牌顺序或未来 RNG。

## 6. 多人地图投票

`AiMapVoter`（由 `NMapScreen._Ready` 和人类投票触发）在 `ContextBuilder.GetTravelableMapPoints` 里选点，然后以 `MapGenerationCount` / `MapLocation` 守卫投出**自己的** `MapVote`。人类的坐标只是 LLM 失败时的退路（预算取 `DecisionService.OutOfCombatTimeoutMs`，即 `NonCombatDecisionTimeoutMs`，默认 30s）；同一时刻最多一个决策在跑。

## 7. AI 拥有的选择与药水（0.111+）

0.111 起 `PotionModel.OnUseWrapper` 把药水的 `OnUse` 包进 `BranchingPlayerChoiceContext`（0.107 传的是原始 context）。于是到达 `CardSelectCmd.FromChooseACardScreen` / `FromHand` 的 context 不再是 `AiPlayerChoiceContextHelper` 匹配的 `GameActionPlayerChoiceContext`，而原版 `SignalPlayerChoiceEnded` 只在动作 owner 是**本地**玩家时才请求队列 resume → AI 归属的动作会在药水已消耗（`RemoveBeforeUse`）之后永远等 `WaitForActionToResumeExecutingAfterPlayerChoice`，战斗卡死。`AiOwnedActionChoiceEndResumePatch` 为 AI 归属动作请求 resume，版本中立、对人类动作是 no-op。

选牌药水（`Attack`/`Skill`/`Power`/`Colorless Potion`、Ashwater、Gambler's Brew、Touch of Insanity、Droplet of Precognition、Liquid Memories）必须在**屏幕出现之前**被拦下：`FromChooseACardScreen`、`FromHand`、`FromHandForUpgrade`、`FromSimpleGrid`、`FromDeck*`、`FromCombatPile` 都有 AI prefix。

- **增益**类效果用 `CardGridSelectionPatch.PickBestForGain`（最强卡），不要用 `PickCardsByHeuristic`（那个按“移除候选”排序，最先返回最差卡）。
- `PotionUsage.Automatic`（Fairy in a Bottle）从药水 prompt 里过滤掉，并被 `DecisionValidator` / `CombatDecisionExecutor.UseOnePotion` 拒绝——`UsePotionAction` 没有原版弹窗那个“禁用 Use 按钮”的等价物。
- 药水 prompt 行带真实目标类型（`目标:`）：0.111 几乎每种药水都是 `AnyPlayer`（自己或队友）。

`AnyPlayer` 指向（0.111）：`DecisionValidator.ResolveTargetForPotion` 经 `CombatTargetResolver` 以 `TargetScope.Any` 解析显式 `target` 并拒绝敌人；`CombatDecisionExecutor.UseOnePotion` 在动作来自未校验的 directive 退路时会重新解析同一目标（“给队友用药水”现在真的落在队友身上，以前会静默加在自己身上还把指令标成已完成）。空目标（或自我别名、单人局只有一个玩家）沿用 `UsePotionAction` 的 null-target 默认（即自己）。

## 8. 选择 context：绝不要暂停一个无法 resume 的流程

`AiChoiceContext.CanSignalForAi`（`RoomPatches.cs`）决定一次选牌能否调用 `SignalPlayerChoiceBegun`，所有镜像 prefix 都用它（`FromChooseACardScreen`、`FromHand`、`FromHandForUpgrade`、`FromSimpleGrid`、`FromCombatPile`）。

游戏自建的 hook context 以 `LocalContext.NetId` 作为 `_localPlayerId`（`Hook.cs`），而 `HookPlayerChoiceContext.SignalPlayerChoiceBegun` 只在动作 owner **就是**那个本地 id 时才入队 hook 动作——AI 归属的 hook（遗物/能力驱动的选择，例如 ChoicesParadox、Stratagem）会永远等 `ExecutionStartedTask`。所以只有 context 是 `GameActionPlayerChoiceContext`、`BlockingPlayerChoiceContext`，或 `_localPlayerId` 就是选择者的 `HookPlayerChoiceContext` 时，才镜像原版的 begin/end 成对调用。

0.111 的 `BranchingPlayerChoiceContext`（按类型名判断——0.107 变体里不存在该类型）按“它实际会做什么”解析：选择者拥有被包裹的 context（或无 owner）时转发过去由被包裹者决定；否则它会**创建** `HookPlayerChoiceContext(chooser, _localPlayerId)`，其 `_localPlayerId` 是 `LocalContext.NetId`（`PotionModel`/`CardModel`/`CardPileCmd` 都传这个），因此“人类对 AI 队友用药水”同样无法 resume，也不许暂停。

不安全时就**不暂停**地解析：没有暂停就没有需要 resume 的东西，而且每个 peer 跑同一套确定性启发式。

同一批修复：`AiChoiceBroadcastSkipPatch` 仍拒绝广播 AI 的选择，但会自己在本机抛 `PlayerChoiceReceived`，让 `CombatReplayWriter` 保住 AI 的选择（缺了它回放会永远等待）；等房主复制药水动作的 follower 若 3s 内药水槽没变化现在返回 `false`，而不是把玩家指令标成已完成；`AgentSnapshot.GetGridSelectRange` 沿运行时类型层级找 `_prefs`（0.111 把它放在 `NDeckCardSelectScreen` / `NSimpleCardSelectScreen` 上，否则范围会读成 (1,1)）。

## 9. 截图工具

截图必须在主线程：`AiToolRunner.Execute` 已经 marshal 过去，`ScreenshotTool` 用与 StupidScreenshot 相同的 `Viewport.GetTexture().GetImage()` 路径，缩到 1024px、JPEG 质量 0.72。图片只附在紧接着的那一次跟进请求上，历史保持纯文本，避免每轮重发。`ChatManager.TryRunChatScreenshotAsync` 给交互聊天同样的“一轮”流程（人类消息本来就在主线程，截图安全）。
