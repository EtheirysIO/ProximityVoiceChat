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
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace EtheirysProximityVoiceChat.UI.Util;

public static class Common
{
    /// <summary>
    /// "(?)" hover-tooltip helper used wherever a control needs an inline
    /// explanation. Tooltip wraps at 35 em for readability.
    /// </summary>
    public static void HelpMarker(string description)
    {
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35.0f);
            ImGui.TextUnformatted(description);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    /// <summary>
    /// Bright-red destructive-action button (report, kick, mute, etc.). Caller
    /// owns the result and the click semantics; this just enforces a consistent
    /// danger styling across both windows.
    /// </summary>
    public static bool DrawDangerButton(string label, Vector2 size)
    {
        using var button = ImRaii.PushColor(ImGuiCol.Button, Vector4Colors.DangerButton);
        using var hover = ImRaii.PushColor(ImGuiCol.ButtonHovered, Vector4Colors.DangerButtonHover);
        using var active = ImRaii.PushColor(ImGuiCol.ButtonActive, Vector4Colors.DangerButtonActive);
        return ImGui.Button(label, size);
    }

    /// <summary>
    /// "Reset to Defaults" button — slightly softer red than <see cref="DrawDangerButton"/>
    /// so the two are visually distinguishable when stacked on the same screen.
    /// </summary>
    public static bool DrawResetButton(string label)
    {
        using var button = ImRaii.PushColor(ImGuiCol.Button, Vector4Colors.ResetButton);
        using var hover = ImRaii.PushColor(ImGuiCol.ButtonHovered, Vector4Colors.ResetButtonHover);
        using var active = ImRaii.PushColor(ImGuiCol.ButtonActive, Vector4Colors.ResetButtonActive);
        return ImGui.Button(label);
    }
}
