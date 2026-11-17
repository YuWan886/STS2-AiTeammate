# MiniMax 语音合成（TTS）

> 范围：`gameplay/AiTeammate/AiTeammateCode/Voice/` 及其设置/聊天挂点。状态：已实现并部署（非流式 MP3）。本文以代码为准，旧规划稿与实现的出入见 §9。

## 1. 功能与挂点

AI 台词在聊天面板出声：MiniMax T2A 合成，音色/音量/预算可配。语音失败**绝不影响**聊天、气泡、决策，也不占回合预算。

唯一挂点是 `ChatManager.OnMessageReceived`（`AiTeammateManager.Initialize` 末尾 `TtsService.Instance.Attach(ChatManager)`，`AiTeammateManager.cs:114`）。战斗台词、自动搭话、玩家回复、dsh 回复都经过这里，**不需要在各调用点加语音代码**。

`ChatMessage.Source`（`Chat/ChatMessage.cs:17`）决定读什么：

| Source | 产生处 | `VoiceChatOnly=on`（默认） | `=off` |
|---|---|---|---|
| `ChatReply` | `HandlePlayerMessage` / `ApplyDshReply` | ✅ | ✅ |
| `Combat` | 战斗 decision 的 `chat` 字段 | ❌ | ✅ |
| `Auto` | `TryAutoMessage` | ❌ | ✅ |
| `Other` | 其它 AI 行 | ❌ | ✅ |

总过滤：`message.IsAi && message.Type == ChatMessageType.Normal`（`TtsService.cs:126`）→ 系统行、`Suggestion`、`Warning`、`SystemInfo` 永不朗读。

另一入口是直播弹幕：`TtsService.SpeakDanmaku(text)`（`Live/LiveDanmakuService.cs:597`），只读 **AI 自己发出的**弹幕（`VoiceReadDanmaku`）；观众弹幕永远不读——滚屏会盖过解说并烧光预算。

## 2. MiniMax API（实际用到的部分）

`POST {BaseUrl}/v1/t2a_v2`，`Authorization: Bearer <MiniMaxApiKey>`，`Content-Type: application/json`。静态 `HttpClient`（`SocketsHttpHandler`，连接寿命 5min，超时 35s，`MiniMaxTtsClient.cs:58-67`）。默认 `https://api.minimax.cn`（大陆更快可用 `https://api-bj.minimaxi.com`）。

| 请求字段 | 值 |
|---|---|
| `model` | `speech-2.8-turbo`；`KnownModels` 另含 2.8/2.6/02/01 的 turbo·hd 共 8 项 |
| `text` | 清洗后的**单段**文本（§3.1） |
| `stream` / `output_format` | `false` / `hex`（无流式实现） |
| `language_boost` | `Chinese` / `English` / `auto`，由 `DetectLanguageBoost` 现算 |
| `text_normalization_mode` / `aigc_watermark` | `basic` / `false` |
| `voice_setting.voice_id` | `VoiceId`，默认 `male-qn-qingse` |
| `voice_setting.speed` / `pitch` / `vol` | `VoiceSpeedPercent/100`（clamp 0.5–2.0）/ clamp -12–12 / 固定 `1.0` |
| `voice_setting.emotion` | 仅当 `VoiceEmotion != "auto"` 才下发（`KnownEmotions` 10 项） |
| `audio_setting` | `32000 / 128000 / mp3 / channel 1` |

响应：`data.audio`（hex → `Convert.FromHexString`）、`extra_info.audio_length`（毫秒，仅日志）、`extra_info.usage_characters`（计费字符，进预算与费用）。
- **HTTP 200 ≠ 成功**：必须判 `base_resp.status_code`（`TryReadBaseError`，`MiniMaxTtsClient.cs:242`），非 0 时错误串为 `"{code}: {status_msg}"`。代码**不按错误码分支**——1002/1039/1004/1042/2013 全走同一条失败路径（§3.2 熔断），没有退避重试。
- 音色列表：`POST /v1/get_voice`，body `{"voice_type":"all"}`（不是 `system`），依次读 `system_voice` / `voice_cloning` / `voice_generation`，取 `voice_id`+`voice_name`，去重排序进 `TtsVoiceCatalog`。
- 限制与计费：T2A v2 **免费号 10 RPM / 充值号 20 RPM**（账号级），客户端按 `VoiceRequestsPerMinute` 节流；1 个汉字 = 2 字符、其它可见字符 = 1、空白不计（`EstimateBilledChars`）；¥2/万字符（turbo）、¥3.5/万（hd）。

## 3. 管线

```
OnMessageReceived / SpeakDanmaku
  → 触发过滤（§1）→ Clean（长度 <2 丢弃）→ 15s 去重
  → Split(VoiceMaxChars) → 只取前 2 段 → Enqueue（在飞 >3 丢弃本条）
      → 缓存命中？直接播
      → SemaphoreSlim(1) 单飞 → 节流等待 → 预算预留
      → MiniMaxTtsClient → 写缓存 → 主线程 TtsPlayer
```

HTTP、清洗、缓存读写都在线程池，Godot 侧动作经 `Compat/MainThread.Post`。语音**不参与** `GetSpeechLeadInDelayMs` 的气泡节奏，"气泡先出、声音稍后（0.8–2s）"是正常现象。

### 3.1 文本清洗（`TtsTextNormalizer`）

顺序：代码块 → URL → BBCode → `*舞台动作*` → 括号内容 → markdown 符号 → 省略号 → emoji → 书名号/引号 → 空白规整。

- 括号内容（≤12 字）命中 `CueTags` 才保留为语气词：`笑/哈哈/笑死`→`(laughs)`、`轻笑/苦笑`→`(chuckle)`、`叹气`→`(sighs)`，另有 `(coughs)/(clear-throat)/(pant)/(inhale)/(exhale)/(humming)/(emm)`；其余括号内容直接删除。
- **语气词只对 2.8 系模型生效**（`SupportsInterjections`：模型串含 `"2.8"`）；旧模型下括号内容一律丢弃，避免被逐字读出。
- 省略号 → 停顿标记 `<#0.3#>`，首尾标记会被剥掉；清洗后无任何字母/数字（如 `。。。`）或总长 <2 → 丢弃。

切句：按 `。！？!?；;\n` 切，单句超长在 `，、,空格` 处硬切，再贪心合并到 `VoiceMaxChars`（默认 120，clamp 20–300），英文句间补空格。`TtsService` 每条台词**最多只用前 2 段**（`MaxSegmentsPerLine`）。

### 3.2 去重 / 队列 / 节流 / 预算 / 熔断（`TtsService`）

| 机制 | 行为 | 证据 |
|---|---|---|
| 去重 | 与**上一条已朗读文本**相同且 15s 内 → 跳过（只比 text，不含 voiceId） | `TtsService.cs:197` |
| 队列 | `_pending > MaxPending(3)` 时**丢弃新来的这条** | `:217` |
| 单飞 | 合成串行 `SemaphoreSlim(1)`；缓存命中不占锁 | `:256` |
| 节流 | 距上次请求至少 `60/rpm` 秒，不足则 `Task.Delay` 等待（不丢请求） | `:499` |
| 预算 | 请求前按 `EstimateBilledChars` 预留，失败回滚，成功后按 `usage_characters` 校正；`0`=不限 | `:460` |
| 熔断 | 连续 3 次失败 → `_disabledForSession` + toast；成功一次即清零 | `:525` |

每局预算 `VoiceBudgetCharsPerRun`（20000）在 `RunManager.RunStarted` 重置（并清熔断/禁用），每小时窗口 `VoiceBudgetCharsPerHour`（8000）为滚动 1 小时。预算耗尽只写一条 `[VOICE]` 日志并只出文字，不报错。

### 3.3 缓存（`TtsAudioCache`）

`key = sha1hex(Model|VoiceId|SpeedPercent|Pitch|Emotion|LanguageBoost|text)`，小写；文件 `user://ai_teammate/voice/<key>.mp3`（`ModPaths.VoiceDir`）。命中刷新 mtime 作 LRU 依据，零请求零预算。写入后按 `VoiceCacheMaxFiles`（300，clamp 0–5000）与 `VoiceCacheMaxMegabytes`（30，clamp 1–4096，**×1024×1024**）修剪最旧文件；仅在 `VoiceCacheEnabled` 时读写。

### 3.4 播放（`TtsPlayer`）

`AudioStreamMP3.LoadFromBuffer(bytes)` → 宿主节点（`NGame.Instance`，否则 `SceneTree.Root`）下的 `AudioStreamPlayer` 池（**3 个**，`ProcessMode=Always`，`Finished` 事件续播）。

- 同一条台词（同 `lineId`）分段排队播完（每行最多再排 2 段）；新台词到达清空队列，若 `VoiceInterrupt`（默认开）当前句 150ms 淡出（`volume_db → -50`）后停。
- 慢请求返回时若已被新台词顶替（`IsSuperseded`，仅 interrupt 开时生效）直接丢弃，不会反过来掐断新语音。
- `player.Bus = TtsAudioBus.Ensure(VoiceVolumeDb)`，`player.VolumeDb = TtsAudioBus.PlayerVolumeDb`（正常 0）：音量只在总线上。
- `StopAll()` 只由「关闭语音开关」和聊天栏 ♪ 按钮调用，**没有**在战斗结束/切房时自动停（§7）。

### 3.5 独立 `AiVoice` 总线与响度（`TtsAudioBus`）

**为什么不能用 `SFX` 总线**：游戏自身音频走 FMOD，Godot 的 `Master`/`SFX` 只服务调试音效；而 `NDebugAudioManager.SetMasterAudioVolume`/`SetSfxAudioVolume` 把玩家滑块按**线性²** 写进这两条总线。实测 `volume_master=0.7`、`volume_sfx=0.51` 时 `SFX ≈ -11.7 dB`、`Master ≈ -6.2 dB`，合计 **≈ -17.9 dB**，语音几乎听不见。MiniMax 输出本身也只有 **-19.5 LUFS / -5.3 dBFS 峰值**（给混音用的干声），所以默认必须是**正增益**。

```
AiVoice ─▶ AudioEffectAmplify（增益，链首，clamp -24..+24 dB）
        ─▶ AudioEffectHardLimiter（ceiling -0.5 dB, PreGain 0, Release 0.1）
        ─▶ Master
```

- 总线首次播放时 `AudioServer.AddBus` 创建，`Send = Master`，总线音量固定 0；增益放在**效果链最前面**而不是总线音量上，保证顺序恒为「放大 → 限幅」。
- **故意不加压缩器**：+6 dB 已接近 0 dBFS，压缩器无补偿增益反而把响度压回去；硬限幅才是唯一需要的安全网（+24 dB 也不炸）。
- 设置变更只改 Amplify（变化 ≥0.5 dB 才动，避免刷日志）；同名总线已存在但无我们的 Amplify 时改用总线音量。
- 降级（建总线失败）：退回 `SFX`（否则 `Master`），`PlayerVolumeDb = db - 总线dB`，让设置里的数字含义不变。

## 4. 设置（`Settings/AiSettings.cs` + `AiSettingsPage.ConfigureVoice`）

独立分类页 `voice`（`RegisterCategoryPage("voice", 50, …)`，标题 "AI Voice (MiniMax TTS)"），除总开关外所有行仅在其打开时可见；弹幕行还要求 `EnableBilibiliDanmaku`。

| 键 | 默认 | 说明 |
|---|---|---|
| `EnableVoice` | `false` | 总开关；聊天栏 ♪ 按钮同一字段（1s 轮询同步），关闭时 `StopAll()` |
| `MiniMaxApiKey` | `""` | 只写入不回显；有「清空 Key」按钮 |
| `MiniMaxBaseUrl` | `https://api.minimax.cn` | |
| `VoiceModel` | `speech-2.8-turbo` | 下拉，`KnownModels` |
| `VoiceId` | `male-qn-qingse` | 文本框 +「拉取音色」下拉 |
| `VoiceSpeedPercent` / `VoicePitch` | `100` / `0` | 50–200 / -12–12 |
| `VoiceEmotion` | `auto` | `KnownEmotions` |
| `VoiceVolumeDb` | `+6` | -24–+24，独立总线增益 |
| `VoiceChatOnly` | `true` | 只读玩家聊天回复 |
| `VoiceReadDanmaku` | `true` | 读 AI 自己的直播弹幕 |
| `VoiceMaxChars` | `120` | 单次请求字数 |
| `VoiceRequestsPerMinute` | `12` | 客户端节流 |
| `VoiceBudgetCharsPerRun` / `VoiceBudgetCharsPerHour` | `20000` / `8000` | 0 = 不限 |
| `VoiceHostOnly` | `false` | 仅房主发声（省钱） |
| `VoiceInterrupt` | `true` | 新台词打断旧台词 |
| `VoiceCacheEnabled` | `true` | |
| `VoiceCacheMaxFiles` / `VoiceCacheMaxMegabytes` | `300` / `30` | |

按钮：拉取音色（`FetchVoicesAsync`）、试听（`PreviewAsync`，固定一句并走缓存）、清空缓存（`TtsAudioCache.Clear`）、清空 Key。状态行 `DescribeStatus()`：`本局 N 字符（≈¥x，本小时 M）· 缓存 a 条 / b MB · 最近错误：…`，被熔断时前缀「本次游戏已自动关闭语音（连续失败）」。

**设置迁移**：`AiSettings.CurrentSchemaVersion = 2`；`AiSettingsStore.EnsureMigrated` 读到旧版本（缺字段 → 0）时，仅当 `VoiceVolumeDb == 0` 改成新默认 `6` 并落盘，再写回版本号——玩家之后自己调成 0 不会再被覆盖。

## 5. 多人 / dsh

- 语音完全**本地**：不进联机包、不同步、不影响胜负。每个客户端各自合成（= 每个听众一次计费请求）。
- `VoiceHostOnly=true` 时 `RunManager.Instance.NetService.Type == NetGameType.Client` 一侧直接跳过（`TtsService.cs:142`），房主/单人照常。
- dsh 接管聊天时回复走 `ApplyDshReply → AddAiMessage(..., ChatReply)`，自动被朗读，无需适配。

## 6. 失败模式（全部只写日志）

| 情形 | 结果 |
|---|---|
| Key 为空 / 开关关 | 静默跳过（`TtsService.cs:133-136`） |
| HTTP 非 2xx | `HTTP {code}: {body 前 160 字}` |
| `base_resp.status_code != 0` | `"{code}: {msg}"` |
| 无 `data.audio` / 空 / 非 hex | `no audio data` / `empty audio` / `not valid hex` |
| 超时 / 取消 | 35s HttpClient 超时 → `request failed` |
| MP3 解码失败 | `[VOICE] MP3 解码失败`，本条放弃（不回退 wav） |
| 连续 3 次失败 | 本次游戏关闭语音 + toast；新对局或成功一次即恢复 |
| 预算耗尽 | 只出文字，一条日志 |

失败**不会**出现在聊天面板，排查只能看设置页状态行的「最近错误」或 `%APPDATA%\SlayTheSpire2\ai_teammate\logs\session.log` 的 `[VOICE]` 行。

## 7. 已知坑 / 回归红线

1. **HTTP 200 假成功**：不判 `base_resp.status_code` 会把鉴权失败表现成「静音」。
2. **绝不改回 `SFX` 总线**：会重新吃到 `slider²` 的 ≈-18 dB，听感就是"没声"。
3. **没有自动 `StopAll`**：战斗结束/切房时语音可能飘进下一个房间；若要做，挂明确的房间/战斗结束事件，别改 `TtsPlayer` 语义。
4. **无重试**：1002/1039 只记失败，靠 `VoiceRequestsPerMinute` 提前节流；加节流别改成丢请求（当前是等待）。
5. **单音色**：`VoiceId` 全局唯一，`PersonalityProfile` 无 `VoiceId`，也没有「按场景换情绪」——`VoiceEmotion` 是固定值。
6. **设置页文案与实现不一致**：音量描述写「compressor + limiter」（`AiSettingsPage.cs:585`），实际是 `Amplify + HardLimiter`，没有压缩器。
7. **分段上限**：一条台词最多 2 段，超长回复的后续句子会丢（省钱优先）。
8. **缓存 MB bug（已修）**：`Put` 曾把 MB 当字节传给修剪（`maxBytes = 30`），每个文件写完即被删；现在必须 `×1024×1024`。
9. **部署**：`dotnet build` 会覆盖 `mods/AiTeammate`，换 DLL 必须重启游戏；语音层只碰 Godot + HttpClient + ChatManager，各版本变体都可编。

## 8. 文件地图

| 文件 | 职责 |
|---|---|
| `Voice/MiniMaxTtsClient.cs` | `/v1/t2a_v2` 合成、`/v1/get_voice` 音色、`base_resp` 校验、静态 HttpClient |
| `Voice/TtsService.cs` | 订阅、过滤、去重、节流、预算、熔断、试听/拉取、状态行；**`TtsVoiceCatalog` 在本文件末尾（`:584`）** |
| `Voice/TtsTextNormalizer.cs` | 清洗、语气词、切句、语种判断、计费字符估算 |
| `Voice/TtsAudioCache.cs` | sha1 键、`user://ai_teammate/voice/*.mp3`、LRU 修剪、统计/清空 |
| `Voice/TtsPlayer.cs` | 3 个播放器池、`LoadFromBuffer`、按行排队、150ms 淡出打断 |
| `Voice/TtsAudioBus.cs` | `AiVoice` 总线（Amplify + HardLimiter）、音量、降级路径 |

改动点：`Chat/ChatMessage.cs`（`ChatMessageSource`）、`Chat/ChatManager.cs`（`AddAiMessage(content, source)`）、`AiTeammateManager.cs`（`Attach`、战斗台词传 `Combat`）、`Settings/AiSettings(.Store/Page).cs`、`Patches/NGamePatch.cs`（♪）、`Live/LiveDanmakuService.cs`（`SpeakDanmaku`）、`ModPaths.VoiceDir`、本地化 JSON。

## 9. 旧文档勘误（以代码为准）

| 旧说法 | 实际 | 证据 |
|---|---|---|
| `VoiceChatOnly` 默认关 | 默认**开** | `AiSettings.cs:197` |
| `VoiceRpm` / `VoiceSpeed` | `VoiceRequestsPerMinute` / `VoiceSpeedPercent` | `AiSettings.cs:210,178` |
| 队列满丢最旧 | 丢**新来的**这条 | `TtsService.cs:217-221` |
| 令牌桶限流 | 均匀最小间隔 `60/rpm`，等待不丢弃 | `TtsService.cs:499-523` |
| 1002/1039 退避 1s 重试 | 无按码重试，直接记失败 | `TtsService.cs:271-276` |
| 去重键 `(voiceId, text)` | 只比 `text` | `TtsService.cs:197` |
| 播放器池 2 个 / `Bus="SFX"` | 3 个 / 独立 `AiVoice` 总线 | `TtsPlayer.cs:19,112-120` |
| 省略号 → `<#0.4#>` | `<#0.3#>` | `TtsTextNormalizer.cs:15` |
| 按人格分配 `VoiceId`、败局切 `sad` | 均未实现，单音色/单情绪 | grep `VoiceId` 无命中；`:456` |
| `TtsVoiceCatalog.cs` 独立文件 | 在 `TtsService.cs:584` 内 | 同左 |
| `get_voice` body `voice_type:"system"` | `"all"`，另读 cloned/generation | `MiniMaxTtsClient.cs:181,212` |
| 战斗结束/切房调用 `StopAll()` | 无此调用，只有总开关与 ♪ | grep `StopAll` |
| 19 个语音字段 | 20 个 | `AiSettings.cs:157-235` |
