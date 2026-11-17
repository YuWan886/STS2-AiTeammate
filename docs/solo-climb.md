# 单人「AI 独自爬塔」

`EnableSoloClimb` 让 AI 接管**本地单机玩家**，人类观看、聊天、随时打断。`SoloCharacterId` / `SoloAscensionLevel` 配置“开始 AI 独自爬塔”这一局。开局走 `Solo/AgentRunLauncher`（dsh 的 `sts2_start_run` 也用它；它绝不把 `EnableSoloClimb` 打开）。

| 组件 | 职责 |
|---|---|
| `SoloRunController` | 绑定/释放 AI 对本地玩家的控制。`TryBind` 只在 solo 模式开且是单机局时生效；失败会按原因记录一次日志。`RequestBindRetry` 在几秒内重试——读档时本地 netId 要等 `RunState` 反序列化后才存在。`SoloSupervisor`（挂在 `NGame` 上）在 solo 模式开启且有单机局进行中时每秒重试一次绑定，所以重启游戏后点 “Continue” 也能恢复（即使载入钩子在本地玩家存在之前就跑了）。 |
| `AiFlowControl` | 中断控制：聊天消息暂停 AI，聊天面板 ⏸ 切换粘性暂停，暂停中的战斗**保持回合不结束**直到人类恢复。 |
| `SoloMapNavigator` | 地图选路。**按楼层去抖**：房间的 proceed 按钮在打开的地图下方仍然可点，不去抖会无限重开路线决策。`NMapScreen.Open` 每次重开都重新驱动选路。 |
| `SoloRoomAutomation` | 房间流程：休息处、商店等走**本地玩家** API（保持 UI 同步）；奖励界面被抑制后自己调 proceed 步骤。 |
| `AgentRunLauncher` | dsh 的程序化开局（standard / daily / custom），见 [dsh-agent-bridge.md](dsh-agent-bridge.md)。 |

注意：

- solo 下 AI 就是“本地玩家”，所以很多补丁里 `SoloRunController.IsActive` 会走本地玩家分支而不是多人分支（休息处、商店、建筑师事件、地图投票都是如此）。
- `AiFlowControl` 的暂停只影响 AI 自动化，不影响人类操作界面（人类仍可接管、点按钮、聊天）。
- solo 里 dsh 当 AI 的大脑时，手牌选择 / 选牌药水会在 `AgentDecisionBroker` 里挂起决策（`kind=hand_select` / `kind=card_selection`）；多人下永远不挂，因为每个 peer 必须选出同一张牌。
