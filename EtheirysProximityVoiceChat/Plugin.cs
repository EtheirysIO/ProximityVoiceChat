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
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using EtheirysProximityVoiceChat.Log;

namespace EtheirysProximityVoiceChat;

public class Plugin(
    IDalamudPluginInterface pluginInterface,
    IEnumerable<IDalamudHook> dalamudHooks,
    Spatializer spatializer,
    ILogger logger)
{
    private IDalamudPluginInterface PluginInterface { get; init; } = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
    private IEnumerable<IDalamudHook> DalamudHooks { get; init; } = dalamudHooks ?? throw new ArgumentNullException(nameof(dalamudHooks));
    private Spatializer Spatializer { get; init; } = spatializer;
    private ILogger Logger { get; init; } = logger ?? throw new ArgumentNullException(nameof(logger));

    public void Initialize()
    {
        foreach (var dalamudHook in this.DalamudHooks)
        {
            dalamudHook.HookToDalamud();
        }

        this.Spatializer.StartUpdateLoop();

        Logger.Info("EtheirysProximityVoiceChat initialized");
    }
}
