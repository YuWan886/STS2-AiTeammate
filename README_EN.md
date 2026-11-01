<div align="center">
  <img style="width: 200px; height: 200px;" src="AiTeammate/mod_image.png" alt="logo" />

  <h1>AI Teammate</h1>
  <p><em>AiTeammate · let an LLM climb the Spire with you</em></p>

  <p>
    <a href="README.md">简体中文</a> | <b>English</b>
  </p>

  <img src="https://count.getloli.com/@Sts2-AiTeammate?name=Sts2-AiTeammate&theme=moebooru&padding=7&offset=0&align=top&scale=1&pixelated=1&darkmode=auto" alt="count" />
</div>

> [!IMPORTANT]
> This mod is developed using AI, there may be errors.

## Installation

1. Download the latest [**Release**](https://github.com/YuWan886/STS2-AiTeammate/releases/latest) (or `git clone` and run `dotnet build` yourself).
2. Install the [**RitsuLib**](https://github.com/BAKAOLC/STS2-RitsuLib) runtime matching your game version.
3. Drop the whole `AiTeammate/` folder into the game's `mods` directory.
4. Launch the game. The loader prints the content variant it picked in the log.
5. Game logs are in the `%AppData%\SlayTheSpire2\logs\` folder.
6. Optional: the [**dsh external agent**](#external-agent-takeover-dsh) needs the extra [`dsh-sts2`](https://github.com/YuWan886/dsh-sts2) plugin.

## Supported Versions

| Game version | Status |
|---|---|
| 0.107.1 (stable) | ✅ Verified in game |
| 0.111.0 (beta) | ✅ Compile/package verified (needs that game version) |

The mod ships as a **multi-version bundle**: `AiTeammate.dll` is the loader, the real logic lives in `lib/<game version>/AiTeammate.Content.dll`, and the loader automatically picks the variant matching the running game.

## Features

- **Multiplayer · AI teammate**: its own personality, independent map votes, coordinated card play, chat, read-only views of teammates.
- **Custom nickname**: name the AI teammate (Settings → AI Identity). It can also rename itself in chat or through dsh; the nameplate, chat, prompts and other multiplayer clients follow immediately.
- **Singleplayer · solo climb**: the AI takes over the local player and handles routing, combat, rewards, shops, rest sites and events; you can interrupt it and talk to it at any time.
- **It can read the board**: combat prompts carry hand, draw, discard and exhaust pile contents (**draw order is never exposed**), plus on-demand read-only tools for piles, relics, potions, teammates and the map.
- **Vision models**: optional screenshot tool so the AI can literally look at the screen.
- **Bilibili live danmaku**: anonymously receive a stream's chat and scroll it over the game; log in to let the AI reply to viewers.
- **AI voice (MiniMax TTS)**: the AI can actually speak; pick a voice, speed and emotion, fetch the voice list from your account, preview it, and cap spending per run/hour. By default only chat replies are read aloud.
- **External agent takeover**: a localhost bridge lets **dsh (DeepSeek Harness)** drive the game directly.
- **Post-run review**: when a run ends (victory or defeat) the AI reviews the deck, route, relics and fights, then writes the reason and the next-run lesson into that personality's long-term memory.
- **Fully localized**: settings page, chat panel, character select, toasts and built-in personalities follow the game language (Simplified Chinese / English).

## Configuration

Everything is configured in **in-game → Settings → Mod Settings → AI Teammate** (RitsuLib provides the settings page); no JSON editing:

- **AI backend**: DeepSeek / OpenAI / Ollama with their own API key, model and base URL; pick models from a drop-down after hitting "Fetch models" (`GET /models` for OpenAI-compatible backends, `/api/tags` for Ollama); optional reasoning mode; "Test connection".
- **Decision**: combat decision timeout, chat timeout, out-of-combat retries; whether pile contents go into prompts; whether the AI may call read-only tools in/out of combat (piles, relics, potions, teammate state, map route; teammates' piles in multiplayer); **allow the AI to see the game screen** (screenshot tool, vision models only).
- **Personality**: select/create/delete profiles; edit backstory, combat philosophy, communication style and decision principles; long-term memory and personality growth toggles (with them on, every finished run is reviewed and saved to memory).
- **Chat**: AI chat, speech bubbles, auto-speak cooldown, prompt chat history.
- **AI voice (MiniMax TTS)**: MiniMax API key, base URL, model, voice id (with a fetch button for the account's voices), speed/pitch/emotion, "only read chat replies", volume, characters per request, requests per minute, per-run/per-hour character budgets, host-only playback, interrupt and cache toggles, preview and clear-cache buttons.
- **Maintenance**: auto/manual content cache refresh, decision/traffic log toggles, log retention, open log folder, reset all settings.

## Multiplayer

- The AI teammate **votes on the map independently**: it reads the whole act map, HP, gold and its deck and decides its own route instead of copying a human vote. Human votes are only a fallback when its decision fails or times out, so the map never stalls.
- It can inspect its own piles, relics and potions, and also **teammates'** hands, piles, gold, relics and potions (read-only, no state changes). Draw order is never revealed.
- Turn off "allow tools in combat" / "allow tools out of combat" to reduce cost.

## Solo climb (singleplayer)

1. **How to start**
   - Enable "AI solo climb (singleplayer)" and **start a normal singleplayer run** — the AI takes over the local player immediately;
   - or click "Start AI solo climb" to open a run straight from the main menu (character picked by "AI character", random by default).
2. **What it does**: routing, combat cards/potions, rewards and card picks, shopping, rest sites, events, chests and act transitions, all decided by the LLM.
3. **Interrupt any time**
   - Sending a chat message interrupts the AI: it stops (keeping the current turn/screen), replies, then resumes after about 3 seconds;
   - the **⏸ / ▶** button in the chat title bar pauses/resumes instantly (same as "Pause / resume AI" in settings);
   - consecutive messages keep extending the pause; the game never hangs while paused, the AI simply waits.
4. **Command it**: instructions in chat ("kill the left one first", "save me a potion") are parsed as tactical directives for the following combat.
5. **Fallback**: for mod-specific minigame screens the AI waits a few seconds and hands over to its built-in automation; if it still cannot manage, it **auto-pauses and tells you to handle it**, then you press ▶ to continue.

> In solo mode you are still the local player: you can click cards / end turns manually at any time (the AI keeps its own pace). Pause with ⏸ first if you want to take over.

## Vision (screenshot tool)

Enable "Allow the AI to see the game screen" under Settings → Decision and the AI may call the `screenshot` tool during combat or out-of-combat decisions: the mod captures the current screen as JPEG (long edge 1024) and attaches it to the next request. It can also be triggered by asking it to "look at the screen" in chat (one extra request). Vision-capable models only; if the model does not support images the API errors out and the AI falls back to plain text decisions without stalling the turn.

## Bilibili live danmaku

The settings page has a "Bilibili live danmaku" section:

1. **Watch only (no login)**: enter the room id (the number after `live.bilibili.com/`, or paste the full URL) and enable "Show live danmaku". The mod connects anonymously to Bilibili's danmaku server and scrolls messages over the game.
2. **Appearance**: by default it shows **on the host's screen only**, scrolls **right → left**, shows avatars and caps each line at **50 characters**. Speed, font size, opacity, vertical coverage, direction and avatars are configurable. Turn off "host screen only" and every client in a multiplayer game connects and renders its own overlay.
3. **Sending requires login**: log into Bilibili in a browser, press F12 → Application → Cookies → `https://www.bilibili.com`, copy `SESSDATA` and `bili_jct` into the matching settings fields, then click "Verify" to see the account name. Cookies are stored in plain text in the local RitsuLib settings file and never uploaded anywhere.
4. **AI replies to chat**: after login the AI may reply to viewers by default (toggleable). It reads recent messages (count configurable), writes one reply with its current personality and a short game-state summary, and sends it with your account; replies are spaced 30 seconds apart by default to avoid spam.
5. **Limits**: normal accounts are limited to roughly 20 characters per danmaku, and sending also depends on account level, room mutes/fan badges and other rules; failures are shown in the settings status line and the log. Bilibili may rate-limit; the client reconnects automatically after a while.

## External agent takeover (dsh)

Let **dsh (DeepSeek Harness)** drive the game directly instead of using it as a plain LLM backend.

1. Install the dsh plugin (separate repo [YuWan886/dsh-sts2](https://github.com/YuWan886/dsh-sts2) / npm package `dsh-sts2`):

   ```sh
   dsh plugin --profile web add -w dsh-sts2
   ```

   Restart dsh.
2. Enable **external agent bridge** in the same settings section, make sure the port (default `26333`) matches the plugin, and fill in an access token if you set one.
3. Start a **singleplayer run** (do not enable "AI solo climb" at the same time) and tell dsh:

   > Play this Slay the Spire 2 run. Call `sts2_autoplay` first to take over, then keep playing until the run ends.

dsh will handle combat, routing, rewards, shops, rest sites, events and chests until the run ends. Call `sts2_autoplay { "enabled": false }` or switch the in-game toggle off to take control back.

- The bridge listens on `127.0.0.1` only and is never exposed to LAN/WAN.
- In multiplayer the **AI teammate**'s card picks, events, shops, rest sites and map votes also go to dsh (`controlled.kind=ai`, answered with `sts2_decide`). If dsh is attached but not driving (autoplay off, nobody asking it to play), the AI does not wait forever: the "Fall back when dsh is silent" setting (default 30s) hands decisions back to the built-in backend as soon as no dsh request arrived for that long, and returns them to dsh as soon as it calls again.
- Protocol reference: [`docs/dsh-agent-bridge.md`](docs/dsh-agent-bridge.md).

## Logs

- Decision log: `user://ai_teammate/logs/session.log` (on by default, toggleable)
- Request/response log: `user://ai_teammate/logs/ai_traffic.log` (off by default)
- "Open log folder" in settings jumps straight there.

On Windows `user://` is usually `%AppData%\SlayTheSpire2\`.

## Data & Privacy

This mod **collects no statistics at all** — no telemetry, no background uploads.

- LLM API keys and Bilibili cookies are stored in plain text in the local RitsuLib settings file and only used when talking to the matching service.
- Bilibili servers are contacted only when you enable the danmaku feature; the external agent bridge listens on `127.0.0.1` only when you enable it.

## Communication

- [**QQ: 752913553**](https://qm.qq.com/q/ohYkDqbe7K)
- [**Discord**](https://discord.gg/tJT3a95Y8y)

## Mod Integration

- [**RitsuLib**](https://github.com/BAKAOLC/STS2-RitsuLib): **required**, provides the settings page and toasts.
- [**HextechRunes**](https://www.bilibili.com/video/BV1AE9xByEuM/): optional soft compatibility; when detected the AI handles Hextech rune picks, forge/relic choices and act-selection confirmations itself (and answers the selection screen during solo climbs), otherwise it skips them.
- **dsh (DeepSeek Harness) + `dsh-sts2`**: optional, lets an external agent take over the game.

## CREDITS

- Framework, settings page and toasts - [RitsuLib](https://github.com/BAKAOLC/STS2-RitsuLib)
- Game - Slay the Spire 2 (Mega Crit)
- LLM backends - [DeepSeek](https://www.deepseek.com/) / [OpenAI](https://openai.com/) / [Ollama](https://ollama.com/)
- External agent plugin - [dsh-sts2](https://github.com/YuWan886/dsh-sts2)
- Bilibili live danmaku - built on Bilibili's public live WebSocket protocol (unofficial API)

## Development

Build, dual-version packaging and code layout are documented in [CLAUDE.md](CLAUDE.md).
