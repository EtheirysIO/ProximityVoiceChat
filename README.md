# EtheirysProximityVoiceChat

Plugin for [XIVLauncher/Dalamud](https://goatcorp.github.io/).

In-game proximity voice chat with character distance falloff and spatialized stereo audio.

### Features

**Voice rooms**
- Public auto-joining rooms per map / instance
- Private rooms (v4) — free-form names per current world, optional password, "Unlisted" toggle, inline browse list with click-to-fill
- Per-room owner / moderator model — owner appoints mods, owner / mods can kick or ban, mods can't act against the owner
- 24-player cap per private room, 24-hour cleanup of empty rooms
- Live server "Users Online" indicator under your name
- Automatic reconnect on dropped connections
- Live connection-strength indicator with latency tooltip
- Session resume across plugin updates (within the same game session)

**Spatial audio**
- Customizable distance-volume falloff and spatialized stereo
- Hard cutoff at the configured maximum distance — peers outside that range are silent
- Mute dead players (configurable delay)
- Master volume up to 500%
- Per-peer local mute / volume (right-click a player in the roster)
- Roster auto-sorted by distance; out-of-range peers grayed out

**Microphone**
- WASAPI shared-mode capture for low latency and broad device compatibility
- Live input-level meter with clipping warning
- Adjustable input boost (0–500%)
- Push-to-talk (with optional release delay) or voice-activated
- Noise suppression (RNNoise) + WebRTC voice-activity gating with tunable sensitivity

**Transport (v4)**
- UDP audio with AES-128-GCM encryption per session
- Automatic TCP fallback if UDP is blocked (corporate firewalls, restrictive ISPs)
- Opus codec at 48 kbps with forward error correction for packet-loss recovery
- Adaptive per-peer jitter buffer (smaller on UDP, larger on TCP fallback)
- ~30x less bandwidth than the original mesh-WebRTC PCM design (v1)
- Mixed-version rooms supported — server cross-stitches v2 / v3 / v4 clients

## Server

The plugin requires a signaling/relay server.

## Slash commands

- `/evc` — open the plugin window (short form)
- `/etheirysvoicechat` — open the plugin window (long form)

## License

This project is licensed under the GNU Affero General Public License v3.0.

The backend voice infrastructure and related hosted services are not included
in this repository and are not covered by this license.
