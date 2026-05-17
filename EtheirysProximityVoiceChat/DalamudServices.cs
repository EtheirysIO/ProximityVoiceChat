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
using Dalamud.Plugin.Services;

namespace EtheirysProximityVoiceChat;

public class DalamudServices
{
#pragma warning disable CA1822 // Mark members as static
    public IDalamudPluginInterface PluginInterface => PluginInitializer.PluginInterface;
    public ICommandManager CommandManager => PluginInitializer.CommandManager;
    public IClientState ClientState => PluginInitializer.ClientState;
    public IPlayerState PlayerState => PluginInitializer.PlayerState;
    public IChatGui ChatGui => PluginInitializer.ChatGui;
    public ICondition Condition => PluginInitializer.Condition;
    public IDutyState DutyState => PluginInitializer.DutyState;
    public IDataManager DataManager => PluginInitializer.DataManager;
    public IObjectTable ObjectTable => PluginInitializer.ObjectTable;
    public IGameGui GameGui => PluginInitializer.GameGui;
    public IAddonEventManager AddonEventManager => PluginInitializer.AddonEventManager;
    public IAddonLifecycle AddonLifecycle => PluginInitializer.AddonLifecycle;
    public IFramework Framework => PluginInitializer.Framework;
    public ITextureProvider TextureProvider => PluginInitializer.TextureProvider;
    public IPluginLog Log => PluginInitializer.Log;
#pragma warning restore CA1822 // Mark members as static
}
