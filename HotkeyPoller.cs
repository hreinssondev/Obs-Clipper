using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace OBSClipper;

// Watches hotkeys by polling key state, the same way OBS does. Nothing hooks
// the keyboard, so key presses reach the game untouched and can't be delayed.
sealed class HotkeyPoller : IDisposable
{
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

    public event Action<HotkeyBinding>? Pressed;
    public volatile bool Paused;

    readonly object gate = new();
    readonly System.Threading.Timer timer;
    List<HotkeyBinding> bindings = new();
    HashSet<HotkeyBinding> held = new();

    public HotkeyPoller() => timer = new System.Threading.Timer(Tick, null, 25, 25);

    public void SetBindings(IEnumerable<HotkeyBinding> source)
    {
        lock (gate)
        {
            bindings = source.Where(b => b.Key != 0).Select(b => b.Copy()).ToList();
            // A key that is down right now (e.g. the one just assigned) must be released first.
            held = bindings.Where(b => IsDown(b.Key)).ToHashSet();
        }
    }

    void Tick(object? _)
    {
        if (!Monitor.TryEnter(gate)) return;
        try
        {
            bool ctrl = IsDown(VK_CONTROL), alt = IsDown(VK_MENU), shift = IsDown(VK_SHIFT);
            var active = bindings.Where(b => IsDown(b.Key) && (!b.Ctrl || ctrl) && (!b.Alt || alt) && (!b.Shift || shift)).ToList();
            // Ctrl+X wins over plain X on the same key, so only one clip is saved.
            active.RemoveAll(b => active.Any(o => o.Key == b.Key && Modifiers(o) > Modifiers(b)));
            foreach (var b in bindings)
            {
                if (!active.Contains(b)) held.Remove(b);
                else if (held.Add(b) && !Paused) Pressed?.Invoke(b);
            }
        }
        finally
        {
            Monitor.Exit(gate);
        }
    }

    static int Modifiers(HotkeyBinding b) => (b.Ctrl ? 1 : 0) + (b.Alt ? 1 : 0) + (b.Shift ? 1 : 0);

    static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose() => timer.Dispose();
}

static class KeyNames
{
    const uint MAPVK_VK_TO_VSC = 0, MAPVK_VK_TO_CHAR = 2;

    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetKeyNameText(int lParam, StringBuilder text, int size);

    static readonly HashSet<int> Extended = new() { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x6F, 0x90, 0xA3, 0xA5 };

    public static string Describe(HotkeyBinding b)
    {
        if (b.Key == 0) return "Click to set key";
        var parts = new List<string>();
        if (b.Ctrl) parts.Add("Ctrl");
        if (b.Alt) parts.Add("Alt");
        if (b.Shift) parts.Add("Shift");
        parts.Add(Name(b.Key));
        return string.Join(" + ", parts);
    }

    // Names follow the active keyboard layout, so Norwegian keys show as + \ ' etc.
    public static string Name(int vk)
    {
        switch (vk)
        {
            case 0x04: return "Middle mouse";
            case 0x05: return "Mouse 4";
            case 0x06: return "Mouse 5";
            case 0x6A: return "Num *";
            case 0x6B: return "Num +";
            case 0x6D: return "Num -";
            case 0x6E: return "Num .";
            case 0x6F: return "Num /";
        }
        if (vk >= 0x60 && vk <= 0x69) return "Num " + (vk - 0x60);
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);

        uint ch = MapVirtualKey((uint)vk, MAPVK_VK_TO_CHAR) & 0xFFFF;
        if (ch > 32) return char.ToUpperInvariant((char)ch).ToString();

        uint scan = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
        if (scan != 0)
        {
            int lParam = (int)(scan << 16) | (Extended.Contains(vk) ? 1 << 24 : 0);
            var name = new StringBuilder(64);
            if (GetKeyNameText(lParam, name, name.Capacity) > 0) return name.ToString();
        }
        return ((Keys)vk).ToString();
    }
}
