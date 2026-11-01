# MiniMax 语音合成（TTS）接入方案

> 目标：让 AI 队友**说的话真的发出声音**——用 MiniMax T2A（语音合成）把聊天面板里的 AI 台词朗读出来，音色可配、可按人格区分、可随时静音，且**绝不阻塞游戏逻辑**。
> 状态：**已实现并部署**（阶段 1–3，2026-11-01）；阶段 4（流式低延迟）未做。范围：`gameplay/AiTeammate`。

---

## 1. 现状与接入点

当前 AI 说话的三条路径最终都汇聚到同一个出口：

```
CombatManagerPatch.OnTurnStarted
  → AiTeammateManager.ProcessCombatTurnAsync
      → ChatManager.AddAiMessage(...)            // 战斗内台词（decision 里的 chat 字段）
      → ChatManager.TryAutoMessage(...)          // 自动搭话（回合开始/低血/胜利…）
ChatManager.HandlePlayerMessage
  → HandlePlayerMessageViaBackendAsync → new ChatMessage → AddRecent + OnMessageReceived
  → AgentChatRelay（dsh 接管）→ ChatManager.ApplyDshReply → AddAiMessage
```

所有消息（`ChatMessage`）都会触发 `ChatManager.OnMessageReceived`，由 `NGamePatch.RouteChatMessage`（主线程，`CallDeferred`）落进聊天面板，AI 的 `Normal` 消息再额外弹世界气泡。

**结论：语音只需要挂 `ChatManager.OnMessageReceived` 一个事件**，不碰后端、不碰决策、不碰任何调用点，就能同时覆盖战斗台词、自动发言、玩家聊天回复、dsh 回复。

可用信息：

- `ChatMessage.IsAi`、`ChatMessage.Type`（`Normal / Suggestion / Warning / SystemInfo`）。
- `ChatMessage.Content` 为最终展示文本（可能含 markdown / emoji / 舞台指示 / 卡名）。
- 现有 `GetSpeechLeadInDelayMs()`（0.45–1.2s）只是"假装打字"的视觉延迟，不涉及音频。

---

## 2. 总体架构

```
                     ┌──────────────────────────────┐
 ChatManager ──event─▶│ TtsService（单例，唯一入口）  │
                     │  文本清洗/切句 → 去重 → 限流    │
                     │  → 预算 → 缓存查询 → 请求队列   │
                     └───────┬───────────────┬──────┘
                             │ miss          │ hit
                             ▼               │
                    ┌────────────────┐       │
                    │ MiniMaxTtsClient│      │
                    │ POST /v1/t2a_v2 │      │
                    └───────┬────────┘       │
                            │ bytes(mp3)     │
                            ▼                ▼
                    ┌────────────────────────────┐
                    │ TtsAudioCache（user://…/voice）│
                    └───────────┬────────────────┘
                                │ 主线程
                                ▼
                    ┌────────────────────────────┐
                    │ TtsPlayer（AudioStreamPlayer│
                    │ 池，Bus=SFX，淡入/打断）      │
                    └────────────────────────────┘
```

线程模型沿用现有约定：

- **HTTP、文本清洗、缓存读写**在后台线程（`Task.Run` / `async`）。
- **Godot 播放**必须回主线程（`Compat/MainThread.Post`）。
- 语音失败**只写日志**，绝不影响聊天、气泡、决策。

---

## 3. 新增文件

```
AiTeammateCode/Voice/
  MiniMaxTtsClient.cs     # HTTP 客户端：合成 + 音色列表；错误码解析
  TtsTextNormalizer.cs    # 台词 → 可朗读文本；切句；语种判断
  TtsAudioCache.cs        # 磁盘缓存（sha1 key + LRU）
  TtsPlayer.cs            # AudioStreamPlayer 池（SFX 总线，打断/淡出）
  TtsService.cs           # 事件订阅、队列、限流、预算、熔断、用量统计
  TtsVoiceCatalog.cs      # 音色列表缓存（设置页下拉 & 试听用）
```

改动文件：

| 文件 | 改动 |
|---|---|
| `Settings/AiSettings.cs` | 新增「语音」字段 + `ResetToDefaults` |
| `Settings/AiSettingsPage.cs` | 新增 `voice` 设置段 + 绑定 + 按钮 |
| `Personality/PersonalityProfile.cs` | 新增 `VoiceId`（空 = 用全局默认） |
| `Personality/PersonalityStore.cs` | 内建人格种子补 `VoiceId`（可选） |
| `AiTeammateManager.cs` | `Initialize()` 末尾 `TtsService.Instance.Attach(ChatManager)` |
| `Patches/NGamePatch.cs` | 标题栏加 🔊/🔇 按钮；（可选）气泡时长跟随音频长度 |
| `ModPaths.cs` | `VoiceDir = user://ai_teammate/voice` |
| `Localization/zhs.json`、`eng.json` | `settings.voice.*`、`voice.*`、`voice.log.*` |
| `CLAUDE.md` | 表格加 `Voice` 一行 |
| `AiTeammate.json` | 版本号 +0.1 |

不需要新资源、不需要改 `.pck`（`has_pck` 保持，只继续装语言 JSON）。

---

## 4. MiniMax T2A API 摘要

### 4.1 接入地址

| 用途 | 地址 |
|---|---|
| 默认（国内） | `https://api.minimax.cn` |
| 华北低延迟（可选） | `https://api-bj.minimaxi.com` |
| 海外账号 | `https://api.minimax.io` |
| WebSocket（阶段二） | `wss://api.minimax.cn/ws/v1/t2a_v2` |

合成接口：`POST {base}/v1/t2a_v2`，鉴权 `Authorization: Bearer <API_KEY>`，`Content-Type: application/json`。

### 4.2 请求

```jsonc
{
  "model": "speech-2.8-turbo",          // 2.8-hd / 2.8-turbo / 2.6-hd / 2.6-turbo / 02-* / 01-*
  "text": "打得不错嘛。这波换我来。",       // < 10000 字符；支持 <#0.3#> 停顿、行内拼音
  "stream": false,
  "output_format": "hex",                // hex | url（url 仅非流式）
  "language_boost": "Chinese",           // auto / Chinese / English / Chinese,Yue / ...
  "voice_setting": {
    "voice_id": "male-qn-qingse",
    "speed": 1.0,                        // [0.5, 2]
    "vol": 1.0,                          // (0, 10]
    "pitch": 0,                          // [-12, 12]
    "emotion": "calm"                    // happy/sad/angry/fearful/disgusted/surprised/calm/fluent/whisper
  },
  "audio_setting": {
    "sample_rate": 32000,
    "bitrate": 128000,
    "format": "mp3",                     // mp3 | pcm | flac | wav | opus | pcmu_*
    "channel": 1
  },
  "text_normalization_mode": "basic",    // basic | quality（quality 仅非流式 + 2.6/2.8）
  "aigc_watermark": false
}
```

### 4.3 响应

```jsonc
{
  "data": { "audio": "<hex>", "status": 2 },
  "extra_info": {
    "audio_length": 6931,          // 毫秒，可用于把气泡时长对齐音频
    "audio_sample_rate": 32000,
    "audio_size": 111789,
    "audio_format": "mp3",
    "usage_characters": 112        // 计费字符数（统计用）
  },
  "trace_id": "...",
  "base_resp": { "status_code": 0, "status_msg": "success" }
}
```

**坑：HTTP 200 不代表成功**，必须判 `base_resp.status_code`：

| code | 含义 | 处理 |
|---|---|---|
| 0 | 成功 | — |
| 1002 | 触发限流 | 退避重试 1 次（1s） |
| 1004 | 鉴权失败 | 日志 + 关闭语音本次会话 |
| 1039 | TPM 限流 | 退避，丢弃本条 |
| 1042 | 非法字符 > 10% | 丢弃本条（清洗逻辑要兜底） |
| 2013 | 参数异常 | 日志 |

### 4.4 音色列表

`POST /v1/get_voice`，body `{"voice_type":"system"}` → `system_voice[]`，每项含 `voice_id`、`voice_name`、`description`。用于设置页「拉取音色」下拉，避免用户手抄 ID。

### 4.5 限制与价格

- **RPM：T2A v2 免费号 10 / 充值号 20**（按账号级）。→ 客户端必须节流，默认 12 RPM 以下。
- 价格：`speech-2.8-turbo` ¥2 / 万字符，`speech-2.8-hd` ¥3.5 / 万字符；**1 个汉字计 2 个字符**，标点/英文/空格各 1 个字符。
- 估算：30 汉字/句 ≈ 60 计费字符 ≈ ¥0.012（turbo）；100 句/局 ≈ ¥1.2/局（hd 约 ¥2.1）。

---

## 5. 核心设计

### 5.1 触发规则

只朗读满足全部条件的消息：

- `message.IsAi == true` 且 `message.Type == ChatMessageType.Normal`；
- 全局开关 `EnableVoice` 打开且 API Key 非空；
- `VoiceChatOnly == false`，或者该消息来自玩家聊天回复（默认 `false`：战斗台词也读）；
- 不在同一时间重复同一条（15 s 内相同文本跳过）；
- 文本清洗后长度 ≥ 2 个可读字符。

跳过：`Suggestion`（AI 思考）、`Warning`、`SystemInfo`、系统提示、错误行。

### 5.2 文本清洗（TtsTextNormalizer）

输入是展示用文本，直接喂 TTS 会读出 `**`、`（笑）`、`【斩击】` 之类。规则：

1. 去 markdown / BBCode：`**粗**`、`*斜*`、`#`、`` ` ` ``、`[color=..]`。
2. 去 emoji（非 BMP 与 emoji 区间）、URL、代码块。
3. 舞台指示映射：`（笑）`/`(笑)`/`*sigh*` → speech-2.8 语气词标签 `(laughs)` / `(sighs)`；其余括号内容直接删除。
4. `【卡名】`→ `卡名`；`……`→ 停顿标记 `<#0.4#>`。
5. 数字/英文保留；纯数字、纯符号、纯空白 → 放弃。
6. 切句：按 `。！？!?；;\n` 切，单条 ≤ `VoiceMaxChars`（默认 120 汉字），最多 2 段（超出丢弃，避免匿名长文）。
7. 语种：含 CJK → `Chinese`；纯 ASCII 字母 → `English`；混排 → `auto`。

### 5.3 请求节流与打断

- **单并发**：同一时刻只有 1 个合成请求在飞（`SemaphoreSlim(1)`）。
- **队列**：最多 3 条；新消息到来时，若队列已满，丢最旧的（保留最新的对话）。
- **去重**：`(voiceId, text)` 15 s 内只播一次。
- **打断**：新台词开始播放时，当前正在播的语音淡出 150 ms 后停止（`VoiceInterrupt` 默认开）；关闭则排队播完。
- **限流**：令牌桶 `VoiceRpm`（默认 12/min），超出排队等待而不是报错。
- **退避**：1002/1039 → 1 s 后重试一次；仍失败 → 本条放弃。
- **熔断**：连续 3 次失败（鉴权/参数/网络）→ 本次会话关闭语音，写一条 toast（"语音已暂停：<原因>"），设置页显示最近错误。

### 5.4 预算

- `VoiceBudgetCharsPerRun`（默认 20000 计费字符 ≈ ¥4 turbo）与 `VoiceBudgetCharsPerHour`（默认 8000）。
- 用量来自响应里的 `usage_characters` 累加；命中缓存不计入。
- 超限：只显示文字 + 一条一次性提示，不报错。
- 设置页状态行：`本局 1,240 字符 ≈ ¥0.25 · 缓存 87 条/12.4 MB · 最近错误：无`。

### 5.5 缓存

- `key = sha1(model|voiceId|speed|pitch|emotion|language|text)`。
- 文件 `user://ai_teammate/voice/<key>.mp3`（`ModPaths.EnsureDir` 建目录）。
- 容量：默认 300 个文件 / 30 MB，LRU 清理；设置页「清空语音缓存」。
- 命中：0 请求、0 延迟；固定台词（"又见面了"）会反复命中。

### 5.6 播放（阶段一，非流式）

```csharp
// 后台线程拿到 mp3 bytes 之后
MainThread.Post(() => {
    var stream = AudioStreamMP3.LoadFromBuffer(bytes);   // PhonkCard 已验证的 API
    var player = TtsPlayer.Get();                        // 池化，父节点挂在 NGame 根
    player.Bus = AudioServer.GetBusIndex("SFX") >= 0 ? "SFX" : "Master";
    player.VolumeDb = Mathf.LinearToDb(VoiceVolumeLinear) + VoiceVolumeDb;
    player.Stream = stream;
    player.Play();
});
```

- ~~走 `SFX` 总线 = 跟随游戏「音效音量」滑块~~ → **已改**：独立 `AiVoice` 总线（详见 §11），游戏「音效」滑块不再影响语音。
- 模组「语音音量」(-24..+24 dB) 直接作用在这条独立总线上，默认 +6 dB；总线串了增益 + 硬限幅。
- 播放器池 2 个，`Finished` 信号归还。
- **不阻塞**：不参与 `GetSpeechLeadInDelayMs` 的等待，也不改变战斗节奏；语音比气泡晚 0.5–2 s 属于正常。
- 可选（阶段三）：拿到 `extra_info.audio_length` 后，把气泡显示时长改成 `max(现有计算, 音频时长)`，让文字陪语音走完。

### 5.7 人格音色

`PersonalityProfile.VoiceId`（空 = 全局默认）→ 解析顺序：

1. 当前人格的 `VoiceId`；
2. `AiSettings.VoiceId`；
3. 内置默认 `male-qn-qingse`。

内置人格建议（可在设置里改）：

| 人格 | 建议音色 | 情绪 |
|---|---|---|
| tactician 战术家 | `Chinese (Mandarin)_Reliable_Executive` | calm |
| aggressive 激进派 | `male-qn-qingse` | auto |
| cautious 谨慎派 | `Chinese (Mandarin)_Lyrical_Voice` | calm |
| chaos-seeker 乐子人 | `female-tianmei`（旧版系统音色，可在设置里换成拉取列表中的任意音色） | happy |

> 音色 ID 只是默认值：设置页「拉取音色」会调 `POST /v1/get_voice` 把账号可用的系统音色列出来，用户自己挑。

情绪再叠一层场景规则：战斗台词 → 人格默认情绪；聊天回复 → `calm`；败局/低血 → `sad`（可选，默认关）。

### 5.8 设置页（独立的「语音」分类页）

> v2.3.2 起设置页按分类拆页：主页只放分类导航，语音单独一页（`AiVoice` 页，id `voice`）。

| 控件 | 字段 | 默认 |
|---|---|---|
| 开关 | `EnableVoice` | 关 |
| API Key（masked，复用 `ApiKeyPlaceholder` 模式） | `MiniMaxApiKey` | — |
| Base URL | `MiniMaxBaseUrl` | `https://api.minimax.cn` |
| 模型下拉 | `VoiceModel` | `speech-2.8-turbo` |
| 音色（拉取下拉 + 手填） | `VoiceId` | `male-qn-qingse` |
| 拉取音色按钮 | → `POST /v1/get_voice` | — |
| 试听按钮 | 合成固定一句并播放 | — |
| 语速 / 音调 / 情绪 | `VoiceSpeed` / `VoicePitch` / `VoiceEmotion` | 1.0 / 0 / auto |
| 只读聊天回复 | `VoiceChatOnly` | 关 |
| 语音音量 (dB) | `VoiceVolumeDb` | +6（v2.3.1 起；独立总线增益，见 §11） |
| 每句最大字数 | `VoiceMaxChars` | 120 |
| 每分钟请求数 | `VoiceRpm` | 12 |
| 每局字符预算 | `VoiceBudgetCharsPerRun` | 20000 |
| 仅房主播放 | `VoiceHostOnly` | 关 |
| 清空缓存按钮 | — | — |
| 状态行 | 用量/缓存/最近错误 | — |

### 5.9 多人 / dsh

- 语音只在**本地**播放，不做网络同步（文本/昵称本来就是各端各自处理）。
- 现状每个客户端都会跑 AI 回合与聊天（LLM 请求也是各自发的），所以语音也会各自请求一次；想省钱可开「仅房主播放」。
- dsh 接管聊天时，回复走 `ChatManager.ApplyDshReply → AddAiMessage → OnMessageReceived`，自动被朗读，无需额外适配。
- 直播弹幕：v2.3.3 起「朗读 AI 自己发出的弹幕」是独立开关（`VoiceReadDanmaku`，默认开，仅在「AI 语音」和「B 站弹幕」都开启时可见）。
  弹幕回复不进聊天面板，走 `TtsService.SpeakDanmaku` 直接进合成管线（同样限流/预算/缓存/熔断）。
  **观众弹幕永远不朗读**：滚屏会盖过游戏解说，还会把每小时字数预算烧光。

---

## 6. 阶段二（可选）：流式低延迟

非流式的首字延迟 ≈ 0.8–2 s（短句）。想要"开口即说"：

```
POST /v1/t2a_v2  { "stream": true, "output_format": "hex",
                   "stream_options": { "exclude_aggregated_audio": true },
                   "audio_setting": { "format": "pcm", "sample_rate": 32000, "channel": 1 } }
→ SSE，每个 data: 一行 JSON，data.audio 为一段 PCM16LE
```

客户端把 PCM 推给 `AudioStreamGenerator`：

```csharp
var gen = new AudioStreamGenerator { MixRate = 32000, BufferLength = 0.2 };
player.Stream = gen; player.Play();
var playback = (AudioStreamGeneratorPlayback)player.GetStreamPlayback();
// 每块：byte[] pcm16le → Vector2 帧（左右同值）→ playback.PushBuffer(frames)
```

首包 ~300–800 ms。更省连接的方式是 WebSocket（`task_start` / `task_continue`，一个连接连说多句），适合连续台词/直播场景。

**建议先做阶段一**：实现简单、可回退、mp3 一条就是完整文件，缓存/统计都好做；延迟敏感再加阶段二，接口与设置项不变，只换 `TtsPlayer` + 客户端解析。

---

## 7. 风险与坑

1. **HTTP 200 假成功**：必须判 `base_resp.status_code`，否则 401 会表现为"静音"。
2. **RPM 只有 10/20**：不加节流时，战斗台词 + 自动发言 + 玩家聊天很容易打满，表现为间歇性无声。
3. **中文里夹英文卡名**：`language_boost=auto` 更稳，但英文缩写可能被读成字母；可接受。
4. **音频比回合长**：默认打断旧句；另在战斗结束/切房时 `StopAll()`，避免语音飘到下一个房间。
5. **Godot 解码**：`AudioStreamMP3` 对 32 kHz mono 128 kbps 没问题；若遇到个别文件解码失败，回退 `format=wav`。
6. **总线**：`SFX` 不存在时回退 `Master`，不要硬写死。
7. **多版本**：语音层只碰 Godot + HttpClient + `ChatManager`，不涉及 0.107/0.111 的 API 差异，两个变体都能编。
8. **部署**：`dotnet build` 会覆盖 `mods/AiTeammate`；改 DLL 必须重启游戏，验证看 `%APPDATA%\SlayTheSpire2\ai_teammate\logs\session.log` 的 `[VOICE]` 行。
9. **隐私**：API Key 与现有 LLM Key 一样明文存 `settings.json`，界面永不回显。

---

## 8. 实施步骤（每个步骤可独立构建验证）

| 步骤 | 内容 | 验收 |
|---|---|---|
| 1 | `Voice/` 骨架 + 设置字段 + 事件订阅 + 非流式客户端 + 播放器 + 日志 | AI 说话能出声 |
| 2 | 文本清洗/切句/去重/限流/预算/熔断 + 静音按钮 + 缓存 | 静音即时生效；错 Key 不刷屏；重复台词命中缓存 |
| 3 | 音色目录 + 人格音色 + 情绪 + 试听 + 用量状态行 | 设置页能选音色、试听、看到本局花费 |
| 4（可选） | 流式 PCM / WebSocket | 首字 < 0.8 s |

工作量估计：步骤 1–3 约 1 个工作日（~600 行 C# + 设置页 ~150 行 + 本地化键），步骤 4 再加半天。

验证清单：

- 单人 AI 爬塔：AI 台词有语音，气泡与语音不同步但可接受；
- 多人房：两人都听得到（或"仅房主"生效）；
- 断网 / 空 Key / 错 Key：聊天正常，日志有 `[VOICE] error=1004`，3 次后自动关；
- 预算耗尽：只出文字 + 一次提示；
- 两变体 `dotnet build` 均通过。

---

## 9. 不做的部分

- 不做语音识别（STT）、不做语音克隆（Cloning 要 ¥9.9/音色，属于账号侧操作，用户在控制台做好后把 voice_id 填进来即可）。
- 不给玩家自己的话配音。
- 不把语音送进联机包、不影响同步与胜负判定。
- 不引入新的 NuGet 依赖（`System.Net.Http` + Godot 自带解码）。

---

## 10. 实现记录（2026-11-01）

### 落地文件

| 文件 | 内容 |
|---|---|
| `AiTeammateCode/Voice/MiniMaxTtsClient.cs` | `POST /v1/t2a_v2` 合成（hex → bytes）+ `POST /v1/get_voice` 音色列表；`base_resp.status_code` 校验；静态 HttpClient（35s 超时） |
| `AiTeammateCode/Voice/TtsTextNormalizer.cs` | markdown/BBCode/emoji/URL 清理、舞台指示 → `(laughs)/(sighs)`、省略号 → `<#0.3#>`、按句切分（≤ 单次字数）、语种判断、计费字符估算 |
| `AiTeammateCode/Voice/TtsAudioCache.cs` | `user://ai_teammate/voice/<sha1>.mp3`；命中即播、LRU 修剪（文件数/大小上限） |
| `AiTeammateCode/Voice/TtsPlayer.cs` | 3 个 `AudioStreamPlayer` 池、`AudioStreamMP3.LoadFromBuffer`、按台词分组排队、150ms 淡出打断 |
| `AiTeammateCode/Voice/TtsAudioBus.cs` | 运行时建独立 `AiVoice` 总线（→ `Master`）：增益（链首 Amplify）+ 硬限幅、音量随设置实时生效；建总线失败时退回 `SFX`/`Master` 并按总线音量反向补偿 |
| `AiTeammateCode/Voice/TtsService.cs` | 订阅 `ChatManager.OnMessageReceived`、`ChatMessage.Source` 过滤、15s 去重、单并发 + 队列 3、RPM 令牌桶、每局/每小时预算、连续 3 次失败熔断、`RunStarted` 重置、用量/费用/缓存状态行、试听与音色拉取 |
| `Chat/ChatMessage.cs` | 新增 `ChatMessageSource`（`Other/Combat/Auto/ChatReply`） |
| `Chat/ChatManager.cs` | `AddAiMessage(content, source)`；聊天回复 = `ChatReply`，自动搭话 = `Auto`，dsh = `ChatReply` |
| `AiTeammateManager.cs` | 战斗台词传 `Combat`；`Initialize()` 末尾 `TtsService.Attach(ChatManager)` |
| `Settings/AiSettings.cs` | 19 个语音字段 + `ResetToDefaults` |
| `Settings/AiSettingsPage.cs` | 「AI 语音（MiniMax TTS）」设置段 + 绑定 + 试听/拉取音色/清缓存/清 Key + 状态行 |
| `Patches/NGamePatch.cs` | 聊天标题栏 ♪ 按钮（切换 `EnableVoice`，1s 定时器同步设置页改动） |
| `Localization/{zhs,eng}.json` | `settings.voice.*` / `voice.*` / `toast.voice_*` / `chat.voice*` 共 67 个键 |
| `ModPaths.cs` | `VoiceDir` |

### 落地时确定的默认值（与产品选择一致）

| 项 | 默认 |
|---|---|
| 语音开关 | 关（需要先填 Key） |
| 只读聊天回复（`VoiceChatOnly`） | **开**：战斗台词与主动搭话默认不读 |
| 每人都播放（`VoiceHostOnly`） | **关**：多人局每个客户端各自播放 |
| 音色 | **全局一个** `male-qn-qingse`；未做按人格分配 |
| 模型 / 语速 / 音调 / 情绪 | `speech-2.8-turbo` / 100% / 0 / auto |
| RPM / 每局预算 / 每小时预算 | 12 / 20000 / 8000 计费字符 |
| 打断 / 缓存 / 缓存上限 | 开 / 开 / 300 文件、30 MB |

### 验证

- `dotnet build`（0.107.1，部署到 `F:\steam\steamapps\common\Slay the Spire 2\mods\AiTeammate`）通过，0 警告 0 错误。
- `dotnet build -p:VariantTarget=0.111.0 -p:Sts2DataDir=... -p:ModsPath=F:\sts2-mod\_tmp_mods` 通过（多版本兼容）。
- 两个本地化 JSON 校验通过（各 403 个键，其中 67 个语音相关）。
- 需进游戏实测：设置页填 Key → 试听 → 聊天面板里对 AI 说一句话 → 听到回复语音；`session.log` 中 `[VOICE]` 行可见 `合成完成 / 缓存命中 / 失败`。

### 已知取舍

- 非流式：首字延迟约 0.8–2s（短句）。语音永远不阻塞决策，所以表现为「气泡先出、声音稍后」。
- 一条台词超过单次字数上限时会拆成最多 2 段：同一条的多个分段排队播完；若期间又来了新台词，旧台词剩下的分段会被丢弃（`IsSuperseded`），不会反过来打断新台词。
- `VoiceHostOnly` 打开后非房主端完全无声（省钱换体验）。
- MiniMax 返回的是完整 MP3，缓存/预算统计都建立在 `usage_characters` 上；缓存命中不计费也不请求。

## 11. 音量修正（2026-11-01，v2.3.1）

### 现象

v2.3.0 首次实测时用户反馈「声音有点小，听不太清楚」。

### 根因（实测数据，不是猜）

游戏的音乐/音效走 **FMOD**，Godot 的 `Master` / `SFX` 总线只服务调试音效，而
`NDebugAudioManager.SetMasterAudioVolume` / `SetSfxAudioVolume` 把玩家的音量滑块按
**线性²** 写进这两条总线。用户的 `settings.save` 是 `volume_master = 0.7`、
`volume_sfx = 0.51`，于是：

- `SFX` 总线：`20·log10(0.51²) ≈ -11.7 dB`
- `Master` 总线：`20·log10(0.7²) ≈ -6.2 dB`
- 合计 **≈ -17.9 dB**（线性约 0.13），TTS 被压到几乎听不见。

再叠加 MiniMax 输出本身的电平（用 `ffmpeg volumedetect/ebur128` 实测同一音色
`Cantonese_KindWoman`、同一模型）：峰值 **-5.3 dBFS**、均值 -19.3 dB、整体 **-19.5 LUFS**
——是「给混音用的干声」，不是「压在游戏音效上的贴脸声」。

### 修法

1. 新增 `TtsAudioBus`：首次播放时 `AudioServer.AddBus()` 建 `AiVoice` 总线，`Send = Master`，
   效果链顺序固定为 `AudioEffectAmplify`（增益，链首）→ `AudioEffectHardLimiter`（ceiling -0.5 dB）。
   增益放在链里而不是总线音量上，这样「放大 → 限幅」的顺序与 Godot 内部实现无关；
   没加压缩器：+6 dB 后 TTS 峰值已接近 0 dBFS，压缩器若无补偿增益反而会把响度压回去。
   语音只吃 `Master`，不再吃 `SFX` 的平方衰减。
2. 语音音量语义改为「独立总线增益」：范围 -24..+24 dB，**默认 +6 dB**（用户旧文件里的 0 会被
   迁移成 6，见下）。+6 dB 后峰值约 0 dBFS，由限幅器吸收，整体约 -19.7 LUFS，属于正常对白电平。
3. 设置迁移：`AiSettings.SettingsSchemaVersion`（旧文件缺字段 → 0），`AiSettingsStore`
   在首次读取时把 v1 升到 v2，`VoiceVolumeDb == 0` 的旧默认值改成新默认 6 并保存。
   玩家之后自己调成 0 不会再被覆盖。
4. 顺带修掉一个真 bug：`TtsAudioCache.Put` 把「MB」当「字节」传给 LRU 修剪
   （`maxBytes = 30`），导致每写入一个文件就被立刻删掉——缓存目录一直是空的。现在按
   `MB × 1024 × 1024` 换算。

### 验证

- 两个变体重新构建部署（0.107.1 / 0.111.0），0 警告 0 错误；DLL 内可见 `TtsAudioBus` /
  `AudioEffectHardLimiter` / `AiVoice`，PCK 内为新文案。
- 音量相关的 `[VOICE]` 日志：建总线时打印 `effects=compressor+limiter, master=..., 旧 SFX 总线=...`，
  之后每次改音量打印 `语音音量 +6 dB（独立通道…）`，每条播放打印 `播放 3.7s (bus AiVoice, +0 dB)`。
- 待实测：重启游戏后 `settings.json` 应出现 `SettingsSchemaVersion: 2`、`VoiceVolumeDb: 6`；
  试听/聊天回复应明显比 v2.3.0 响；缓存目录开始出现 `<sha1>.mp3`。
