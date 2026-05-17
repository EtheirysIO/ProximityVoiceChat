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
using EtheirysProximityVoiceChat.UI.Presenter;
using EtheirysProximityVoiceChat.UI.View;

namespace EtheirysProximityVoiceChat.UI;

// It is good to have this be disposable in general, in case you ever need it
// to do any cleanup
public sealed class PluginUIContainer : IDalamudHook
{
    private readonly IPluginUIPresenter[] pluginUIPresenters;
    private readonly MainWindowPresenter mainWindowPresenter;
    private readonly ConfigWindow configWindow;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly WindowSystem windowSystem;

    public PluginUIContainer(
        IPluginUIPresenter[] pluginUIPresenters,
        MainWindowPresenter mainWindowPresenter,
        ConfigWindow configWindow,
        IDalamudPluginInterface pluginInterface,
        WindowSystem windowSystem)
    {
        this.pluginUIPresenters = pluginUIPresenters;
        this.mainWindowPresenter = mainWindowPresenter;
        this.configWindow = configWindow;
        this.pluginInterface = pluginInterface;
        this.windowSystem = windowSystem;

        foreach (var pluginUIPresenter in this.pluginUIPresenters)
        {
            pluginUIPresenter.SetupBindings();
        }
    }

    public void Dispose()
    {
        this.pluginInterface.UiBuilder.Draw -= Draw;
        this.pluginInterface.UiBuilder.OpenMainUi -= ShowMainWindow;
        this.pluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigWindow;
    }

    public void HookToDalamud()
    {
        this.pluginInterface.UiBuilder.Draw += Draw;
        this.pluginInterface.UiBuilder.OpenMainUi += ShowMainWindow;
        // Dalamud's per-plugin "⚙ Configuration" button. The standalone Settings
        // window is also reachable from the gear icon in MainWindow's footer.
        this.pluginInterface.UiBuilder.OpenConfigUi += ToggleConfigWindow;
    }

    public void Draw()
    {
        // This is our only draw handler attached to UIBuilder, so it needs to be
        // able to draw any windows we might have open.
        // Each method checks its own visibility/state to ensure it only draws when
        // it actually makes sense.
        // There are other ways to do this, but it is generally best to keep the number of
        // draw delegates as low as possible.

        foreach (var pluginUIPresenter in this.pluginUIPresenters)
        {
            pluginUIPresenter.View.Draw();
        }
    }

    private void ShowMainWindow()
    {
        this.mainWindowPresenter.View.Visible = true;
    }

    private void ToggleConfigWindow()
    {
        this.configWindow.Visible = !this.configWindow.Visible;
    }
}
