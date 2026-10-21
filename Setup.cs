using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace OBSClipper;

sealed class SetupException : Exception
{
    public SetupException(string message) : base(message) { }
}

// First-launch setup: installs ffmpeg and OBS with winget, connects to OBS and
// configures it for replay clips. Each step checks first, so running it again
// only fixes what's missing.
sealed class Setup
{
    public enum StepState { Pending, Working, Done, Failed }

    public sealed class Step
    {
        public Step(string title) => Title = title;
        public string Title { get; }
        public StepState State { get; set; }
        public string Detail { get; set; } = "";
    }

    public readonly Step Ffmpeg = new("Install ffmpeg (trims clips)");
    public readonly Step Obs = new("Install OBS Studio");
    public readonly Step Connect = new("Connect to OBS");
    public readonly Step Capture = new("Add screen capture to OBS");
    public readonly Step Buffer = new("Turn on the replay buffer");
    public Step[] Steps => new[] { Ffmpeg, Obs, Connect, Capture, Buffer };

    // Raised on the UI thread whenever a step changes.
    public event Action? Changed;

    readonly AppConfig config;
    readonly ObsClient obs;
    // OBS had never been run, so its settings are ours to choose.
    bool freshObs;

    public Setup(AppConfig config, ObsClient obs)
    {
        this.config = config;
        this.obs = obs;
    }

    public async Task<bool> RunAsync()
    {
        foreach (var step in Steps.Where(s => s.State != StepState.Done))
        {
            step.State = StepState.Pending;
            step.Detail = "";
        }
        Changed?.Invoke();
        return await Run(Ffmpeg, EnsureFfmpegAsync)
            && await Run(Obs, EnsureObsAsync)
            && await Run(Connect, ConnectAsync)
            && await Run(Capture, EnsureCaptureAsync)
            && await Run(Buffer, EnsureReplayBufferAsync);
    }

    async Task<bool> Run(Step step, Func<Step, Task<string>> action)
    {
        if (step.State == StepState.Done) return true;
        step.State = StepState.Working;
        step.Detail = "";
        Changed?.Invoke();
        try
        {
            step.Detail = await action(step);
            step.State = StepState.Done;
        }
        catch (SetupException ex)
        {
            step.Detail = ex.Message;
            step.State = StepState.Failed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            step.Detail = "Something went wrong: " + ex.Message;
            step.State = StepState.Failed;
        }
        Log.Write($"Setup: {step.Title}: {step.State} {step.Detail}");
        Changed?.Invoke();
        return step.State == StepState.Done;
    }

    void Progress(Step step, string detail)
    {
        step.Detail = detail;
        Changed?.Invoke();
    }

    async Task<string> EnsureFfmpegAsync(Step step)
    {
        if (File.Exists(config.FfmpegPath)) return "Already installed";
        string found = AppConfig.FindFfmpeg();
        if (found.Length == 0)
        {
            Progress(step, "Downloading and installing (about 100 MB)…");
            await WingetInstallAsync("Gyan.FFmpeg");
            found = AppConfig.FindFfmpeg();
        }
        if (found.Length == 0) throw new SetupException("ffmpeg didn't install. Check your internet connection and try again.");
        config.FfmpegPath = found;
        config.Save();
        return "Installed";
    }

    async Task<string> EnsureObsAsync(Step step)
    {
        var exe = ObsProcess.Find(config.ObsPath);
        string result = "Already installed";
        if (exe == null)
        {
            Progress(step, "Downloading and installing… Click Yes if Windows asks for permission.");
            await WingetInstallAsync("OBSProject.OBSStudio");
            exe = ObsProcess.Find(config.ObsPath);
            if (exe == null) throw new SetupException("OBS didn't install. If Windows asked for permission, click Yes, then try again.");
            result = "Installed";
        }
        config.ObsPath = exe;
        config.Save();
        return result;
    }

    async Task<string> ConnectAsync(Step step)
    {
        if (obs.Connected) return "Connected";
        bool running = ObsProcess.IsRunning();
        if (!ObsWebSocketConfig.Read().Enabled)
        {
            if (running)
            {
                Progress(step, "Closing OBS to turn on its WebSocket server…");
                if (!await ObsProcess.CloseAsync(obs)) throw new SetupException("OBS didn't close. Close OBS yourself, then try again.");
                running = false;
            }
            ObsWebSocketConfig.SetEnabled(true);
        }
        if (!running)
        {
            freshObs = !ObsSettings.HasProfile();
            if (freshObs) ObsSettings.SkipFirstRunWizard();
            Progress(step, "Starting OBS…");
            ObsProcess.Start(config.ObsPath);
        }
        Progress(step, "Waiting for OBS…");
        if (!await WaitForConnectionAsync(90))
            throw new SetupException("Couldn't connect to OBS. In OBS, open Tools → WebSocket Server Settings and make sure the server is on, then try again.");
        return "Connected";
    }

    async Task<string> EnsureCaptureAsync(Step step)
    {
        var scene = await CallAsync("GetCurrentProgramScene");
        string sceneName = scene.GetProperty("currentProgramSceneName").GetString()!;
        var items = await CallAsync("GetSceneItemList", new() { ["sceneName"] = sceneName });
        if (items.GetProperty("sceneItems").GetArrayLength() > 0) return "Your OBS scene already has sources";

        Progress(step, "Adding Display Capture…");
        string inputName = "Display Capture";
        var created = await obs.RequestAsync("CreateInput", new()
        {
            ["sceneName"] = sceneName, ["inputName"] = inputName, ["inputKind"] = "monitor_capture",
            ["inputSettings"] = new Dictionary<string, object>(), ["sceneItemEnabled"] = true,
        });
        if (!ObsClient.Succeeded(created))
        {
            inputName = "Display Capture (OBS Clipper)";
            created = await CallAsync("CreateInput", new()
            {
                ["sceneName"] = sceneName, ["inputName"] = inputName, ["inputKind"] = "monitor_capture",
                ["inputSettings"] = new Dictionary<string, object>(), ["sceneItemEnabled"] = true,
            });
        }
        int itemId = Data(created).GetProperty("sceneItemId").GetInt32();

        // Capture the primary monitor.
        var monitors = await CallAsync("GetInputPropertiesListPropertyItems", new() { ["inputName"] = inputName, ["propertyName"] = "monitor_id" });
        var choices = monitors.GetProperty("propertyItems").EnumerateArray()
            .Where(m => m.GetProperty("itemEnabled").ValueKind != JsonValueKind.False).ToList();
        if (choices.Count > 0)
        {
            var primary = choices.FirstOrDefault(m => (m.GetProperty("itemName").GetString() ?? "").Contains("Primary"));
            var monitor = primary.ValueKind == JsonValueKind.Object ? primary : choices[0];
            await CallAsync("SetInputSettings", new()
            {
                ["inputName"] = inputName,
                ["inputSettings"] = new Dictionary<string, object> { ["monitor_id"] = monitor.GetProperty("itemValue").GetString()! },
            });
        }

        // Fit it to the canvas.
        var video = await CallAsync("GetVideoSettings");
        await CallAsync("SetSceneItemTransform", new()
        {
            ["sceneName"] = sceneName, ["sceneItemId"] = itemId,
            ["sceneItemTransform"] = new Dictionary<string, object>
            {
                ["positionX"] = 0, ["positionY"] = 0, ["alignment"] = 5,
                ["boundsType"] = "OBS_BOUNDS_SCALE_INNER", ["boundsAlignment"] = 0,
                ["boundsWidth"] = video.GetProperty("baseWidth").GetDouble(),
                ["boundsHeight"] = video.GetProperty("baseHeight").GetDouble(),
            },
        });
        return "Added Display Capture (your main screen)";
    }

    async Task<string> EnsureReplayBufferAsync(Step step)
    {
        if (await ReplayBufferRunningAsync(3)) return "On";

        Progress(step, "Turning it on in OBS's settings (OBS restarts)…");
        if (!await RestartObsAsync(() => ObsSettings.EnableReplayBuffer(freshObs, StagingFolder())))
            throw new SetupException("OBS didn't restart. Close OBS yourself, then try again.");
        if (await ReplayBufferRunningAsync(20)) return "On";

        // The hardware encoder picked for a new OBS may not work on this PC.
        if (ObsSettings.UsesHardwareEncoder())
        {
            Progress(step, "Trying the CPU encoder instead…");
            await RestartObsAsync(ObsSettings.UseSoftwareEncoder);
            if (await ReplayBufferRunningAsync(20)) return "On (CPU encoder)";
        }
        throw new SetupException("OBS wouldn't start the replay buffer. In OBS, open Settings → Output and check that Replay Buffer is ticked, then try again.");
    }

    string StagingFolder() => Path.Combine(config.ClipsFolder, "_staging");

    // Closes OBS, changes its settings while it's closed, and starts it again.
    async Task<bool> RestartObsAsync(Action changeSettings)
    {
        if (ObsProcess.IsRunning() && !await ObsProcess.CloseAsync(obs)) return false;
        changeSettings();
        ObsProcess.Start(config.ObsPath);
        return await WaitForConnectionAsync(60);
    }

    async Task<bool> ReplayBufferRunningAsync(int seconds)
    {
        var deadline = DateTime.Now.AddSeconds(seconds);
        bool asked = false;
        while (DateTime.Now < deadline)
        {
            if (obs.Connected)
            {
                if (ObsClient.OutputActive(await obs.RequestAsync("GetReplayBufferStatus"))) return true;
                if (!asked) asked = ObsClient.Succeeded(await obs.RequestAsync("StartReplayBuffer"));
            }
            await Task.Delay(1000);
        }
        return false;
    }

    async Task<bool> WaitForConnectionAsync(int seconds)
    {
        var deadline = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < deadline)
        {
            if (obs.Connected) return true;
            await Task.Delay(500);
        }
        return obs.Connected;
    }

    async Task<JsonElement> CallAsync(string type, Dictionary<string, object>? data = null)
    {
        var response = await obs.RequestAsync(type, data);
        if (!ObsClient.Succeeded(response))
        {
            string reason = response is JsonElement r && r.TryGetProperty("requestStatus", out var status) && status.TryGetProperty("comment", out var c)
                ? c.GetString() ?? "" : "no answer";
            throw new SetupException($"OBS refused {type}: {reason}");
        }
        return Data(response);
    }

    static JsonElement Data(JsonElement? response) =>
        response is JsonElement r && r.TryGetProperty("responseData", out var data) ? data : default;

    static async Task WingetInstallAsync(string id)
    {
        var psi = new ProcessStartInfo("winget",
            $"install --id {id} -e --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Win32Exception)
        {
            throw new SetupException("Windows Package Manager (winget) is missing. Install \"App Installer\" from the Microsoft Store, then try again.");
        }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string text = (await output + await errors).Trim();
            Log.Write($"winget {id} exited with {process.ExitCode}: {(text.Length > 400 ? text[^400..] : text)}");
        }
    }
}

// Reading and changing OBS's own settings files (only while OBS is closed).
static class ObsSettings
{
    static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");

    static string ProfileIni()
    {
        string dir = "Untitled";
        foreach (var file in new[] { "user.ini", "global.ini" })
        {
            var value = Ini.Get(Path.Combine(Root, file), "Basic", "ProfileDir");
            if (!string.IsNullOrEmpty(value))
            {
                dir = value;
                break;
            }
        }
        return Path.Combine(Root, "basic", "profiles", dir, "basic.ini");
    }

    public static bool HasProfile() => File.Exists(ProfileIni());

    // OBS's first start asks to run its auto-configuration wizard; setup does that job.
    // FirstRun=true tells OBS its first run already happened.
    public static void SkipFirstRunWizard()
    {
        foreach (var file in new[] { "user.ini", "global.ini" })
            Ini.Set(Path.Combine(Root, file), "General", "FirstRun", "true");
    }

    const string ReplaySeconds = "60", ReplayMegabytes = "1024";

    // Called when OBS isn't set up for replays yet: applies the recording setup
    // OBS Clipper was tuned with. Video (resolution, fps) only changes on a brand-new OBS.
    public static void EnableReplayBuffer(bool fresh, string staging)
    {
        var ini = ProfileIni();
        Directory.CreateDirectory(staging);
        if (!ApplyNvidiaDefaults(ini, staging))
        {
            bool advanced = Ini.Get(ini, "Output", "Mode") == "Advanced";
            string section = advanced ? "AdvOut" : "SimpleOutput";
            Ini.Set(ini, section, "RecRB", "true");
            Ini.Set(ini, section, "RecRBTime", ReplaySeconds);
            Ini.Set(ini, section, "RecRBSize", ReplayMegabytes);
            Ini.Set(ini, section, "RecFormat2", "mp4");
            Ini.Set(ini, section, "RecFormat", "mp4");
            Ini.Set(ini, section, advanced ? "RecFilePath" : "FilePath", staging);
            if (!advanced)
            {
                Ini.Set(ini, section, "RecQuality", "Small");
                Ini.Set(ini, section, "RecEncoder", HardwareEncoder());
            }
        }
        if (fresh) ApplyVideoDefaults(ini);
        ClearObsSaveHotkey(ini);
    }

    // Advanced output, NVENC (AV1 where the card has it) at CQ 25 with low-latency
    // tuning, 60 s / 1 GB replay buffer, MP4, AAC 160 kbps. False without an NVIDIA card.
    static bool ApplyNvidiaDefaults(string ini, string staging)
    {
        var gpus = GpuNames();
        if (!gpus.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return false;
        // AV1 encoding needs an RTX 40 or 50 series (or Ada / Blackwell workstation) card.
        bool av1 = Regex.IsMatch(gpus, @"RTX\s*[45]0\d0|\bAda\b|Blackwell", RegexOptions.IgnoreCase);

        Ini.Set(ini, "Output", "Mode", "Advanced");
        Ini.Set(ini, "AdvOut", "RecType", "Standard");
        Ini.Set(ini, "AdvOut", "RecFilePath", staging);
        Ini.Set(ini, "AdvOut", "RecFormat2", "mp4");
        Ini.Set(ini, "AdvOut", "RecEncoder", av1 ? "obs_nvenc_av1_tex" : "obs_nvenc_h264_tex");
        Ini.Set(ini, "AdvOut", "RecTracks", "1");
        Ini.Set(ini, "AdvOut", "RecAudioEncoder", "ffmpeg_aac");
        Ini.Set(ini, "AdvOut", "Track1Bitrate", "160");
        Ini.Set(ini, "AdvOut", "RecRB", "true");
        Ini.Set(ini, "AdvOut", "RecRBTime", ReplaySeconds);
        Ini.Set(ini, "AdvOut", "RecRBSize", ReplayMegabytes);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(ini)!, "recordEncoder.json"),
            "{\"rate_control\":\"CQVBR\",\"preset\":\"p1\",\"multipass\":\"disabled\",\"lookahead\":false,\"adaptive_quantization\":false,\"target_quality\":25,\"tune\":\"ull\"}");
        return true;
    }

    // 60 fps, output scaled down to 1080p at most.
    static void ApplyVideoDefaults(string ini)
    {
        Ini.Set(ini, "Video", "FPSType", "0");
        Ini.Set(ini, "Video", "FPSCommon", "60");
        if (int.TryParse(Ini.Get(ini, "Video", "BaseCX"), out int baseWidth) && int.TryParse(Ini.Get(ini, "Video", "BaseCY"), out int baseHeight) && baseHeight > 0)
        {
            int height = Math.Min(baseHeight, 1080);
            int width = (int)Math.Round(baseWidth * (double)height / baseHeight / 2) * 2;
            Ini.Set(ini, "Video", "OutputCX", width.ToString());
            Ini.Set(ini, "Video", "OutputCY", height.ToString());
        }
    }

    // OBS Clipper's hotkeys save replays; an OBS hotkey too would save twice.
    static void ClearObsSaveHotkey(string ini)
    {
        if (Ini.Get(ini, "Hotkeys", "ReplayBuffer") != null)
            Ini.Set(ini, "Hotkeys", "ReplayBuffer", "{\"ReplayBuffer.Save\":[]}");
    }

    public static bool UsesHardwareEncoder()
    {
        var ini = ProfileIni();
        string? encoder = Ini.Get(ini, "Output", "Mode") == "Advanced"
            ? Ini.Get(ini, "AdvOut", "RecEncoder")
            : Ini.Get(ini, "SimpleOutput", "RecEncoder");
        return encoder != null && !encoder.Contains("x264");
    }

    public static void UseSoftwareEncoder()
    {
        var ini = ProfileIni();
        if (Ini.Get(ini, "Output", "Mode") == "Advanced")
        {
            Ini.Set(ini, "AdvOut", "RecEncoder", "obs_x264");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(ini)!, "recordEncoder.json"),
                "{\"rate_control\":\"CRF\",\"crf\":23,\"preset\":\"veryfast\"}");
        }
        else
        {
            Ini.Set(ini, "SimpleOutput", "RecEncoder", "x264");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);

    static string GpuNames()
    {
        var names = new List<string>();
        var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++)
            names.Add(device.DeviceString);
        return string.Join(" | ", names);
    }

    // Simple-mode encoder IDs: NVIDIA first, since it's usually the gaming GPU.
    static string HardwareEncoder()
    {
        string all = GpuNames();
        if (all.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "nvenc";
        if (all.Contains("AMD", StringComparison.OrdinalIgnoreCase) || all.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return "amd";
        if (all.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "qsv";
        return "x264";
    }
}

static class Ini
{
    public static string? Get(string path, string section, string key)
    {
        if (!File.Exists(path)) return null;
        string? current = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1];
                continue;
            }
            int eq = line.IndexOf('=');
            if (current == section && eq > 0 && line[..eq] == key) return line[(eq + 1)..];
        }
        return null;
    }

    public static void Set(string path, string section, string key, string value)
    {
        bool exists = File.Exists(path);
        var lines = exists ? File.ReadAllLines(path).ToList() : new List<string>();
        bool bom = exists && HasBom(path);
        string? current = null;
        int insertAt = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (current == section) break;
                current = line[1..^1];
                if (current == section) insertAt = i + 1;
                continue;
            }
            if (current != section) continue;
            int eq = line.IndexOf('=');
            if (eq > 0 && line[..eq] == key)
            {
                lines[i] = key + "=" + value;
                Write(path, lines, bom);
                return;
            }
            if (line.Length > 0) insertAt = i + 1;
        }
        if (insertAt < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
        }
        else
        {
            lines.Insert(insertAt, $"{key}={value}");
        }
        Write(path, lines, bom);
    }

    static bool HasBom(string path)
    {
        var start = new byte[3];
        using var stream = File.OpenRead(path);
        return stream.Read(start, 0, 3) == 3 && start[0] == 0xEF && start[1] == 0xBB && start[2] == 0xBF;
    }

    static void Write(string path, List<string> lines, bool bom)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines, new UTF8Encoding(bom));
    }
}

// Copies OBS Clipper into the user's programs folder with a Start menu shortcut, so it can
// be run straight from a download.
static class Installer
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OBS Clipper");
    public static string Exe => Path.Combine(Dir, "OBSClipper.exe");
    public const string ExitSignalName = "OBSClipper.Exit";

    // True when this copy started the installed one and should exit.
    public static bool RelaunchFromInstallDir()
    {
        var current = Environment.ProcessPath;
        if (current == null || string.Equals(Path.GetFullPath(current), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase)) return false;
        // Builds run from the project folder aren't installed.
        if (current.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)) return false;
        CloseRunningCopy();
        try
        {
            Directory.CreateDirectory(Dir);
            File.Copy(current, Exe, true);
        }
        catch (IOException)
        {
            // The installed copy is still running; it gets shown instead.
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        if (!File.Exists(Exe)) return false;
        CreateShortcuts();
        Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true, WorkingDirectory = Dir })?.Dispose();
        return true;
    }

    // Asks a running OBS Clipper to exit so its exe can be replaced by this version.
    static void CloseRunningCopy()
    {
        if (!EventWaitHandle.TryOpenExisting(ExitSignalName, out var signal)) return;
        using (signal) signal.Set();
        foreach (var p in Process.GetProcessesByName("OBSClipper").Where(p => p.Id != Environment.ProcessId))
        {
            using (p) p.WaitForExit(10_000);
        }
    }

    static void CreateShortcuts()
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            // Start menu only.
            dynamic link = shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "OBS Clipper.lnk"));
            link.TargetPath = Exe;
            link.WorkingDirectory = Dir;
            link.Save();
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Log.Write("Could not create shortcuts: " + ex.Message);
        }
    }
}
