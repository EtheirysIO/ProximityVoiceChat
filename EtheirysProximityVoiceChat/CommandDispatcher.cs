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
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using EtheirysProximityVoiceChat.UI.Presenter;

namespace EtheirysProximityVoiceChat;

public sealed class CommandDispatcher(
    ICommandManager commandManager,
    MainWindowPresenter mainWindowPresenter) : IDalamudHook
{
    private const string commandName = "/etheirysvoicechat";
    private const string commandNameAlt = "/evc";

    private readonly ICommandManager commandManager = commandManager;
    private readonly MainWindowPresenter mainWindowPresenter = mainWindowPresenter;

    public void HookToDalamud()
    {
        this.commandManager.AddHandler(commandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Etheirys Proximity Voice Chat window"
        });
        this.commandManager.AddHandler(commandNameAlt, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Etheirys Proximity Voice Chat window"
        });
    }

    public void Dispose()
    {
        this.commandManager.RemoveHandler(commandName);
        this.commandManager.RemoveHandler(commandNameAlt);
    }

    private void OnCommand(string command, string args)
    {
        // in response to the slash command, just display our main ui
        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        this.mainWindowPresenter.View.Visible = true;
    }
}
