# Harmony 补丁地图

安装方式：`Compat/SafePatchInstaller.InstallAll(new Harmony(ModId))` 逐个安装补丁类，某个目标在当前游戏版本不存在时只记警告、不影响其它补丁（`Harmony.PatchAll` 会整体失败）。`SafePatchInstaller` 只是对每个补丁类 `try/catch`（没有 `Prepare()` 约定），所以补丁自己必须在小成员可能消失时先判断目标是否存在。

loader 侧另有 `HarmonyPatchAllTypeLoadGuard`（把 `PatchAll` 的 `TypeLoadException` 降级为跳过）和 ModTypes 桥，见 [architecture.md](architecture.md) 第 2 节。

## 启动 / 生命周期

| 类 | 目标 | 作用 |
|---|---|---|
| `NGamePatch` | `NGame._Ready` | 捕获主线程上下文、初始化管理器、挂 `LiveDanmakuService`、`SoloSupervisor`、dsh 桥，构建可拖动聊天面板、语音气泡和反应。**真实启动不会触发**（见 `ModBootstrap`）；`AfterGameReady` 才是唯一就绪入口，由 bootstrap 调用。 |
| `RunReadySoloBindPatch` | `NRun._Ready` | 新开/读档都会在 `RunManager.Launch()` 设好本地 netId 之后创建 `NRun`，是恢复 solo 控制最可靠的时机（单次 bind 常太早）。同时重跑 `DetectAndBindAiPlayer()` 换掉读档后的过期 `Player` 引用。 |
| `LoadRunPatch` | `NGame.LoadRun` | 读档后重新检测并绑定 AI；后台重试 solo bind（异步加载还没结束，本地 netId 可能未生成）。 |
| `LoadGameScreenHostPatch` | `NMultiplayerLoadGameScreen.InitializeAsHost` | 读档大厅把 AI 立刻塞进 `ConnectedPlayerIds`：AI 没有网络对端，永远发不出 `ClientLoadJoinRequestMessage`，否则每次读档都弹警告甚至卡住。 |

## 大厅 / 角色选择

| 类 | 目标 | 作用 |
|---|---|---|
| `CharacterSelectScreenPatch`（+ `...EmbarkPatch` / `...UnreadyPatch` / `...BackPatch`） | `NCharacterSelectScreen` | 增加/移除/配置/准备 AI 大厅玩家。 |
| `CharacterSelectScreenSelectCharacterPatch` | `NCharacterSelectScreen.SelectCharacter` | 兼容层（在 `Compat/CharacterSelectSelectCompatPatch.cs`，按版本分支），处理角色选择 API 变化。 |
| `AutoLobbyAiPatch` | `StartRunLobby.AddLocalHostPlayer` | 所有**没有“添加 AI”控件**的房主大厅（daily / custom 以及 0.113.1+ 的 Dread / Blitz）自动添加并准备 AI 队友，否则单人房主永远满足不了 `IsAboutToBeginGame`。判定刻意写成“不是 `GameMode.Standard`”（`GameCompat.IsAutoAiLobby`），因为 `Dread`/`Blitz` 在 0.113.1 才追加。标准大厅仍用角色选择 UI 或 dsh 的 `ai_join`。 |
| `LobbyAiCharacterPanelPatch`（`Ui/LobbyAiCharacterPanel`） | `NSubmenuStack.Push` | 给没有 AI 选人界面的 lobby 加紧凑“AI 队友”卡片（当前角色 + 准备状态 + 换人按钮，换人打开原版风格全屏网格）。卡片默认停在左上 (24,24)；进入 Dread/Blitz 大厅时 `EnsureParked` 把它挪到右上信息按钮（`DreadInfoButton` / `BlitzInfoButton`，64×64、距右边缘 24px，卡片以前会盖住它）左侧 12px，屏幕太窄才回落到玩家列表下方。选人走 `AiTeammateManager.SetAiCharacter`（仅房主）并持久化到 `AiSettings.AiLobbyCharacterId`。两个坑：代码创建的 picker 必须在进树**之后**建子节点（`NCharacterSelectButton.Init` 读取 `_Ready` 创建的节点），已建好的行要用界面自己的 `PlayerChanged` 刷新。 |
| `LobbyAiCharacterPickerBackPatch` | `NSubmenuStack.Pop` | 返回/Esc 先关选人网格，再退出大厅。 |

## 战斗

| 类 | 目标 | 作用 |
|---|---|---|
| `CombatManagerPatch`（类级 `[HarmonyPatch(typeof(CombatManager))]`） | `SetUpCombat`、`SetReadyToEndTurn`；订阅 `TurnStarted` / `CombatWon` | 检测 AI、驱动战斗回合、强制结束兜底、战后记忆生成。 |
| `CombatTurnTransitionPatch` | `ActionQueueSynchronizer.RequestEnqueue`（prefix，只处理 `ReadyToBeginEnemyTurnAction`） | 多人下等 AI 第一阶段结束后替它发“准备进入敌方回合”信号。**单人绝不发**：`SetReadyToBeginEnemyTurn` 在那里没有守卫，会重复游戏的转换。 |

## 多人同步

| 类 | 目标 | 作用 |
|---|---|---|
| `CombatStateSynchronizerPatch` | `CheckSyncCompleted` | 跳过 AI NetId，状态同步不等待不存在的对端；客户端在这一步挂起，直到房主的 AI 快照应用完毕。 |
| `CombatSyncAiStatePatch` | `CombatStateSynchronizer.StartSync` | 房主在战斗同步前广播 AI 玩家快照；客户端进入快照闸门。 |
| `SteamHostAiPeerSkipPatch` | `SteamHost.SendMessageToClient` | 丢弃发往保留 AI NetId 的定向消息（没有连接；原版会抛异常并把正在加入的人类踢掉）。 |
| `AiActionOwnerPatch` | `ActionQueueSynchronizer.EnqueueAction` | 把 AI 动作重新归属到 `action.OwnerId`，客户端据此为 AI 重建动作，而不是记到发送的人类头上。 |
| `AiChoiceBroadcastSkipPatch` | `PlayerChoiceSynchronizer.SyncLocalChoice` | 绝不广播 AI 的选牌结果（接收方会应用到发送者身上）；但仍在本机抛 `PlayerChoiceReceived`，让 `CombatReplayWriter` 记录 AI 选择。 |
| `AiChecksumNormalizePatch` | `NetFullCombatState.FromRun` | 校验和快照里剥掉 AI 的遗物抓包（它单独同步），其余照常参与校验。 |

细节（authority、战斗计划、状态复制、desync 教训）见 [multiplayer-sync.md](multiplayer-sync.md)。

## 非战斗决策

| 类 | 目标 | 作用 |
|---|---|---|
| `EventBeginLogPatch` | `EventSynchronizer.BeginEvent` | 每次事件开始重跑 `DetectAndBindAiPlayer`（读档后缓存引用过期会让 `GetEventForPlayer` 抛 `IndexOutOfRange`），清掉上一个事件的翻页记忆，再启动 AI 的事件决策。 |
| `EventAutoChoicePatch` | `EventSynchronizer.ChooseLocalOption` | AI 的事件选项：非共享事件独立决定，共享事件跟随投票；可自动连推单选项页（上限 5 页）。 |
| `TheArchitectWinRunAiReadyPatch` | `TheArchitect.WinRun` | 建筑师事件里 AI 也需要给“进入下一幕”投票（原版只给 `LocalContext.IsMe` 的玩家发 ready 信号）。solo 下跳过。 |
| `MapAutoVotePatch` | `MapSelectionSynchronizer.PlayerVotedForMapCoord` | 有人投票后触发 AI 自己的地图投票（`AiMapVoter`）；solo 下跳过。 |

## 房间自动化

| 类 | 目标 | 作用 |
|---|---|---|
| `OfferForRoomEndPatch` | `RewardsCmd.OfferForRoomEnd` | 为 AI 触发房间结束奖励流程（内部 `WithRewardsFromRoom` 可能因 AI 的 PlayerOdds 未初始化抛 NRE，实际奖励由 `RewardsSetOfferPatch` 接手）。 |
| `RewardsSetOfferPatch` | `RewardsSet.Offer` | 拦截 AI 的奖励领取（原版会在 `IsMe` 处提前返回）。**客户端不领**：房主独占整条奖励流程（含 LLM 选择）再同步 AI 状态，两边各问一次 LLM 正是早期 desync 的根源。卡牌入库后补调 `CardPile.InvokeCardAddFinished()`，因为被抑制的奖励界面没有桌面节点，而顶栏牌数监听该事件。 |
| `MerchantRoomPatch` | `MerchantRoom.EnterInternal` | 商店：solo 走房间自己的本地库存（保持 UI 同步）；多人只有房主购买（库存每 peer 独立 roll，购买会改 AI 的金币/牌组/遗物），客户端等状态快照。 |
| `MapScreenPatch` | `NMapScreen._Ready` | 多人：地图打开 1.5s 后让 AI 投自己的票（以前是点节点，等于用房主的票）。solo：转 `SoloMapNavigator`。 |
| `MapScreenOpenPatch` | `NMapScreen.Open` | solo：每间房之后地图都会重开，每次重开都要重新选路线。 |
| `RestSiteBeginPatch` | `RestSiteSynchronizer.BeginRestSite` | 休息处：为 AI 拿选项并决策（solo 走 `SoloRoomAutomation.HandleRestSite`）。 |
| `RestSiteCompletionSafetyPatch` | `RestSiteSynchronizer.AfterAllRestSitesCompleted`（prefix） | 兜底补完 AI 的休息处槽位：选项路径没找到 AI 的 `PlayerRestSite`（或人类在 AI 决策飞行中先走了）时，`RestSiteRoom.Exit` 会永远等待，游戏卡在黑屏淡出。 |
| `TreasureRoomRelicAutoPickPatch` | `NTreasureRoomRelicCollection.InitializeRelics` | 宝箱遗物：等本地收藏初始化后再投票（早投会让 `AnimateRelicAwards` 抛 `Sequence contains no matching element`），并经动作队列广播让所有 peer 一起应用。单选直接拿，多选走 `DecisionService.DecideTreasureRelicAsync`（dsh 驱动时是挂起的决策），本地兜底 `TreasureRelicHeuristic`。 |
| `CardBundleSelectionPatch` | `NChooseABundleSelectionScreen.AfterOverlayShown` | AI 的卡牌包选择。 |
| `ChooseACardSelectionPatch` | `NChooseACardSelectionScreen.AfterOverlayShown` | 屏幕已出现时的兜底自动选（弃牌、Survivor 效果、事件选牌等）；正常路径在 `CardSelectCmd` 就已经被拦下，不会开屏。 |
| `CardGridSelectionPatch` | `NCardGridSelectionScreen.AfterOverlayShown` | 同上（网格选牌），并提供 `PickBestForGain` / `PickCardsByHeuristic` 两个排序策略。 |
| `DeckCardSelectPatch` | `NDeckCardSelectScreen.AfterOverlayShown` | 同上（牌组选牌）。 |

## 卡牌选择命令（`CardSelectCmd`）

AI 的所有选牌都在**屏幕出现之前**拦截，返回 `Task<...>` 而不是开 UI：

| 类 | 命令 | 备注 |
|---|---|---|
| `ShouldSelectLocalCardPatch` | `ShouldSelectLocalCard` | 对 AI 返回 true；否则 AI 会掉进 `WaitForRemoteChoice` 软锁。 |
| `AiOwnedActionChoiceEndResumePatch` | `GameActionPlayerChoiceContext.SignalPlayerChoiceEnded` | 0.111 起药水 `OnUse` 包在 `BranchingPlayerChoiceContext` 里，AI 归属的动作会永远等 `WaitForActionToResumeExecutingAfterPlayerChoice`（药水已消耗、战斗卡死）。该 prefix 为 AI 归属动作请求 resume；对人类动作是 no-op。 |
| `FromHandAiPatch` / `FromHandForUpgradeAiPatch` | `FromHand` / `FromHandForUpgrade` | dsh 接管本地玩家时改为挂起 `kind=hand_select` 决策（`DshHandSelect`），否则原版手牌 UI 等人点击、战斗软锁。 |
| `FromChooseACardScreenAiPatch` | `FromChooseACardScreen` | 选牌药水 / Discovery / Quasar / Splash 等“凭空生成卡”的效果。 |
| `FromSimpleGridAiPatch` | `FromSimpleGrid` | 简单网格选牌。 |
| `FromDeckGenericAiPatch` | `FromDeckGeneric` | 牌组通用选择（如移除卡）。 |
| `FromDeckForUpgradeAiPatch` | `FromDeckForUpgrade` | 牌组升级选择。 |
| `FromDeckForEnchantmentAiPatch` | `FromDeckForEnchantment`（按参数类型匹配） | 事件/遗物/自定义 mod 的附魔流程。 |
| `FromCombatPileAiPatch` | `FromCombatPile` | 抽/弃牌堆挑选（Droplet of Precognition、Liquid Memories、Cleanse、Headbutt、Tutor 等）。 |

增益类效果用 `CardGridSelectionPatch.PickBestForGain`（最强卡），**不要**用 `PickCardsByHeuristic`（它按“移除候选”排序，返回最差卡）。多人下每个 peer 都要跑同一套确定性启发式，否则选择不一致。

## 可选 mod 集成 / 其他

| 类 | 目标 | 作用 |
|---|---|---|
| `Hextech*`（`HextechPatchInstaller` 及 7 个 `Hextech*Patch`） | 外部 HextechRunes mod（ARAM: Mayhem）的反射目标 | 仅在该 mod 存在时安装（并等待其程序集加载）。AI 没有网络对端，Hextech 为每玩家挂的 `PlayerChoiceSynchronizer` 事务需要替它应答：`SelectRuneMultiplayer` 合成 payload 走 `ReceiveReplayChoice`（复用 Hextech 自己的 journaling/seen/遗物授予）；锻造与遗物选项本地解析；幕选择 ack 立即完成；`GetActRollAuthorityPlayer` 回落到非 AI 玩家；`TryObtainRandomRunesMultiplayer` 返回 false 让调用方走 seed 稳定的本地路径；solo 攀爬回答模态选符文界面。只有单个真人时才用 LLM；2+ 真人时所有 peer 都在模拟 AI，改用 `HextechStableRandom` 保证一致。 |
| `RunReviewPatch` | `RunManager.OnEnded` | 每局结束（胜利走 `WinRun`，团灭走 `CreatureCmd.Kill`）交给 `RunReviewService` 写一次性 `RunSummary` 记忆。 |
| `AiPlayerNamePatch` | `PlatformUtil.GetPlayerNameRaw` | 保留 AI NetId 返回 `AiNames.DisplayName`，让原版 nameplate / tooltip 显示昵称而不是假 id。 |
