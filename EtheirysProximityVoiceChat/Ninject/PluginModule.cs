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
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Ninject.Activation;
using Ninject.Extensions.Factory;
using Ninject.Modules;
using EtheirysProximityVoiceChat.Audio;
using EtheirysProximityVoiceChat.Input;
using EtheirysProximityVoiceChat.Log;
using EtheirysProximityVoiceChat.Premium;
using EtheirysProximityVoiceChat.UI;
using EtheirysProximityVoiceChat.UI.Presenter;
using EtheirysProximityVoiceChat.UI.View;
using EtheirysProximityVoiceChat.WebRTC;

namespace EtheirysProximityVoiceChat.Ninject;

public class PluginModule : NinjectModule
{
    public override void Load()
    {
        // Dalamud services exposed individually for the small handful of
        // ctor-injected consumers below. Most plugin code should depend on
        // DalamudServices (bound just below) instead; these per-service
        // bindings exist only because the listed consumers haven't been
        // migrated yet. Current consumers as of v1.1.4:
        //   IDalamudPluginInterface — Plugin, PluginUIContainer, Configuration.Initialize
        //   ICommandManager         — CommandDispatcher
        //   IChatGui                — DalamudLogger
        //   IFramework              — ConfigWindowPresenter
        //   IPluginLog              — DalamudLogger
        // Future cleanup: migrate each consumer to DalamudServices and remove
        // the matching binding.
        Bind<IDalamudPluginInterface>().ToConstant(PluginInitializer.PluginInterface).InTransientScope();
        Bind<ICommandManager>().ToConstant(PluginInitializer.CommandManager).InTransientScope();
        Bind<IChatGui>().ToConstant(PluginInitializer.ChatGui).InTransientScope();
        Bind<IFramework>().ToConstant(PluginInitializer.Framework).InTransientScope();
        Bind<IPluginLog>().ToConstant(PluginInitializer.Log).InTransientScope();

        // Dalamud services
        Bind<DalamudServices>().ToSelf();

        // Plugin classes
        Bind<Plugin>().ToSelf().InSingletonScope();
        Bind<IDalamudHook>().To<PluginUIContainer>().InSingletonScope();
        Bind<IDalamudHook>().To<CommandDispatcher>().InSingletonScope();
        Bind<IDalamudHook>().To<NameplateVoiceOverlay>().InSingletonScope();
        Bind<Configuration>().ToMethod(GetConfiguration).InSingletonScope();
        Bind<InputEventSource>().ToSelf().InSingletonScope();
        Bind<InputManager>().ToSelf().InSingletonScope();
        Bind<IAudioDeviceController, PushToTalkController>().To<PushToTalkController>().InSingletonScope();
        Bind<IAudioDeviceController>().To<AudioDeviceController>().WhenInjectedInto<PushToTalkController>().InSingletonScope();
        Bind<VoiceRoomManager>().ToSelf().InSingletonScope();
        Bind<Spatializer>().ToSelf().InSingletonScope();
        Bind<MapManager>().ToSelf().InSingletonScope();
        Bind<PremiumLinker>().ToSelf().InSingletonScope();
        Bind<PrivateRoomCatalog>().ToSelf().InSingletonScope();

        // Views and Presenters
        Bind<WindowSystem>().ToMethod(_ => new(PluginInitializer.Name)).InSingletonScope();
        Bind<IPluginUIView, MainWindow>().To<MainWindow>().InSingletonScope();
        Bind<IPluginUIPresenter, MainWindowPresenter>().To<MainWindowPresenter>().InSingletonScope();
        Bind<IPluginUIView, ConfigWindow>().To<ConfigWindow>().InSingletonScope();
        Bind<IPluginUIPresenter, ConfigWindowPresenter>().To<ConfigWindowPresenter>().InSingletonScope();

        Bind<ILogger>().To<DalamudLogger>();
    }

    private Configuration GetConfiguration(IContext context)
    {
        var configuration =
            PluginInitializer.PluginInterface.GetPluginConfig() as Configuration
            ?? new Configuration();
        configuration.Initialize(PluginInitializer.PluginInterface);
        return configuration;
    }
}
