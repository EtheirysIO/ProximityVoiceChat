# EtheirysProximityVoiceChat

Plugin for [XIVLauncher/Dalamud](https://goatcorp.github.io/).

In-game proximity voice chat with character distance falloff and spatialized stereo audio.

### Features
- Public and private voice rooms
- Customizable distance-volume falloff and spatialized audio
- Individual volume controls
- Mute dead players
- Push-to-talk and noise suppression
- v2 protocol: server-relayed audio with Opus compression (~30x bandwidth reduction vs the mesh-PCM v1 protocol)

## Server

The plugin requires a signaling/relay server.

## Slash commands

- `/evc` — open the plugin window (short form)
- `/etheirysvoicechat` — open the plugin window (long form)

## License

This project is licensed under the GNU Affero General Public License v3.0.

The backend voice infrastructure and related hosted services are not included
in this repository and are not covered by this license.
