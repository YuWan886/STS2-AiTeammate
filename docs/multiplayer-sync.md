# 多人同步模型

每个 peer 都在本地模拟 AI 玩家（游戏里没有它的连接），所以任何改变 AI 状态的事必须**恰好发生一次**，再在所有端以相同方式应用。

## Authority（谁做决定）

`Compat/AiMultiplayer`：房主拥有全部 AI 决策，客户端（`IsFollower`）从不向 LLM 请求 AI 决策；单人和回放也是 authority。`RequiresDecisionSync` 在网络房主且真人 ≥2 时为 true，客户端恒为 true。

## 战斗计划

卡牌用 `CardCmd.AutoPlay` 打出，它直接改本地模拟、从不入动作队列 → 房主发布每个通过校验的决策 pass（`floor:turn:pass`），客户端执行同一份计划（`Compat/AiCombatPlanCodec` + `AiSyncBus.KindCombatPlan`）。

- 计划里带 `externalBrain`，即**执行模式**：dsh 驱动 = 按计划顺序打、跳过 Phase 3、每回合最多 6 pass；内置 = 先技能后攻击、Phase 3 补齐、3 pass。
- `AiTeammateManager.ExternalBrainActive` 是当回合的模式：authority 从本地 `AgentDecisionBroker.Enabled` 求出，follower 从计划里的 `DecisionResult.ExternalBrain` 采纳并重算 pass 预算。
- **绝不要用 `AgentDecisionBroker.Enabled` 分支任何会影响“打哪张牌”的逻辑**：该标志是 per-machine 的。两端用不同顺序执行同一份计划会让 AI 的弃牌堆 desync（2026-11-07 实例：房主 `[NEUT,DEF,DEF,STRIKE]` vs 客户端 `[DEF,DEF,NEUT,STRIKE]` → `StateDivergence`）。
- 药水动作走动作队列，只有 authority 入队（`CombatDecisionExecutor.UseOnePotion`），客户端等复制过来的动作。

## 非战斗选择

- **奖励与商店只有房主领取**（客户端等状态快照）。
- **事件与休息处由房主决策后广播**（`AiSyncBus.KindChoice`），每个 peer 都要应用：事件需要每个 peer 的 `EventModel` 前进；休息处需要 AI 的选项在每个 peer 都解析掉，否则 `AfterAllRestSitesCompleted` 在那里挂住。

## 状态复制

`Compat/AiStateSync` + `AiSyncBus.KindState`：房主广播 AI 的 `SerializablePlayer`（牌组 / 遗物 / 药水 / 金币 / RNG / 遗物抓包）。时机：AI 入队时、`CombatStateSynchronizer.StartSync`、以及奖励 / 事件 / 商店之后。客户端在战斗未进行时应用 `Player.SyncWithSerializedPlayer`；战前那次由快照闸门守着（`CombatStateSynchronizerPatch`），带 2.5s failsafe。

## 校验和

AI 的遗物抓包从 `NetFullCombatState` 里剥掉（`AiChecksumNormalizePatch`），因为它单独复制；其余照常参与校验。

## 绝不把 AI 的选择/战利品当作本地玩家广播

`RewardSynchronizer.SyncLocal*` 和 `PlayerChoiceSynchronizer.SyncLocalChoice` 会盖上**发送者**的 id，所以不用于 AI（`AiChoiceBroadcastSkipPatch`）。AI 动作经 `ActionQueueSynchronizer.EnqueueAction` 重新归属（`AiActionOwnerPatch`），客户端据此为 AI 重建动作。

## 传输层与大堂

- 发往保留 AI NetId 的定向消息直接丢弃（`AiPeerTransportPatch` / `SteamHostAiPeerSkipPatch`）：原版的 `StartRunLobby` / `LoadRunLobby` 用逐玩家定向消息宣告加入，Steam 房主对假 peer 会抛异常，曾把正在加入的人类踢下线。
- 读档：`LoadGameScreenHostPatch` 在 `InitializeAsHost` 时把 AI 立刻写进 `ConnectedPlayerIds`——AI 发不出 `ClientLoadJoinRequestMessage`，否则每次读档都弹警告甚至卡流程。
- 大厅注入：标准大厅由角色选择 / dsh 的 `ai_join` 添加 AI；daily / custom /（0.113.1+）Dread / Blitz 由 `AutoLobbyAiPatch` 自动添加并准备。
- 第三方 mod 兼容（Multiplayer Limit Break）：MLB 只按真实 lobby 消息回填每个 peer 的扩容能力，而 AI 只是 `lobby.Players` 里没有连接的假玩家——不登记就会常驻「因不兼容客户端已禁用」，并在名单凑够 4 人时把下一个真人用 `NetError.ModMismatch` 拒之门外（原版弹「模组不一致」，实际模组完全一致）。`Compat/MlbPeerCompat` 在房主侧用反射把 `MlbPeerCapability.Local` 写进 MLB 自己的 `MlbLobbyProtocolState`（加入时登记、移除时清理，重复调用幂等），使它的大厅状态条、入房校验和开局快照口径一致；AI 仍照常占一个席位。

## 显示名同步

`Compat/AiNicknameSync` 用 `AiNicknameMessage` 广播昵称：房主在 AI 加入时发一次，其它 peer 存值并刷新 nameplate（见 [architecture.md](architecture.md) 第 4 节）。

## 相关补丁

`CombatStateSynchronizerPatch`、`CombatSyncAiStatePatch`、`SteamHostAiPeerSkipPatch`、`AiActionOwnerPatch`、`AiChoiceBroadcastSkipPatch`、`AiChecksumNormalizePatch`、`CombatTurnTransitionPatch`、`LoadGameScreenHostPatch`——逐个说明见 [patches.md](patches.md)。
