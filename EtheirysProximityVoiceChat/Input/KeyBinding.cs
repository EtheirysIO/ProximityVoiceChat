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
using System;
using System.Collections.Generic;
using EtheirysProximityVoiceChat.UI.Util;
using WindowsInput.Events;

namespace EtheirysProximityVoiceChat.Input;

/// <summary>
/// A single user-configurable hotkey: one main (non-modifier) key plus any
/// combination of Shift / Ctrl / Alt modifiers. Replaced the legacy
/// "single <see cref="KeyCode"/> field" in <see cref="Configuration"/> at v1.1.3;
/// the legacy fields were dropped entirely in v1.1.4 (config Version bumped to 1).
/// </summary>
[Serializable]
public sealed class KeyBinding : IEquatable<KeyBinding>
{
    public KeyCode Key { get; set; } = KeyCode.None;
    public bool Shift { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }

    public KeyBinding() { }

    public KeyBinding(KeyCode key, bool shift = false, bool ctrl = false, bool alt = false)
    {
        this.Key = key;
        this.Shift = shift;
        this.Ctrl = ctrl;
        this.Alt = alt;
    }

    public bool IsEmpty() => Key == KeyCode.None && !Shift && !Ctrl && !Alt;

    /// <summary>
    /// User-facing label: "Ctrl+Shift+G", "Alt+F4", or "Not set" when empty.
    /// Order is fixed (Ctrl, Shift, Alt, key) so labels are stable.
    /// </summary>
    public string ToDisplayString()
    {
        if (IsEmpty()) return "Not set";
        var parts = new List<string>(4);
        if (Ctrl) parts.Add("Ctrl");
        if (Shift) parts.Add("Shift");
        if (Alt) parts.Add("Alt");
        if (Key != KeyCode.None) parts.Add(KeyCodeStrings.TranslateKeyCode(Key));
        return string.Join("+", parts);
    }

    public bool Equals(KeyBinding? other) =>
        other != null
        && this.Key == other.Key
        && this.Shift == other.Shift
        && this.Ctrl == other.Ctrl
        && this.Alt == other.Alt;

    public override bool Equals(object? obj) => Equals(obj as KeyBinding);
    public override int GetHashCode() => HashCode.Combine((int)Key, Shift, Ctrl, Alt);
    public static bool operator ==(KeyBinding? a, KeyBinding? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(KeyBinding? a, KeyBinding? b) => !(a == b);
}
