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
using System.Globalization;
using System.Numerics;

namespace EtheirysProximityVoiceChat.UI.Util;

/// <summary>
/// Centralised palette + small color utilities. All plugin UI should reference
/// these constants instead of inlining <c>new Vector4(...)</c> literals so the
/// theme stays consistent and a single edit re-colours every consumer.
/// </summary>
public static class Vector4Colors
{
    // ── Generic palette ──
    public static Vector4 Red => new(1, 0, 0, 1);
    public static Vector4 Green => new(0, 1, 0, 1);
    public static Vector4 Orange => new(1, 0.65f, 0, 1);
    public static Vector4 Gray => new(0.2f, 0.2f, 0.2f, 1);
    public static Vector4 White => new(1f, 1f, 1f, 1f);

    // ── MainWindow chrome ──
    public static Vector4 Header => new(0.40f, 0.75f, 1.00f, 1.00f);
    public static Vector4 JoinedBackground => new(0.20f, 0.55f, 0.20f, 0.25f);
    public static Vector4 JoinButton => new(0.25f, 0.55f, 0.90f, 1.00f);
    public static Vector4 JoinButtonHover => new(0.35f, 0.65f, 1.00f, 1.00f);
    public static Vector4 LeaveButton => new(0.70f, 0.20f, 0.20f, 1.00f);
    public static Vector4 LeaveButtonHover => new(0.85f, 0.30f, 0.30f, 1.00f);

    // ── Danger (report / kick / etc.) buttons ──
    public static Vector4 DangerButton => new(0.72f, 0.18f, 0.18f, 1f);
    public static Vector4 DangerButtonHover => new(0.85f, 0.24f, 0.24f, 1f);
    public static Vector4 DangerButtonActive => new(0.60f, 0.12f, 0.12f, 1f);

    // ── "Reset to defaults" buttons (slightly softer red than Danger) ──
    public static Vector4 ResetButton => new(0.65f, 0.20f, 0.20f, 1.0f);
    public static Vector4 ResetButtonHover => new(0.78f, 0.28f, 0.28f, 1.0f);
    public static Vector4 ResetButtonActive => new(0.55f, 0.15f, 0.15f, 1.0f);

    // ── External-link buttons (Discord / Ko-fi) ──
    public static Vector4 DiscordButton => new(0.35f, 0.40f, 0.95f, 1f);
    public static Vector4 DiscordButtonHover => new(0.41f, 0.45f, 1.0f, 1f);
    public static Vector4 DiscordButtonActive => new(0.32f, 0.36f, 0.88f, 1f);
    public static Vector4 KofiButton => new(1.0f, 0.39f, 0.20f, 1f);
    public static Vector4 KofiButtonHover => new(1.0f, 0.49f, 0.30f, 1f);
    public static Vector4 KofiButtonActive => new(0.92f, 0.36f, 0.18f, 1f);

    /// <summary>
    /// Parses an RGB hex string ("FFFFFF", "#FFFFFF", "ffaa00") into a Vector4
    /// with alpha = 1. Returns white on any parse failure.
    /// </summary>
    public static Vector4 HexToVector4(string hex)
    {
        try
        {
            var normalized = Utils.NormalizeHexColor(hex);
            var r = int.Parse(normalized.Substring(0, 2), NumberStyles.HexNumber) / 255f;
            var g = int.Parse(normalized.Substring(2, 2), NumberStyles.HexNumber) / 255f;
            var b = int.Parse(normalized.Substring(4, 2), NumberStyles.HexNumber) / 255f;
            return new Vector4(r, g, b, 1.0f);
        }
        catch
        {
            return White;
        }
    }
}
