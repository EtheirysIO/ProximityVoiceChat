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
using System.Runtime.InteropServices;
using WindowsInput;
using WindowsInput.Events;
using WindowsInput.Events.Sources;

namespace EtheirysProximityVoiceChat.Input;

public sealed class InputEventSource(Configuration configuration) : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr handle, out int processId);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // VK_* virtual-key codes used for modifier-state queries. Constants used
    // directly instead of casting KeyCode values because WindowsInput's enum
    // naming for these specific keys isn't worth depending on — the underlying
    // Win32 codes are stable.
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // a.k.a. Alt

    private static bool IsGameFocused()
    {
        try
        {
            var foregroundWindowHandle = GetForegroundWindow();
            if (foregroundWindowHandle == IntPtr.Zero) return false;

            _ = GetWindowThreadProcessId(foregroundWindowHandle, out var activeProcessId);

            return activeProcessId == Environment.ProcessId;
        }
        catch (EntryPointNotFoundException)
        {
            return true;
        }
    }

    /// <summary>
    /// True iff either Shift key is currently held. Queried directly from the
    /// OS (GetAsyncKeyState) so it works from any thread, including the
    /// WindowsInput global keyboard hook thread.
    /// </summary>
    public static bool IsShiftDown() => (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
    public static bool IsCtrlDown() => (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
    public static bool IsAltDown() => (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

    /// <summary>
    /// True if the given KeyCode is a pure modifier (Shift / Ctrl / Alt / Win,
    /// either generic or left/right variants). Pure-modifier presses are
    /// ignored while the user is recording a keybind; only the subsequent
    /// non-modifier press finalizes the binding, with the modifier state
    /// captured separately via <see cref="IsShiftDown"/> etc.
    /// </summary>
    public static bool IsModifierKey(KeyCode k)
    {
        var v = (int)k;
        return v == VK_SHIFT || v == VK_CONTROL || v == VK_MENU // generic shift/ctrl/alt
            || v == 0xA0 || v == 0xA1 // VK_LSHIFT / VK_RSHIFT
            || v == 0xA2 || v == 0xA3 // VK_LCONTROL / VK_RCONTROL
            || v == 0xA4 || v == 0xA5 // VK_LMENU / VK_RMENU
            || v == 0x5B || v == 0x5C; // VK_LWIN / VK_RWIN
    }

    private readonly Configuration configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    // Subscriber lists are mutated from the framework thread (via Subscribe/
    // Unsubscribe calls from ConfigWindowPresenter, InputManager,
    // PushToTalkController) and iterated from the WindowsInput global low-level
    // keyboard hook callback thread. Without synchronization, a foreach
    // overlapping with Add/Remove throws InvalidOperationException inside the
    // hook callback, which propagates into native Win32 hook chain code and
    // crashes the game. Snapshot-before-iterate under a lock keeps the read
    // path lock-free for the duration of the foreach, and re-entrant
    // subscribe/unsubscribe from within a handler is also safe.
    private readonly object subscriberLock = new();
    private readonly List<Action<KeyDown>> subscribedKeyDownActions = [];
    private readonly List<Action<KeyUp>> subscribedKeyUpActions = [];

    private IKeyboardEventSource? keyboard;
    private IMouseEventSource? mouse;

    public void Dispose()
    {
        if (keyboard != null)
        {
            keyboard.KeyDown -= OnKeyboardKeyDown;
            keyboard.KeyUp -= OnKeyboardKeyUp;
        }
        keyboard?.Dispose();
        if (mouse != null)
        {
            mouse.ButtonDown -= OnMouseButtonDown;
            mouse.ButtonUp -= OnMouseButtonUp;
        }
        mouse?.Dispose();
    }

    public void SubscribeToKeyDown(Action<KeyDown> action)
    {
        // One-time setup of persistent listeners
        SetupGlobalSubscriptions();

        lock (this.subscriberLock) { subscribedKeyDownActions.Add(action); }
    }

    public void UnsubscribeToKeyDown(Action<KeyDown> action)
    {
        lock (this.subscriberLock) { subscribedKeyDownActions.Remove(action); }
    }

    public void SubscribeToKeyUp(Action<KeyUp> action)
    {
        // One-time setup of persistent listeners
        SetupGlobalSubscriptions();

        lock (this.subscriberLock) { subscribedKeyUpActions.Add(action); }
    }

    public void UnsubscribeToKeyUp(Action<KeyUp> action)
    {
        lock (this.subscriberLock) { subscribedKeyUpActions.Remove(action); }
    }

    private void SetupGlobalSubscriptions()
    {
        if (keyboard == null)
        {
            keyboard = Capture.Global.KeyboardAsync();
            keyboard.KeyDown += OnKeyboardKeyDown;
            keyboard.KeyUp += OnKeyboardKeyUp;
        }
        if (mouse == null)
        {
            mouse = Capture.Global.MouseAsync();
            mouse.ButtonDown += OnMouseButtonDown;
            mouse.ButtonUp += OnMouseButtonUp;
        }
    }

    private void OnKeyboardKeyDown(object? o, EventSourceEventArgs<KeyDown> e)
    {
        if (this.configuration.KeybindsRequireGameFocus && !IsGameFocused()) { return; }

        DispatchKeyDown(e.Data);
    }

    private void OnKeyboardKeyUp(object? o, EventSourceEventArgs<KeyUp> e)
    {
        // Always listen to key ups, since these are necessary to cancel hold actions
        DispatchKeyUp(e.Data);
    }

    private void OnMouseButtonDown(object? o, EventSourceEventArgs<ButtonDown> e)
    {
        if (this.configuration.KeybindsRequireGameFocus && !IsGameFocused()) { return; }

        // Only accept middle mouse, mouse4, and mouse5
        KeyCode keyCode;
        if (e.Data.Button == ButtonCode.XButton1)
        {
            keyCode = KeyCode.XButton1;
        }
        else if (e.Data.Button == ButtonCode.XButton2)
        {
            keyCode = KeyCode.XButton2;
        }
        else if (e.Data.Button == ButtonCode.Middle)
        {
            keyCode = KeyCode.MButton;
        }
        else
        {
            return;
        }

        DispatchKeyDown(new(keyCode));
    }

    private void OnMouseButtonUp(object? o, EventSourceEventArgs<ButtonUp> e)
    {
        // Always listen to key ups, since these are necessary to cancel hold actions
        // Only accept middle mouse, mouse4, and mouse5
        KeyCode keyCode;
        if (e.Data.Button == ButtonCode.XButton1)
        {
            keyCode = KeyCode.XButton1;
        }
        else if (e.Data.Button == ButtonCode.XButton2)
        {
            keyCode = KeyCode.XButton2;
        }
        else if (e.Data.Button == ButtonCode.Middle)
        {
            keyCode = KeyCode.MButton;
        }
        else
        {
            return;
        }

        DispatchKeyUp(new(keyCode));
    }

    private void DispatchKeyDown(KeyDown data)
    {
        // Snapshot under the lock so the foreach can't fault if the list is
        // mutated while we iterate. The handlers themselves run *outside* the
        // lock so they can re-enter Subscribe/Unsubscribe without deadlock.
        Action<KeyDown>[] snapshot;
        lock (this.subscriberLock) { snapshot = subscribedKeyDownActions.ToArray(); }
        foreach (var action in snapshot)
        {
            // Critical: never let a handler exception propagate. We're called
            // from a WindowsInput global LL hook callback, and an unhandled
            // managed exception escaping into native hook chain code crashes
            // the host process (the game).
            try { action.Invoke(data); }
            catch { /* swallow — a buggy handler must not kill the hook */ }
        }
    }

    private void DispatchKeyUp(KeyUp data)
    {
        Action<KeyUp>[] snapshot;
        lock (this.subscriberLock) { snapshot = subscribedKeyUpActions.ToArray(); }
        foreach (var action in snapshot)
        {
            try { action.Invoke(data); }
            catch { /* swallow — see DispatchKeyDown */ }
        }
    }
}
