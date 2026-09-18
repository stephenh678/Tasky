using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace TodoApp.Services;

/// <summary>
/// A global-hotkey combination ("Ctrl+Alt+T") as stored in Settings.QuickAddHotkey: parsing it
/// back into what RegisterHotKey needs, and formatting what the Settings window's capture box
/// sees. The quick-add hotkey used to be hard-coded, so on a machine where another app already
/// owned Ctrl+Alt+T there was no way to get global quick-add back at all.
/// </summary>
public readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    public const string Default = "Ctrl+Alt+T";

    // RegisterHotKey's fsModifiers bits (winuser.h MOD_*). ModifierKeys happens to use the same
    // values for these four, but spelling them out keeps this from depending on that coincidence.
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;

    public uint Win32Modifiers =>
        (Modifiers.HasFlag(ModifierKeys.Alt) ? ModAlt : 0) |
        (Modifiers.HasFlag(ModifierKeys.Control) ? ModControl : 0) |
        (Modifiers.HasFlag(ModifierKeys.Shift) ? ModShift : 0) |
        (Modifiers.HasFlag(ModifierKeys.Windows) ? ModWin : 0);

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    // A global hotkey fires in every app, so it has to be something nobody types by accident:
    // at least one of Ctrl/Alt/Win (Shift alone is just a capital letter) plus one real key.
    public bool IsValid =>
        (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0
        && !IsModifierKey(Key) && Key != Key.None;

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        _ => key.ToString()
    };

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = ModifierKeys.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; continue;
                case "alt": modifiers |= ModifierKeys.Alt; continue;
                case "shift": modifiers |= ModifierKeys.Shift; continue;
                case "win" or "windows": modifiers |= ModifierKeys.Windows; continue;
            }

            if (key is not null) return false; // two non-modifier keys
            var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
            if (!Enum.TryParse<Key>(name, ignoreCase: true, out var parsed) || int.TryParse(raw, out _) && raw.Length != 1)
                return false;
            key = parsed;
        }

        if (key is null) return false;
        gesture = new HotkeyGesture(modifiers, key.Value);
        return gesture.IsValid;
    }

    public static HotkeyGesture ParseOrDefault(string? text)
        => TryParse(text, out var gesture) ? gesture : new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Alt, Key.T);
}
