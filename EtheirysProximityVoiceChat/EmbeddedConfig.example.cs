/*
 * Copyright (c) 2026 Noah Dolph
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
 *
 * See the GNU Affero General Public License for more details.
 */
// COPY THIS FILE to `EmbeddedConfig.cs` (next to it) and fill in real values.
// `EmbeddedConfig.cs` is gitignored so the maintainer's live signaling URL
// and token never end up in source history. The csproj excludes this
// template file from the build (<Compile Remove="EmbeddedConfig.example.cs" />),
// so the two files can coexist on a contributor's machine without colliding.

namespace EtheirysProximityVoiceChat;

internal static class EmbeddedConfig
{
    public const string SignalingServerUrl   = "https://example.invalid";
    public const string SignalingServerToken = "REPLACE_WITH_TOKEN";
}
