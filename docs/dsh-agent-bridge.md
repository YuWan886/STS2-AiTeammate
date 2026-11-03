# dsh 接管游戏操作 —— 开发文档（插件方案）

> 状态：v1 设计稿
> 目标读者：维护 AiTeammate 的开发者
> 相关代码：`AiTeammateCode/Agent/`（C# 游戏侧）、独立仓库 `dsh-sts2`（npm 上的 dsh 插件）

## 1. 目标

给 AiTeammate 增加一个**外部 Agent 接管（External Agent Bridge）**能力：让 **dsh（DeepSeek Harness）** 直接连进正在运行的游戏，读取完整局面、调用动作工具，像人一样打完一整局。

关键诉求：

1. **dsh 直接操作游戏**：决策、循环、重试都由 dsh 自己完成，不把 dsh 当成普通 LLM 后端。
2. **工具调用对 dsh 是原生的**：`sts2_*` 工具通过 **dsh 插件**（`ctx.tools.register`）注册，和 dsh 自带的 `bash` / `fs` 工具完全同构，支持 dsh 的工具流水线、并行调用、取消与权限策略。
3. **可以真正“接管”**：插件能通过 `ctx.agents` 在 agent 空闲时主动排队续玩消息，不需要玩家每个回合手动说“继续”。
4. 不破坏现有能力：内置 AI 队友、AI 独自爬塔、聊天、弹幕全部保持原样。
5. 与现有实现最大化复用：读牌堆/遗物、出牌、地图、奖励、商店、篝火、事件都复用已有代码路径。

## 1.1 v2：dsh 成为 AI 的大脑

v1 只让 dsh 操作**本地真人玩家**。v2 增加了一条更重要的通道：**对接 dsh 后，AI 玩家（单人爬塔的本地玩家、多人局的 AI 队友）的决策也由 dsh 给出**。

- 触发条件：TCP 桥接在跑（`EnableAgentBridge`）、允许动作（`AgentBridgeAllowActions`）、且至少有一个客户端连着（`AgentBridgeService.ExternalAgentDriving`）。
- 拦截点只有一个：`DecisionService.RequestWithToolsAsync`（所有战斗/选牌/选路/篝火/商店/事件/选卡/海克斯决策都从这里发起）。命中时不再调用配置的 LLM 后端，而是把这次调用挂在 `AgentDecisionBroker` 上等待。
- 快照里出现 `phase=ai_decision` + `decision{id,kind,prompt,system_prompt,answer_with}`；dsh 用 `sts2_decide {decision:{...}}` 回答，内容会被原样交给现有的 `ResponseParser`，所以解析、校验、兜底、执行全部不变。
- 好处：
  - 单人爬塔不再需要关掉开关：`SoloRunController` 的循环照常跑，只是每步决策在等 dsh，天然「自动退位」。
  - 多人局里 AI 队友的选牌/事件/商店/篝火/地图投票也一并交给 dsh（这些流程只存在于数据层，本地没有可点的 UI）。
  - 没有 AI 玩家（普通单人局）时行为与 v1 相同：dsh 用 UI 动作自己玩。
- 失败与超时：单次决策的等待预算是 `AgentFallbackSeconds × 3`（至少 60 秒，最多 300 秒），这样模型思考得久一点不会被误判；客户端断开（客户端数降到 0）会立刻 `Abort`，等待方返回空内容，走 mod 原有的兜底路径；战斗回合的 45 秒硬安全网在有外部大脑时放宽到 `330 + 90×(单回合决策次数-1)` 秒。
- 连着 ≠ 在驱动：桥接插着但 dsh 一直没请求（比如 dsh 没在 autoplay、也没人让它干活）时，`AgentDecisionBroker` 不再挂起决策，直接走自带后端。判定依据是 `AgentBridgeService.ClientIdleSeconds`（每次 dsh 请求刷新，正在跑的 `sts2_wait` 也算活着）；静默超过 `AgentFallbackSeconds` 就整段跳过桥接，所以“连着但不干活”不会再让 AI/整局卡 5 分钟。dsh 一旦恢复调用（哪怕是 `sts2_state`），下一次决策又会回到 dsh 手里。
- 动作归属：`controlled.kind=ai` 时 `sts2_play_card` 等 UI 动作会被拒绝（避免与内置执行器抢同一玩家），只能用 `sts2_decide`。

### 1.2 战斗中的抽牌（v2.1）

内置 AI 的战斗执行器在打完计划牌之后还有一个 Phase 3：用启发式把手里剩下能打的牌全部打掉。对 dsh 来说这一步是错的——出牌、药水抽到的新牌它根本没看见，就被启发式花掉了。现在：

- `controlled.kind=ai` 时 **Phase 3 关闭**（`CombatDecisionExecutor` 判 `AgentDecisionBroker.Enabled`）。每执行完一次决策，只要仍有能量/辉星且手牌里有能打出的牌，`ProcessCombatTurnAsync` 会**再挂一次决策**，prompt 里就是抽完牌之后的新手牌与新牌堆；单回合最多 6 次决策（内置后端仍是 3 次）。
- 计划里显式返回 `{"type":"end_turn"}` 时立即停止追加决策（内置后端也尊重该动作，只是它的 Phase 3 已经先出完剩余牌）。
- `ai_decision` / `ai_busy` 两种快照都带上了 `combat` 段（实时手牌、能量/辉星、敌人、牌堆张数）。执行期间（`ai_busy`）也可以用 `sts2_state` 直接看到抽到的牌；`options` 在 AI 模式下仍为空——出牌要用 `sts2_decide`，不用索引。
- `combat.hand[].cost` 是**当前实际费用**（升级降费、临时费用修正都算进去，如「全身撞击+」= 0）；`upgraded` 表示是否已经升级。`card_reward` / `card_select` / `card_choice` 的 `detail` 末尾会跟一句「升级后: …」，未升级的卡可以直接比较升级收益。
- 决策内容里返回 `tool_calls`（`read_pile` / `card_info` …）同样有效：游戏在主线程执行完只读工具，把结果拼进新 prompt 后**再挂一次决策**，不会像以前那样掉进启发式兜底。图片类工具（`screenshot`）没有图像回传通道，`sts2_screenshot` 请当作独立工具调用。
- 内置 LLM 后端行为不变：仍是每回合最多 3 次决策，Phase 3 照常补牌。

### 1.3 手牌选择、运行控制与人格（v2.2）

- dsh 接管本地玩家时，`CardSelectCmd.FromHand` / `FromHandForUpgrade` 不再打开原版手牌选择 UI（`NPlayerHand.SelectCards` 会一直等真人点击，dsh 无法完成；期间 `play_card` / `end_turn` 还会抢着执行，造成软锁与 ERROR 刷屏）。现在改为在 `AgentDecisionBroker` 挂一个 `kind=hand_select` 决策：`options=[{index, card, upgradable, detail}]`（prompt 里每张牌带费用、当前效果和「升级后」效果，`detail` 与之一致），dsh 用 `sts2_decide {"indices":[...], "reason":"..."}` 回答，模组照原有写法 `SyncLocalChoice` 交回；超时/未回答则启发式兜底，绝不把空选择交给游戏。
- `sts2_play_card` / `sts2_end_turn` 在 `NPlayerHand.IsInCardSelection` 为真时直接拒绝并提示先回答选牌决策。
- 选目标：休息处 MEND（以及少数需要点选目标的药水/遗物）会打开原版 `NTargetManager` 的选目标模式并禁用底层按钮；桥接把这一状态识别为 `phase=target_select`，`options` 是可点的角色/生物（遵守 `NTargetManager` 自己的过滤，例如 MEND 不能选自己），最后一个索引是取消。`sts2_choose index=<n>` 会先 hover 该节点再完成选择，等价于鼠标点击；点取消则让原动作返回失败、休息处重新启用选项。
- 运行控制工具：`sts2_pause` / `sts2_resume` / `sts2_save` / `sts2_quit_to_menu` / `sts2_abandon`。暂停菜单（capstone submenu）不在 overlay 栈里，快照单独识别为 `phase=paused`、不可行动。
- 人格：`AgentDecisionBroker.Enabled` 时 system prompt 不再包含 mod 的人格档案与长期记忆（改为 `prompt.system.external_agent`），人格由 dsh 自己决定；只保留 JSON/语言等输出要求与团队协作原则。

### 1.4 聊天面板由 dsh 回复（v2.3）

dsh 接管时，游戏聊天面板里的玩家发言也交给 dsh，而不是配置的 LLM 后端：

- 玩家在面板发言 → `ChatManager` 记录消息后询问 `AgentChatRelay`：只要**最近 10 秒内有插件在轮询 `sts2_chat_poll`**，就把这条消息放进中继队列、不再请求后端。
- 插件以 `<sts2_chat>` 消息把队列内容注入当前对话，模型用 `sts2_chat_send {text, reply_to}` 回复；mod 走 `ChatManager.AddAiMessage` 渲染，面板、气泡、历史与内置后端完全一致。注入必须走 dsh 的 **`steer()`（next-step）而不是 `followup()`（next-turn）**：自动驾驶时一轮可以持续整局，`followup` 会把聊天排到那一轮结束之后，玩家等不到回复、mod 先超时补答，模型随后又只能从快照猜 `reply_to`——这正是 v2.4.1 之前“dsh 回复不了、变成 mod 自己回”的根因。
- `sts2_chat_send` 可带 `directives`（与内置聊天 JSON 同构）与 `nickname`，所以聊天原有的“记录战术 / 改昵称”副作用不会丢。
- **影响决策的三层保障**：① 消息进入 dsh 的对话上下文（steer 到当前回合）；② `sts2_state` / `sts2_wait` 快照的 `chat` 段带 `recent`、`unread` 与 **`pending[{seq,from,text,age_seconds}]`**，未读时 `notes` 直接指向 `chat.pending` 的 seq，模型不必再猜；`sts2_chat_poll` 只是插件内部使用的桥接方法，**不是注册给模型的 dsh 工具**，所以提示里不再让模型调用它；③ dsh 决策提示词末尾追加 `[玩家最近聊天]`（`DecisionService.AppendRecentChat`），即使对话上下文被压缩，玩家最新的话仍会进入本次决策。
- **能力探测而非版本猜测**：插件只有在绑定了一个 dsh 对话后才轮询（任意 `sts2_*` 工具调用、`sts2_autoplay`、自动接管都会绑定）。旧版插件、没绑定对话的 dsh、桥接关闭时不会产生轮询，聊天自动走回内置后端。
- **回退与 `reply_to` 校验**：超过 `AgentChatReplyTimeoutSeconds`（默认 120 秒）未回复、或客户端断开，`AgentChatFallbackToBackend` 打开时由内置后端补答并在面板注明。`sts2_chat_send` 只在 `reply_to` 命中一条**仍在等待**的记录时把它标记为已回答；`reply_to` 命中不了任何存活记录（被清理、猜错、mod 重启）时整条拒绝并回传仍待回复的 seq 列表，让模型用同一段文字重发——不再“照常渲染但不解决等待”。已由内置模型补答的 seq 再收到 dsh 迟到回复时仍会渲染（结果里注明是迟到回复），因为玩家更愿意看到 dsh 的答案；不带 `reply_to` 且只有一条待回复时自动绑定，多条时按新发言发送并提示还有哪些 seq 待回。
- **不污染决策活性**：`sts2_chat_poll` / `sts2_chat_send` 与 `sts2_takeover` 一样不计入 `AgentBridgeService` 的“dsh 还活着”打点，否则 1 秒一次的聊天轮询会让“dsh 静默回退”永久失效。聊天也不再触发内置的 `TryAutoMessage` 自动闲聊，避免两个声音。
- 只读模式（`AgentBridgeAllowActions=false`）下聊天中继照常工作：聊天不是游戏动作。

### 1.5 主菜单可见、三种开局与程序化建主（v2.4）

v2.3 及以前，只要 `RunManager` 没有局内状态，快照就一律报 `phase=menu`：主菜单的子菜单、多人建房、每日/自定大厅、角色选择全都看不见，dsh 只能盲开标准单人局；而且它还能在别人已经打开的大厅里再起一局单人，把大厅与运行状态一起弄坏。v2.4 把主菜单整层暴露出来：

- 快照读取 `NGame.Instance.MainMenu.SubmenuStack.Peek()`，按栈顶界面给出 `menu` / `char_select` / `daily_lobby` / `custom_lobby` 四个 phase，并带 `menu.screen`（`main` / `solo` / `multiplayer` / `host` / `join_friend` / `other` 等）区分是哪一层。
- 每个界面把**可点元素**枚举进 `options[]`（`kind=button|character|modifier|ai_join|ai_remove`）：主菜单文本按钮、子菜单大按钮、角色选择头像、自定模式词条勾选框、Embark/Back 等；`sts2_choose {index}` 按下它们，`sts2_proceed` 等价返回上一层（有 Back 按钮就按，没有就 `SubmenuStack.Pop()`）。
- 大厅界面额外带 `menu.lobby`：模式、是否房主/单人、玩家列表（名字、角色、是否就绪、是否 AI、是否本地）、`ai_in_lobby`、`can_begin`、种子/进阶/每日服务器时间。
- 主菜单根会返回 `menu.characters` / `max_ascension` / `can_start_run` / `can_continue`，以及 `menu.modes`、`menu.can_start_daily`、`menu.can_start_custom`、`menu.modifiers`（每日/自定开局可选的词条 id 与名称）。
- 有弹窗（`NModalContainer.OpenModal`）时快照 `actionable=false`、`blocked_by=modal`，并在 `notes` 里点名弹窗类型——dsh 不会对着被遮住的按钮乱点。

开局与建房也从“只能标准单人”扩成三模式：

- `sts2_start_run {mode: standard|daily|custom, character, ascension, seed, modifiers[]}`。`daily` 不接收角色/进阶/种子/词条：报名日期取 `TimeServer.FetchDailyTime`（失败时回退本地时间，与游戏一致），种子按 `dd_MM_yyyy_1p` 规范化，角色/进阶/2 好 1 坏词条用与 `NDailyRunScreen.SetupLobbyParams` 相同的 RNG 链复算（`DailyRunCompat`，0.107/0.111 的 RNG 差异隔离在 Compat 里），最终以 `GameMode.Daily` + `dailyTime` 直接开局。`custom` 会校验并解析 `modifiers`（id 或名称；`CharacterCards:<角色id>` 可指定牌组来源），互斥词条会拒绝。
- `sts2_host {mode}`：在干净的主菜单根上打开多人子菜单并 `FastHost(mode)`。**每日/自定大厅没有「添加 AI」按钮，房主一人永远满足不了 `IsAboutToBeginGame`**（多人大厅 1 人拒绝开局），所以 `StartRunLobby.AddLocalHostPlayer` 之后会自动加入 AI 队友并标记 ready；标准大厅仍由角色选择界面的 `ai_join` 选项一键加入 + 准备。
- 开局/建房/续档都要求主菜单根（`SubmenuStack.Peek() == null`）：任何大厅/子菜单打开时都会被拒绝并提示先 `sts2_proceed`。这堵上了“在大厅里误开单人局”的洞。


## 2. 为什么用 dsh 插件而不是 MCP

| 维度 | dsh 插件（本方案） | MCP 服务器 |
|---|---|---|
| 工具名 | 原生 `sts2_play_card` | `mcp__sts2__sts2_play_card` |
| 工具描述/参数 | 与 dsh 工具同源，可做 UI 呈现、并行安全声明、超时策略 | 由 MCP 桥接，能力受 MCP 子集限制 |
| 自动续玩 | `ctx.agents` 主动 `followup()` | 只能靠模型自己循环，或再写一个外部驱动 |
| 生命周期 | `ctx.effect` 自动清理、HMR、配置校验 | 单独进程/端口，需要自行管理 |
| 游戏侧协议 | 极简 TCP 行 JSON（无需实现 MCP/HTTP） | 需要完整 MCP Streamable HTTP |
| 安装 | `dsh plugin add <本地目录>` 或 `--patch` | 手改 cordis.patch.yml |

结论：插件方案代码更少、体验更完整，且完全走 dsh 官方扩展点。

## 3. 总体架构

```
┌──────────────────────────── Windows 游戏进程 ────────────────────────────┐
│  Godot 主线程                         AgentBridge 线程                     │
│  ┌──────────────────────┐  MainThread.Invoke   ┌──────────────────────┐  │
│  │ AgentSnapshotBuilder │ <──────────────────── │ AgentBridgeServer    │  │
│  │ AgentActions         │                       │  TcpListener         │  │
│  │ (UI / 游戏 API)      │ ────────────────────> │  行 JSON 请求/响应    │  │
│  └──────────────────────┘                       └──────────┬───────────┘  │
└────────────────────────────────────────────────────────────┼──────────────┘
                                                             │ 127.0.0.1:<port>
                                                  ┌──────────▼───────────┐
                                                  │ dsh 进程              │
                                                  │  dsh-sts2 插件        │
                                                  │  ├ ctx.tools.register │
                                                  │  ├ TCP 客户端         │
                                                  │  └ autoplay 循环      │
                                                  │  └ ctx.agents.followup│
                                                  └──────────────────────┘
```

- 游戏侧只监听 **127.0.0.1**，不对外网开放。
- 游戏状态读取/动作全部在 Godot 主线程执行；桥接线程只做 socket I/O、等待与 JSON。
- 动作采用**启动后立即返回**模型：工具调用负责“发起动作 + 短轮询观察”，完整推进由 dsh 反复调用 `sts2_wait` / `sts2_state` 完成。
- 插件的 autoplay 循环在游戏可行动、agent 空闲且状态 token 变化时，向 agent 排队一条续玩消息，形成自动对局。

## 4. 游戏侧桥接协议

TCP + UTF-8 行分隔 JSON。每行一个请求，服务器每行一个响应。

请求：

```json
{"id": 7, "token": "<可选>", "call": "sts2_play_card", "args": {"card": "0", "target": "0"}}
```

响应：

```json
{"id": 7, "ok": true, "result": {"text": "{\"phase\":\"combat\",...}"}}
```

错误：

```json
{"id": 7, "ok": false, "error": "No run in progress."}
```

约定：

- `call = "sts2_hello"` 返回 `{name, bridge_version, game_version, allow_actions, port}`，用于连接自检。
- `call = "sts2_takeover"` 是“邀请”轮询（见 6.5）：成功认领时返回
  `{take:true, run_token, prompt, workspace, host, port, character}`，否则 `{take:false, reason}`
  （`disabled | read_only | no_ai_teammate | warmup | already_driving | claimed | exhausted`）。
  这个调用**不计入** dsh 活动时间，否则 4 秒一次的轮询会让“静默回退”永不触发。
- 设置里配置了 token 时，每个请求必须带 `token`，否则 `ok:false`。
- 单条请求上限 1 MB；非法 JSON 返回 `ok:false` 并继续服务。
- 动作工具在游戏侧串行执行（`SemaphoreSlim(1)`）；只读/等待工具可并发。
- 客户端断开时取消进行中的 `sts2_wait`。

## 5. 状态快照（`AgentSnapshot`）

每个工具结果都带同一套 JSON；`sts2_state` 直接返回它，动作工具返回“动作结果 + 最新快照”。

```jsonc
{
  "schema": 1,
  "token": "9f2c1a7b",            // 状态指纹；喂给 sts2_wait 判断“局面变了没”
  "phase": "combat",               // 见下表
  "actionable": true,              // 现在是否轮到本地玩家、且允许外部 Agent 操作
  "blocked_by": "",                // multiplayer | solo_climb | enemy_turn | screen:<Type> | no_run ...
  "mode": "Singleplayer",
  "run": {
    "act": 1, "act_name": "Overgrowth", "floor": 6, "ascension": 0,
    "character": "Ironclad", "hp": 61, "max_hp": 80, "gold": 142,
    "deck_size": 16, "relics": [{"name":"Burning Blood","id":"BurningBlood"}],
    "potions": [{"name":"Fire Potion","id":"FirePotion"}],
    "room": "Monster", "map_row": 4, "map_col": 2
  },
  "combat": {
    "round": 3, "side": "Player",
    "player": { "hp": 61, "max_hp": 80, "block": 5, "energy": 3, "max_energy": 3,
                "stars": 0, "powers": [{"name":"Strength","id":"Strength","amount":2}] },
    "hand": [
      { "index": 0, "name": "Strike", "id": "Strike", "cost": 1, "star_cost": 0,
        "type": "Attack", "upgraded": false, "playable": true, "needs_target": true,
        "description": "造成 6 点伤害。" }
    ],
    "enemies": [
      { "index": 0, "name": "Jaw Worm", "id": "JawWorm", "hp": 40, "max_hp": 44,
        "block": 0, "alive": true, "intent": "伤害11" }
    ],
    "piles": { "draw": 12, "discard": 3, "exhaust": 0, "play": 0 }
  },
  "options": [
    { "index": 0, "kind": "card", "label": "Strike(1费)", "detail": "造成 6 点伤害。", "playable": true }
  ],
  "notes": [
    "It is your turn. Use sts2_play_card with card=<index or name>, then sts2_end_turn."
  ],
  "last_action": { "tool": "sts2_play_card", "ok": true, "detail": "打出 [Strike] → Jaw Worm" }
}
```

### phase 一览

| phase | 来源 | 可用动作 |
|---|---|---|
| `menu` | 没有进行中的存档时的主菜单/子菜单（`menu.screen` 区分 `main` / `solo` / `multiplayer` / `host` / `join_friend` / …） | `choose`（按下枚举出的菜单按钮）；`sts2_start_run` / `sts2_host` / `sts2_continue_run`（仅主菜单根）；`proceed` 返回上一层 |
| `char_select` | 角色选择大厅（单人角色选择或标准多人房的房主/客户端） | `choose`（角色头像、`ai_join`/`ai_remove`、Embark）、`proceed`（返回）；`menu.lobby` 给出玩家/就绪/`can_begin` |
| `daily_lobby` | 每日大厅（`NDailyRunScreen`，含服务器时间加载期） | `choose`（Embark/Back）、`proceed`；`menu.lobby` 带 `seed` / `ascension` / `daily_time` |
| `custom_lobby` | 自定模式大厅（`NCustomRunScreen`） | `choose`（角色、词条勾选、Embark、Back）、`proceed`；`menu.lobby` 带 `seed` / `ascension` |
| `ai_decision` | 有决策挂在 `AgentDecisionBroker` 上（AI 玩家的决策，或本地玩家的 `kind=hand_select` 选牌） | `sts2_decide`（`decision` 里带 prompt/options 与期望 JSON）；`combat` 段是实时局面 |
| `ai_busy` | AI 正在执行上一条决策（出牌/结算房间） | 无；`sts2_wait` 等下一个决策，`sts2_state.combat` 可看实时手牌/抽到的牌 |
| `paused` | 暂停菜单（capstone submenu）打开 | `sts2_resume` / `sts2_save` / `sts2_quit_to_menu` / `sts2_abandon` |
| `combat` | `CombatManager.IsInProgress` | `play_card` / `use_potion` / `discard_potion` / `end_turn` |
| `map` | `NMapScreen.IsOpen` | `choose`；另带 `map` 字段=全幕路线图（与内置 AI 同一份文本） |
| `rewards` | `NRewardsScreen` overlay | `choose`（领取）/ `proceed` |
| `card_reward` | `NCardRewardSelectionScreen` | `choose`（选牌/跳过 alternative） |
| `card_select` | `NCardGridSelectionScreen` 家族 | `select_cards`；单选屏也可 `choose` |
| `card_choice` | `NChooseACardSelectionScreen` | `choose` / `proceed`（跳过） |
| `bundle_select` | `NChooseABundleSelectionScreen` | `choose`（选包/确认/返回） |
| `crystal_sphere` | `NCrystalSphereScreen`（水晶球小游戏） | `choose`（占卜格子/切工具）/ `proceed` |
| `relic_select` | `NChooseARelicSelection` | `choose` / `proceed`（跳过） |
| `shop` | `NMerchantRoom` | `choose`（购买）/ `proceed`（关货架/离开） |
| `rest` | `NRestSiteRoom` | `choose` |
| `target_select` | `NTargetManager.IsInSelection`（休息处 MEND、部分药水/遗物） | `choose`（选中目标；最后一个索引=取消） |
| `event` | 事件房间 | `choose` / `proceed`（继续页） |
| `treasure` | 宝箱房间 | `proceed`（开箱） |
| `room_proceed` | 其他房间，只剩 proceed | `proceed` |
| `game_over` | `NGameOverScreen` | `proceed` |
| `unsupported` | 未识别 overlay | —（提示玩家手动处理） |
| `idle` | 加载、动画、敌方回合等 | —（用 `wait`） |

`actionable` 为 `false` 时，`blocked_by` + `notes` 说明原因。`sts2_wait` 按 `actionable && token != after_token` 唤醒。

### 主菜单 / 大厅（v2.4）

`phase=menu|char_select|daily_lobby|custom_lobby` 时快照带 `menu` 对象：

```jsonc
"menu": {
  "screen": "main",                 // main | solo | multiplayer | host | join_friend | char_select | daily_lobby | custom_lobby | other
  "title": "Main menu",
  "depth": 0,
  "can_start_run": true, "can_start_daily": true, "can_start_custom": true,
  "can_continue": false, "max_ascension": 10, "save": "no saved run",
  "characters": [{"id":"IRONCLAD","name":"铁甲战士"}],
  "modes": ["standard","daily","custom"],
  "modifiers": [{"id":"Draft","name":"牌组草案","kind":"good"}],
  "lobby": {                        // 仅 char_select / daily_lobby / custom_lobby
    "mode": "Daily", "host": true, "singleplayer": false,
    "players": [{"name":"你","character":"铁甲战士","character_id":"IRONCLAD","ready":true,"is_ai":false,"is_local":true}],
    "local": {"name":"你","character":"铁甲战士","character_id":"IRONCLAD","ready":true},
    "ai_in_lobby": true, "can_begin": true,
    "ascension": 4, "seed": "DD_MM_YYYY_2P", "daily_time": "2026-11-01T03:10:00+00:00"
  }
}
```

`options[]` 就是可点列表（`index` 与 `sts2_choose` 一致），元素统一是 `{index, kind, id, label, enabled}`，`kind=modifier` 额外带 `ticked`：

| kind | 含义 | `sts2_choose` 的效果 |
|---|---|---|
| `button` | 菜单/子菜单按钮、Embark/Back、角色选择的普通按钮 | `ForceClick()` / `BaseButton.Pressed` |
| `character` | 角色选择头像（`NCharacterSelectButton`） | `Select()`，等价点选该角色 |
| `modifier` | 自定模式词条勾选框（`NTickbox`） | 切换勾选 |
| `ai_join` | 角色选择界面的「添加 AI 队友并准备」 | `AddAiToLobby` + `SetAiReady(true)` |
| `ai_remove` | 移除大厅里的 AI 队友 | `RemoveAiFromLobby` |

`options` 在每次动作时按当前界面重新枚举；越界会返回错误，dsh 重新 `sts2_state` 即可。主菜单根之外的界面不能用 `sts2_start_run` / `sts2_host` / `sts2_continue_run`（会提示先 `sts2_proceed`）。

### 选项索引

- `options[].index` 是**当前界面内**的稳定序号；动作工具只接受这个序号。
- 索引在每次动作时重新枚举，越界会返回错误和最新快照，让 dsh 自行纠正。
- 地图节点按 `MapCoord` 排序；奖励按钮/卡牌 holder/遗物 holder 按 UI 树顺序；商店按 `MerchantInventory.AllEntries` 过滤后的顺序；事件/篝火按同步器顺序。

### `map` 阶段的全幕路线图

`phase=map` 时快照多一个顶层字段 `map`（字符串），内容就是内置 AI 选路时看到的那段文本 —— `ContextBuilder.DescribeMap` 的原文，两边不会漂移：

```
第1幕地图 | 已访问5个节点
当前位置: 战斗(第5行,第3列)
可前往: 战斗(第6行,第2列)、商店(第6行,第4列)
Boss: Boss(第16行,第3列)
路线图（行: 节点[类型] -> 后续节点坐标）:
第0行: (3,0)远古 ...
第4行: (2,4)战斗√(3,5) (3,4)未知->(4,5)
...
```

所以 dsh 的地图决策不再是“只有下一行能选的几个点”，而是能看到整幕（约 13–16 行 + Boss 行）的节点类型、分叉关系与已访问标记，可以像内置 AI 一样提前几步规划路线。限制也一致：文本有 3200 字符软上限（超了截断末尾几行），且 Boss/精英只给类型不给具体身份。想看同一份数据的工具版可调 `sts2_read subject=map`（`read_map`，另有 4000 字符截断）。

`options` 仍然是**本回合可以点的**节点（`NMapPoint.IsEnabled && State==Travelable`），`sts2_choose index=<n>` 只接受这些序号；`map` 只用于规划，不是选项列表。

## 6. dsh 插件

插件**独立发布**：npm 包 `dsh-sts2`，源码在单独的 GitHub 仓库（`YuWan886/dsh-sts2`），不随 Mod 打包。

### 6.1 形态

一个标准 dsh **组合包（bundle）**，仓库结构：

```
dsh-sts2/                       # 独立 GitHub 仓库
├── package.json                # name: dsh-sts2, dsh.bundle.patch, peerDependencies
├── cordis.patch.yml            # 插入插件行，带默认 host/port
├── index.js                    # 工具注册 + autoplay
├── lib/bridge.js               # 无依赖的 TCP 行 JSON 客户端（可单测）
├── test/protocol.test.mjs      # node:test 协议测试
├── README.md
└── .github/workflows/publish.yml  # tag 触发 npm publish
```

插件入口：

```js
import { defineTool } from '@deepseek-ai/dsh-tools'
import { createUserMessage } from '@deepseek-ai/dsh-llm'

export const name = 'sts2'
export const inject = ['tools']
export const Config = Schema.object({
  host: Schema.string().default('127.0.0.1'),
  port: Schema.number().default(26333),
  token: Schema.string().default(''),
  autoPlayPollMs: Schema.number().default(700),
})
export function apply(ctx, config) { /* 注册工具 + 连接 + autoplay */ }
```

依赖声明遵循 dsh 插件约定：`@deepseek-ai/dsh-tools` / `@deepseek-ai/dsh-llm` 同时放进 `peerDependencies` 与 `devDependencies`，运行时使用 dsh 安装中的副本。

### 6.2 工具

工具直接注册为原生名（无 `mcp__` 前缀）：

| 工具 | 参数 | 说明 |
|---|---|---|
| `sts2_status` | `{}` | 连接自检：`sts2_hello` + 一次快照，返回 CONNECTED / NOT CONNECTED 与排查清单 |
| `sts2_state` | `{}` | 完整快照 |
| `sts2_wait` | `{after_token?, timeout_seconds?}` | 长轮询到可行动或状态变化；默认 25s，最大 50s |
| `sts2_read` | `{subject, pile?, card?, player?, detail?}` | 深读（复用 `AiToolRunner`） |
| `sts2_set_nickname` | `{name}` | 修改 AI 队友显示昵称（≤24 字；空字符串恢复人格名）；名牌、聊天、提示词与联机客户端同步，只读模式也可用 |
| `sts2_screenshot` | `{}` | 截图，原生 image content |
| `sts2_decide` | `{decision}` | **v2**：回答 `phase=ai_decision` 里的挂起决策；内容是 prompt 要求的 JSON（对象或字符串均可） |
| `sts2_chat_poll` | `{after_seq?, wait_seconds?}` | **v2.3**：长轮询玩家聊天（默认等 15s、上限 25s）。返回 `seq` 与 `messages[{seq,from,text,kind,at}]`；无消息时 `wait_timed_out=true`。**插件内部调用，不注册为 dsh 工具**（模型改看快照的 `chat.pending`） |
| `sts2_chat_send` | `{text, reply_to?, directives?, nickname?}` | **v2.3**：在聊天面板发言；`reply_to` 必须是 `chat.pending` / `<sts2_chat>` 里仍在等待的 seq（否则拒绝并回传待回复列表；内置模型已补答的 seq 仍接受为迟到回复），`directives` 记录战术，`nickname` 改显示名 |
| `sts2_start_run` | `{mode?, character?, ascension?, seed?, modifiers?}` | **v2.4**：从主菜单根开一局新的单人游戏。`mode=standard`（默认）/`custom`（自带种子、进阶、角色与词条）/`daily`（时间服务器日期 + `dd_MM_yyyy_1p` 种子 + 按游戏规则摇出的角色/进阶/2 好 1 坏词条；忽略其它参数）。词条用 `state.menu.modifiers` 里的 id 或名称 |
| `sts2_host` | `{mode?}` | **v2.4**：从主菜单根程序化建多人房（`standard` / `daily` / `custom`）。每日/自定大厅自动加入并准备 AI 队友；标准大厅用角色选择界面的 `ai_join` 选项。建好后快照进入 `char_select` / `daily_lobby` / `custom_lobby` |
| `sts2_continue_run` | `{}` | **v2**：读取存档继续（同样要求主菜单根） |
| `sts2_pause` | `{}` | 打开暂停菜单（`NPauseMenu`）；单人爬塔/AI 队友模式下同时挂起自动化。快照变为 `phase=paused`、不可行动 |
| `sts2_resume` | `{}` | 关闭暂停菜单并解除自动化挂起 |
| `sts2_save` | `{}` | 立即写盘当前存档（单人/主机；客户端会被拒绝）。游戏平时在检查点自动存档 |
| `sts2_quit_to_menu` | `{}` | 存档并返回主菜单（等价暂停菜单的 Save and Quit） |
| `sts2_abandon` | `{}` | 放弃当前局并返回主菜单（会删除当前存档）；只允许单人/主机，客户端会被拒绝 |
| `sts2_play_card` | `{card, target?}` | 出牌；`card` 为手牌索引或卡名（`controlled.kind=local` 时可用）。走 `CardModel.TryManualPlay`＝手牌 UI 的同一条路（`PlayCardAction`），会正常扣能量/辉星并触发「打出一张牌」钩子；`CardCmd.AutoPlay` 是「免费打出」，不扣费，不能用于这里 |
| `sts2_use_potion` | `{potion, target?}` | 用药水；`AnyTime` 药水在战斗外也能用 |
| `sts2_discard_potion` | `{potion}` | **v2**：丢弃药水 |
| `sts2_end_turn` | `{}` | 结束回合 |
| `sts2_choose` | `{index, skip?}` | 地图/奖励/卡牌奖励/篝火/事件/商店/遗物/选包/水晶球/选目标；**v2.4** 也按下主菜单/大厅里的按钮、角色头像、自定词条勾选与 `ai_join` |
| `sts2_select_cards` | `{indices}` | `NCardGridSelectionScreen` 家族多选 |
| `sts2_proceed` | `{}` | 继续/离开/开箱/结束界面/确认选包/关闭水晶球；**v2.4** 也从当前菜单/大厅返回上一层 |
| `sts2_autoplay` | `{enabled}` | 开关自动续玩（纯插件侧，不访问游戏） |

工具描述里写清楚当前 phase 对应的用法，模型无需额外学习提示词。

### 6.3 autoplay（真正的“接管”）

`sts2_autoplay {enabled:true}` 时，插件记录调用它的 agent（`exec.agent`），并启动一个 effect 管理的定时器：

1. 每 `autoPlayPollMs` 检查一次游戏状态（直接走桥接，不产生工具历史）。
2. agent 处于 `idle` 且 `state.actionable === true`：
   - 若 `token` 与上次提示不同 → `agent.followup(createUserMessage(...))` 排队一条续玩消息，消息里带紧凑状态；
   - 若 token 相同但已空闲超过 15s → 低频率重发一次（防止模型中途停住）；
3. 游戏进入 `game_over`、连接断开或 `sts2_autoplay {enabled:false}` → 停止。

续玩消息示例：

```xml
<sts2_autoplay>
The game is waiting for you (phase: combat, token: 9f2c1a7b).
Continue with the sts2_* tools. Current state:
{...snapshot json...}
</sts2_autoplay>
```

由于消息来源是 `{kind:'plugin', plugin:'sts2'}`，不会和玩家自己的对话混淆；玩家随时插话会自然抢占当前回合。

### 6.4 连接管理

- 插件加载约 1.5s 后主动探测一次连接（让游戏侧状态行立刻显示已连接），之后按需懒连接；游戏未开/未启用桥接时返回可操作的错误文案。
- 断线后指数退避重连；autoplay 定时器在断开期间静默。
- 每次请求带自增 id，按 id 关联响应；超时可取消。
- 插件卸载时由 `ctx.effect` 关闭 socket 和定时器。

### 6.5 自动接管（`sts2_takeover`）

目标：多人局一出现 AI 队友就自动开一个 dsh 对话接管，省掉“手动开对话 + 说一句来玩杀戮尖塔2”。

桥接是被动监听、不能主动推事件，所以采用**邀请 + 认领**：

1. 游戏侧（`AgentTakeoverInvite`）在 AI 队友入局时开一个窗口，并每局生成 `run_token`。
2. 插件每 `takeoverPollMs`（默认 4s）调用一次 `sts2_takeover`；游戏侧按顺序判断：
   - 自动接管开关是否打开、是否允许操作、是否有 AI 队友；
   - 入局是否已超过 30s（留给玩家手动接管的窗口）；
   - 最近 15s 内有没有 dsh 请求（有 ⇒ 已经有人在驱动，不打扰）；
   - 本局认领次数是否超限（3 次），本次认领是否还在 60s 有效期内。
3. 通过则**认领**并返回渲染好的首条提示词与工作区；`run_token` 保证一个插件进程只为一个对局建一次会话。
4. 插件调用 `ctx.get('sessionController').agents.createSession({cwd, reusable:()=>false})`
   在**当前 dsh 进程里新建一个普通会话**（和 Web UI 点“新建对话”同一条路径），再把工作区注册/挂载到
   `workspaceRegistry`，最后 `agent.followup(createUserMessage({text: prompt, source:{kind:'plugin',plugin:'sts2'}}))`。
5. 新会话第一次调用 `sts2_*` 工具时桥接就会看到活动，游戏侧不再发邀请；玩家在 GUI 里能看、能接手打字。

要点：

- 认领过期（60s 内没有任何 dsh 活动）视为建会话失败，允许插件再试，因此坏掉的会话不会把整局卡死。
- `sts2_takeover` 被排除在“dsh 活动”打点之外，否则轮询会让「dsh 静默回退」永不生效。
- 没有会话控制器的 profile（headless / ACP）里，插件检测到该服务缺失就永久关闭自动接管，不反复重试。
- mod **不启动 dsh**：没有开着的 dsh，就没有人轮询、也没有人认领。

## 7. 动作执行与并发

- 所有动作在主线程**同步校验 + 发起**，然后桥接线程 `await Task.Delay(~900ms)` 并抓取新快照返回。
- 游戏侧动作串行（`SemaphoreSlim`）；只读工具与 `wait` 可并发。
- 出牌/用药水的异步完成复用现有 executor 路径（异常写入 `AiLog` 和 `last_action.detail`）。
- 单人、多人皆可动作：`controlled.kind=local` 时 dsh 驱动本地真人玩家（多人局只动自己那份，别人的回合与投票不受影响）。
- AI 玩家（单人爬塔 / 多人 AI 队友）由 dsh 当大脑：决策走 `sts2_decide`，此时 UI 动作工具被拒绝，避免和内置执行器抢同一个玩家。
- 外部 Agent 自己出牌时不经过 `ProcessCombatTurnAsync`，45 秒回安全网保持原样；当 dsh 当大脑时网络放宽到 330 秒。

## 8. 安全

- 默认**关闭**；开启后仅监听 `127.0.0.1`。
- 可选 Bearer-style token（请求字段 `token`）。
- 只暴露游戏动作，不提供文件/进程/网络代理能力。
- 不读取/回传 API Key、Cookie 等设置。
- 所有动作通过游戏自身 API/UI 节点执行，不修改存档文件。
- dsh 插件以源码/本地 bundle 形式交付，安装时不会执行构建脚本。

## 9. 设置项（RitsuLib）

新增分区「外部 Agent（dsh）」：

| 键 | 默认 | 说明 |
|---|---|---|
| `EnableAgentBridge` | `false` | 启动 TCP 桥接 |
| `AgentBridgePort` | `26333` | 监听端口 |
| `AgentBridgeToken` | `""` | 可选请求 token |
| `AgentBridgeAllowActions` | `true` | 允许动作工具；关闭后只能读 |
| `AgentChatToDsh` | `true` | 由 dsh 回复游戏聊天（仅在插件轮询 `sts2_chat_poll` 时生效） |
| `AgentChatReplyTimeoutSeconds` | `120` | 一条聊天等 dsh 的秒数 |
| `AgentChatFallbackToBackend` | `true` | 超时/断开时由内置后端补答 |

设置页显示运行状态（运行中 / 端口占用 / 错误）和**已连接的 dsh 客户端数**。连接/断开时 `AiLogger` 记录 `[Agent] client connected/disconnected`。由于插件是懒连接，只有 dsh 第一次调用 `sts2_*` 工具后才会看到客户端。

### 9.1 dsh 侧为什么默认“没有端口设置、看不到连接状态”

dsh 应用设置页（**设置 → 插件 → 可配置**）不会自动为任何命名空间生成表单：该页按「Host 注册的设置命名空间」∩「浏览器端注册的卡片」取交集渲染，**一个被服务但没有卡片认领的命名空间什么都不显示**（见 `@deepseek-ai/dsh-client-ui-settings-plugins` 的 `tab-store` 注释）。所以端口与状态要在 dsh 里可见，插件必须自己补齐宿主与浏览器两半：

- 宿主半区 `ctx.settings.register('sts2', Schema.object({host, port, token, ...}), { applies: 'live' })` 声明配置命名空间；`cordis.patch.yml` 的 `config` 作为 `base` 层，设置文档里的用户层覆盖它，所以改设置不动 profile 文件。
- 另注册一个只显示的 `sts2-status` 命名空间（`state` / `endpoint` / `detail` / `updatedAt`），桥接线程在连接、断开、出错时镜像一次状态；设置文档变更本来就推送到浏览器，卡片因此不需要额外 RPC。
- 浏览器半区 `lib/client.js`（手写 ESM bundle；元数据 `dsh.client.platform = 'web'` + `exports['./client']`）把卡片注册进 `settings.plugin.item` 槽，key 为 `sts2`。浏览器启动只预置 `react` / `react/jsx-runtime` / `@deepseek-ai/cordis` / `dsh-client-store` / `dsh-client-ui-slots` / `dsh-client-ui-primitives` / `dsh-client-ui-dockkit` 这些静态模块，所以卡片只用 `require('react')`，其余能力走 cordis 服务（`settingsScope`、`slots`）。
- 客户端 bundle 只在 dsh 启动时扫描并组合进 `window.__DSH_BOOT__`，**改完必须重启 dsh** 才看得到卡片。

### 9.2 游戏内接管标识（聊天面板标题栏）

为了能一眼看出这局是不是 dsh 在开，聊天面板标题栏会在有 dsh 客户端连上时显示一个 **`dsh` 徽标**：

- 徽标在 `AgentBridgeService.ConnectedClients > 0` 时出现；`AgentBridgeAllowActions` 打开时是亮蓝色（接管），关闭时是灰色（只读），tooltip 带客户端数量。
- 面板最小化后的按钮文字同步变成 `AI 队友 · dsh`，收起状态下也能辨认。
- 变化由 `AgentBridgeService.ClientCountChanged` 驱动：它在 `SuperviseTick` 的最前面比对客户端数量（早于设置指纹短路，因此 dsh 连上/断开与设置变更无关），事件在主线程触发，`NGamePatch.OnAgentClientCountChanged` 只在数量真正变化时重算面板可见性。
- `ShouldShowChatPanel` 额外接受「有外部 Agent 连接」，否则普通单人局根本不会创建面板，徽标无处可显示。断开后若既不是单人爬塔也没有 AI 队友，面板会重新隐藏。
- 没有 `AiPlayer` 时面板依然安全：`ChatManager.HandlePlayerMessage` 对 `AiPlayer == null` 做了空值处理（run prelude / 战斗上下文都退化为空串）。


## 10. 安装与使用

### 10.1 安装插件

插件已发布到 npm（包名 `dsh-sts2`），**不需要**随 Mod 分发：

**桌面端（dsh desktop）**：`desktop` profile 由 Electron 应用独占，命令行改不了。在应用内 设置 → 插件 里安装 `dsh-sts2`（npm 包名或本地仓库目录），然后重启应用。

**命令行（web / headless）**：

```sh
# 安装到 web profile
# dsh profile 的 pnpm-workspace 把 profile 自己当作 workspace 根，需要 -w
dsh plugin --profile web add -w dsh-sts2

# 其他 profile 替换 web
dsh plugin --profile <name> add -w dsh-sts2

# 只验证配置层，不启动
dsh --profile web --dump-config
```

安装后 `package.json` 的 `dsh.profile.bundles` 会多出 `dsh-sts2`，其 `cordis.patch.yml` 默认插入：

```yaml
- insert:
    - id: sts2
      name: 'dsh-sts2'
      config:
        host: '127.0.0.1'
        port: 26333
```

本地开发时先给 checkout 安装依赖，再把 checkout 链进 profile：

```sh
cd dsh-sts2 && npm install --registry https://registry.npmjs.org
dsh plugin --profile web add -w /absolute/path/to/dsh-sts2
dsh --profile web --dump-config
```

> 发布后的 npm 安装由 pnpm 正常安装 `dependencies`；`--patch` overlay 要求模块解析能找到 `@deepseek-ai/dsh-*`，优先用 link 方式。

### 10.2 开始接管

1. 游戏内：设置 → 模组设置 → AI 队友 → 外部 Agent，打开开关（端口与插件一致）。
2. 选一种玩法：
   - **自己玩**：开一局普通单人游戏（或让 dsh 用 `sts2_start_run` / `sts2_continue_run` 开局、续档），dsh 用 UI 动作出牌。
   - **给 AI 当大脑**：开「AI 独自爬塔」，或开一局带 AI 队友的多人局。连接后快照进入 `ai_decision`，dsh 用 `sts2_decide` 回答。
3. 在 dsh 里先确认连接：说“sts2 连上了吗？”，dsh 调用 `sts2_status`；返回 CONNECTED 后，游戏设置页的状态行也会显示“已连接 1 个 dsh 客户端”。
4. 在 dsh 里说：
   > 你来玩这局《杀戮尖塔 2》。先调用 `sts2_autoplay` 打开自动接管，然后一直玩到本局结束。
5. dsh 会自己循环：`sts2_wait` → 读状态 → 出牌/选择/回答决策 → 再等待；战斗、地图、奖励、商店、篝火、事件、宝箱、选包、水晶球全部由它处理。
6. 想收回控制权：`sts2_autoplay {enabled:false}`，或直接关闭游戏内开关（AI 会立刻退回内置后端）。

## 11. 验证计划

1. `dotnet build`（0.107.1）与 `dotnet build -p:VariantTarget=0.111.0`（beta）均通过。
2. 插件侧：`npm test`（TCP 协议 7 个用例）、`npm pack --dry-run`、`dsh plugin --profile <tmp> add -w <checkout>` 后用 `--dump-config` 确认 `# == dsh-sts2` 层。
3. 用 `@deepseek-ai/dsh-tools` 的 `defineTool` 实机注册一次工具（fake ctx），确认全部工具与 JSON Schema 都能通过校验。
4. 用 `node -e` 起一个 TCP 客户端，验证 `sts2_hello` / `sts2_state` / `sts2_wait` 的帧格式。
5. 进游戏：开启开关 → dsh 列出工具 → 打一场战斗 → 奖励/地图/商店/篝火/事件/宝箱各走一遍 → autoplay 连续跑 3 个房间。
6. 关闭开关：端口释放，插件工具返回连接错误，游戏不受影响。

## 12. 文件清单

| 文件 | 内容 |
|---|---|
| `AiTeammateCode/Agent/AgentBridgeService.cs` | 生命周期、设置指纹、启动/停止、状态文案 |
| `AiTeammateCode/Agent/AgentBridgeSupervisor.cs` | Godot `Node`，每秒轮询设置 |
| `AiTeammateCode/Agent/AgentBridgeServer.cs` | TcpListener + 行 JSON 协议 |
| `AiTeammateCode/Agent/AgentToolExecutor.cs` | 工具分发、`wait` 循环、结果封装、动作串行 |
| `AiTeammateCode/Agent/AgentSnapshot.cs` | 阶段判定、快照 JSON、token、options |
| `AiTeammateCode/Agent/AgentMenuSupport.cs` | **v2.4**：主菜单/子菜单/大厅的枚举与按下（`menu` 对象、`options`、`TryPress` / `TryGoBack`） |
| `AiTeammateCode/Agent/AgentActions.cs` | 各动作的游戏 API/UI 实现 |
| `AiTeammateCode/Solo/AgentRunLauncher.cs` | **v2.4**：标准/每日/自定单人开局（词条解析、主菜单根守卫） |
| `AiTeammateCode/Compat/DailyRunCompat.cs` | **v2.4**：每日种子/角色/进阶/2 好 1 坏词条复算（0.107/0.111 RNG 差异） |
| `AiTeammateCode/Patches/DailyCustomLobbyAiPatch.cs` | **v2.4**：每日/自定多人房创建时自动加入并准备 AI 队友 |
| `AiTeammateCode/Agent/AgentDecisionBroker.cs` | v2：把 AI 的决策调用挂起，等 dsh 用 `sts2_decide` 回答 |
| `AiTeammateCode/Agent/AgentChatRelay.cs` | v2.3：聊天中继（玩家发言队列、`sts2_chat_poll` / `sts2_chat_send`、能力探测、超时回退） |
| `AiTeammateCode/Settings/AiSettings.cs` | 4 个新设置字段 |
| `AiTeammateCode/Settings/AiSettingsPage.cs` | 新分区、状态段落、重启按钮 |
| `AiTeammateCode/Patches/NGamePatch.cs` | 挂载 supervisor |
| `AiTeammate/localization/{eng,zhs}.json` | 设置/提示文案 |
| `docs/dsh-agent-bridge.md` | 本文档 |

独立仓库 `dsh-sts2`（不在本仓库内）：

| 文件 | 内容 |
|---|---|
| `package.json` | npm 包 / dsh bundle manifest |
| `cordis.patch.yml` | 插件行 |
| `index.js` | 工具注册 + autoplay |
| `lib/bridge.js` | TCP 行 JSON 客户端 |
| `test/protocol.test.mjs` | 协议测试 |
| `.github/workflows/publish.yml` | tag 触发 npm publish |

## 13. 后续可扩展

- 多人模式动作（本地玩家投票/出牌）与观战模式。
- 把动作工具拆成更细的 UI 呈现（dsh 卡片式工具结果）。
- `sts2_say`：把 dsh 的发言显示在游戏聊天面板/气泡里。
- 让插件用 `ctx.goals` 持久化“本局目标”，跨进程恢复接管。
