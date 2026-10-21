using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Automation;
using Microsoft.Win32;

namespace OBSClipper;

// Starting and cleanly closing OBS. A clean close matters: killing OBS makes
// it show "Crash Detected" on the next start.
static class ObsProcess
{
    const uint WM_CLOSE = 0x0010;

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmdShow);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

    static string ClassName(IntPtr hWnd)
    {
        var name = new StringBuilder(256);
        GetClassName(hWnd, name, name.Capacity);
        return name.ToString();
    }

    public static bool IsRunning()
    {
        var processes = Process.GetProcessesByName("obs64");
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    // The configured path if OBS is there, else wherever OBS's installer put it.
    public static string? Find(string configured)
    {
        var candidates = new List<string> { configured, @"C:\Program Files\obs-studio\bin\64bit\obs64.exe" };
        foreach (var keyPath in new[] { @"SOFTWARE\OBS Studio", @"SOFTWARE\WOW6432Node\OBS Studio" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key?.GetValue(null) is string dir) candidates.Add(Path.Combine(dir, "bin", "64bit", "obs64.exe"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    public static void Start(string exe)
    {
        if (!File.Exists(exe)) throw new FileNotFoundException("OBS was not found at " + exe);
        Process.Start(new ProcessStartInfo(exe, "--startreplaybuffer --minimize-to-tray")
        {
            // OBS has to be started from its own folder.
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = true,
        })?.Dispose();
        Log.Write("Started OBS");
    }

    // Brings OBS's main window up from the tray. Showing the hidden window
    // directly leaves it blank (OBS still thinks it's hidden), so this sends
    // OBS the same message a click on its tray icon does.
    public static bool Show()
    {
        const int SW_RESTORE = 9;
        const uint TrayIconMessage = 0x8000 + 101, NIN_SELECT = 0x400; // Qt's WM_APP + 101
        var processes = Process.GetProcessesByName("obs64");
        try
        {
            foreach (var p in processes)
            {
                var windows = TopLevelWindows(p.Id);
                var main = windows.FirstOrDefault(w => w.Item2.StartsWith("OBS ") && w.Item2.Contains(" - ")).Item1;
                if (main == IntPtr.Zero) continue;
                if (IsWindowVisible(main))
                {
                    // Already open: a tray click would hide it, so just bring it forward.
                    if (IsIconic(main)) ShowWindow(main, SW_RESTORE);
                    SetForegroundWindow(main);
                    return true;
                }
                var tray = windows.FirstOrDefault(w => ClassName(w.Item1).EndsWith("TrayIconMessageWindowClass")).Item1;
                if (tray == IntPtr.Zero) continue;
                SendMessage(tray, TrayIconMessage, IntPtr.Zero, (IntPtr)NIN_SELECT);
                SetForegroundWindow(main);
                return true;
            }
            return false;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    // Sends an open OBS window back to the tray (a tray-icon click toggles it).
    public static void HideToTray()
    {
        const uint TrayIconMessage = 0x8000 + 101, NIN_SELECT = 0x400;
        var processes = Process.GetProcessesByName("obs64");
        try
        {
            foreach (var p in processes)
            {
                var windows = TopLevelWindows(p.Id);
                var main = windows.FirstOrDefault(w => w.Item2.StartsWith("OBS ") && w.Item2.Contains(" - ")).Item1;
                if (main == IntPtr.Zero || !IsWindowVisible(main)) continue;
                var tray = windows.FirstOrDefault(w => ClassName(w.Item1).EndsWith("TrayIconMessageWindowClass")).Item1;
                if (tray != IntPtr.Zero) SendMessage(tray, TrayIconMessage, IntPtr.Zero, (IntPtr)NIN_SELECT);
            }
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    public static async Task<bool> CloseAsync(ObsClient obs)
    {
        // With no active outputs OBS closes without asking.
        if (obs.Connected)
        {
            await obs.RequestAsync("StopReplayBuffer");
            await Task.Delay(1000);
        }

        var processes = Process.GetProcessesByName("obs64");
        try
        {
            foreach (var p in processes)
                foreach (var (hWnd, title) in TopLevelWindows(p.Id))
                    if (title.StartsWith("OBS ") && title.Contains(" - "))
                        PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            var deadline = DateTime.Now.AddSeconds(20);
            while (DateTime.Now < deadline)
            {
                if (processes.All(p => p.HasExited))
                {
                    Log.Write("Closed OBS");
                    return true;
                }
                await Task.Run(() =>
                {
                    foreach (var p in processes)
                        if (!p.HasExited) ConfirmExit(p.Id);
                });
                await Task.Delay(500);
            }
            Log.Write("OBS did not close");
            return false;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    // Answers OBS's "outputs are still active, exit anyway?" dialog.
    static void ConfirmExit(int processId)
    {
        try
        {
            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId));
            foreach (AutomationElement window in windows)
            {
                if (window.Current.Name.Contains("Settings")) continue;
                var buttons = window.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                foreach (AutomationElement button in buttons)
                {
                    var name = button.Current.Name.Replace("&", "");
                    if ((name == "Yes" || name == "OK") && button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                    {
                        ((InvokePattern)pattern).Invoke();
                        return;
                    }
                }
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
    }

    static List<(IntPtr, string)> TopLevelWindows(int processId)
    {
        var windows = new List<(IntPtr, string)>();
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == processId)
            {
                var title = new StringBuilder(256);
                GetWindowText(hWnd, title, title.Capacity);
                windows.Add((hWnd, title.ToString()));
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }
}

// OBS's built-in WebSocket server settings. OBS only reads them at startup.
static class ObsWebSocketConfig
{
    public sealed record Settings(bool Enabled, int Port, string Password);

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "obs-studio", "plugin_config", "obs-websocket", "config.json");

    public static Settings Read()
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(FilePath));
            return new Settings(
                (bool?)node?["server_enabled"] ?? false,
                (int?)node?["server_port"] ?? 4455,
                (bool?)node?["auth_required"] == true ? (string?)node?["server_password"] ?? "" : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return new Settings(false, 4455, "");
        }
    }

    // Call only while OBS is closed, or OBS may write its old settings back.
    public static void SetEnabled(bool enabled)
    {
        JsonObject node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            node = new JsonObject();
        }
        node["server_enabled"] = enabled;
        node["first_load"] = false;
        node["alerts_enabled"] ??= false;
        node["server_port"] ??= 4455;
        if (enabled)
        {
            node["auth_required"] = true;
            if (string.IsNullOrEmpty((string?)node["server_password"]))
                node["server_password"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Log.Write(enabled ? "Turned on OBS WebSocket server" : "Turned off OBS WebSocket server");
    }
}

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "OBS Clipper";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue(ValueName, false);
    }
}
