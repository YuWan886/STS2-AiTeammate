# B 站直播弹幕

`LiveDanmakuService` 由 `NGamePatch.AfterGameReady` 挂到 `NGame` 根节点，随后按设置自我管理（0.5s 轮询）：关闭、没配房间号、或 `DanmakuHostOnly` 开着而本地客户端不是房主（`NetGameType.Client`）时停止；房间号/cookie 变化会重启连接；断线 5s 后重试并轮换 host。

## 连接与鉴权

- 匿名接弹幕：`BilibiliApi` 从 `finger/spi` 取 `buvid3/buvid4`，从 `nav` 取 WBI key，签名 `getDanmuInfo`（`w_rid` / `wts`），然后 `BilibiliLiveClient` 用 `uid:0` 鉴权。
- 发弹幕需要 `SESSDATA + bili_jct`；设置项写-only（从不回显），`LiveDanmakuService.VerifyLoginAsync` 报告账号名。
- `BilibiliLiveClient` 包 Godot `WebSocketPeer`：主线程轮询、op 7 鉴权、30s 心跳、zlib/brotli 解包。`RoomEntered` 只在 op=8 鉴权回复 `code:0` 后触发。

## 坑（都验证过）

- **WBI 签名必须把置换后的 key 截到 32 字符**：用完整 64 字符会算出错的 `w_rid`，B 站回 `-352`（风控）。
- `getDanmuInfo` 仍失败时，`GetDanmuInfoAsync` 回落到不签名、不带 cookie 的旧接口 `room/v1/Danmu/getConf`。
- 握手结果（真实房间号 + token + hosts + 登录态）缓存 5 分钟，避免 socket 重连反复打风控接口；连续两次断线则作废缓存。
- 进房后立刻发一次心跳，45s 内没有心跳回复就重连——半开连接否则会静默挂住。
- 连接成功用 RitsuLib toast（`RitsuToastService.ShowInfo`）提示房间号与 AI 能否回复，30s 冷却，避免重连循环刷屏。

## 悬浮层（`DanmakuOverlay`）

全屏 `Control`，默认：仅房主、从左到右、显示头像、50 字符、占屏高 55%。弹幕进队列（有上限）并塞进空闲轨道；头像贴图在主线程下载并缓存（jpg/png/webp）。

## AI 弹幕互动

- 用当前人格 + 主线程构建的简短局势摘要生成回复，POST 到 `msg/send`；**发出的文本截到 20 字符**，B 站会拒绝更长的普通弹幕。
- 批量/冷却由 `DanmakuAiChatBatchSize`（6）与 `DanmakuAiChatCooldownMs`（30s）控制；总开关 `EnableDanmakuAiChat`。
- 语音开启且 `VoiceReadDanmaku` 时，AI 自己的弹幕回复会被读出来（观众的弹幕故意不读），见 [minimax-tts.md](minimax-tts.md)。

## 相关设置

`EnableBilibiliDanmaku`、`BilibiliRoomId`、`BilibiliSessData`、`BilibiliBiliJct`、`DanmakuMaxChars`、`DanmakuSpeed`、`DanmakuFontSize`、`DanmakuOpacity`、`DanmakuShowAvatar`、`DanmakuHostOnly`、`DanmakuAreaPercent`、`EnableDanmakuAiChat`、`DanmakuAiChatCooldownMs`、`DanmakuAiChatBatchSize`。
