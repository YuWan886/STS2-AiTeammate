# AI 游玩质量优化落地计划（2026-11-04）

背景：dsh 驱动 AiTeammate 时的质量评估结论。目标不是换模型，而是补齐信息、执行保真、
反馈闭环与长局上下文。按 P0 → P1 → P2 顺序落地，改完编译双变体并做冒烟。

## P0（低风险、直接收益）

1. **快照 token 去抖**（`Agent/AgentSnapshot.cs`）
   - 计算 token 哈希前剔除易变字段 `decision.age_seconds`。
   - 现状：决策挂起时 token 每 0.1s 变化，`sts2_wait` 不阻塞、autoplay 可能反复催 prompt。
2. **ai_decision 纯文本注入**（`F:\sts2-mod\dsh-sts2-plugin\index.js`）
   - phase=ai_decision 时不再注入整包 state JSON，直接发 `decision.system_prompt` +
     `decision.prompt` + options + 上次决策结果 + 紧凑摘要；token 保留。
   - 其他 phase 保持现状。
3. **原生工具提示**（`Decision/DecisionService.cs`）
   - external-agent 模式下在决策 prompt 末尾列出 `sts2_state / sts2_wait / sts2_read /
     sts2_note / sts2_decide`，提示可先查遗物/牌堆/单卡/地图再答。
4. **去掉内联 tool_calls 干扰**（`Decision/DecisionService.cs`）
   - broker 模式不再追加内置只读工具说明（保留旧执行路径兼容，不主动教）。

## P1（决策质量与执行保真）

5. **遗物信息**（`Decision/ContextBuilder.cs`）
   - broker 模式战斗上下文新增 `遗物:` 行：名称 + 计数 + 截断效果描述（最多 20 条）。
   - 内置后端行为不变。
6. **意图精度**（`Decision/ContextBuilder.cs`、`Agent/AgentSnapshot.cs`）
   - `FormatIntentCompact(move, owner)`：按真实 owner 计算伤害；多段显示 `伤害X×N`；
     补 强减益/塞牌×N/召唤/回血/逃跑/处决/未知 等。
7. **执行顺序**（`Decision/CombatDecisionExecutor.cs`）
   - broker 模式按 dsh 返回的 actions 原序执行（药水仍最先），deferred 仍走协同等待；
     不再拆「先技能后攻击」两阶段。内置后端行为不变。
8. **校验反馈**（`Decision/DecisionValidator.cs`、`Agent/AgentDecisionFeedback.cs` 新增、
   `AiTeammateManager.cs`、`Agent/AgentSnapshot.cs`）
   - Validator 记录被丢弃动作及原因（不在手牌/资源不足/不可打出/药水不存在）。
   - 下一状态快照增加 `last_decision: {id, kind, requested, dropped, valid}`，
     dsh 能知道上一份决策哪些动作没执行。

## P2（长局与速度）

9. **长期笔记**（`Agent/AgentNotes.cs` 新增、`AgentToolExecutor.cs`、`AgentActions.cs`、
   `AgentSnapshot.cs`、`DecisionService.cs`）
   - 新增 `sts2_note {text}`：每局限 20 条、每条 300 字，落盘
     `user://ai_teammate/agent_notes.json`；开局/续档清空。
   - 快照 `ai_notes` + 决策 prompt `[AI 笔记]` 注入。
10. **增量等待**（`Agent/AgentToolExecutor.cs`，插件参数）
    - `sts2_state` / `sts2_wait` 增加 `detail=delta`：只回与上次快照相比变化的顶层字段
      （token/phase/changed/delta）；默认 full 不变。
11. **提速**（插件）
    - `autoPlayPollMs` 默认 700 → 300ms；不影响其他轮询。

## 验证

- `powershell -File tools/build-variants.ps1 -ApiRoot F:/sts2-mod/sts2-versions` 双变体构建。
- `F:\sts2-mod\dsh-sts2-plugin`：`node --check index.js` + `node --test`。
- 启动游戏检查 mod 加载与桥接冒烟（sts2_hello / sts2_state）。
- 插件改动需重启 dsh 才生效（本会话不重启，改动随下次重启生效）。
