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
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Ninject;
using Ninject.Extensions.Factory;
using EtheirysProximityVoiceChat.Log;
using EtheirysProximityVoiceChat.Ninject;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WebRtcVadSharp;

namespace EtheirysProximityVoiceChat;

public sealed class PluginInitializer : IDalamudPlugin
{
    public static string Name => "EtheirysProximityVoiceChat";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get ; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IAddonEventManager AddonEventManager { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly StandardKernel kernel;

    public PluginInitializer()
    {
        // For whatever reason this is needed to load certain dlls
        NativeLibrary.SetDllImportResolver(typeof(WebRtcVad).Assembly, (_, assembly, path) => NativeLibrary.Load("WebRtcVad.dll", assembly, path));

        this.kernel = new StandardKernel(new PluginModule(), new FuncModule());

        // Logging
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Entrypoint
        this.kernel.Get<Plugin>().Initialize();
    }

    public void Dispose()
    {
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        this.kernel.Dispose();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
#if DEBUG
        // There's a number of unobserved Task exceptions that can be thrown by 3rd party libraries
        // in this project, some of which seem to be unavoidable but harmless.
        this.kernel.Get<ILogger>().Error(e.Exception.ToString());
#else
        // So, lower the severity of the log in release builds.
        this.kernel.Get<ILogger>().Trace(e.Exception.ToString());
#endif
        e.SetObserved();
    }
}
