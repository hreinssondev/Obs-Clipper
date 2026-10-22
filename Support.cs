using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OBSClipper;

// Makes sure OBS records the PC's sound. A brand-new OBS started without its first-run
// wizard (which setup skips) gets no Desktop Audio, so its clips come out silent.
static class ObsAudio
{
    static readonly string[] PcSoundKinds = { "wasapi_output_capture", "wasapi_process_output_capture" };

    // Sources capturing what the PC plays: desktop audio or one app's audio.
    public static async Task<List<(string Name, string Kind)>> PcSoundSourcesAsync(ObsClient obs) =>
        (await InputNamesAsync(obs)).Where(i => PcSoundKinds.Contains(i.Kind)).ToList();

    static async Task<List<(string Name, string Kind)>> InputNamesAsync(ObsClient obs)
    {
        var sources = new List<(string, string)>();
        var list = Json.Data(await obs.RequestAsync("GetInputList"));
        if (list.ValueKind == JsonValueKind.Object && list.TryGetProperty("inputs", out var inputs))
            foreach (var input in inputs.EnumerateArray())
            {
                string kind = Json.Str(input, "unversionedInputKind") ?? Json.Str(input, "inputKind") ?? "";
                if (Json.Str(input, "inputName") is string name) sources.Add((name, kind));
            }
        return sources;
    }

    // Adds an audio source on the default device to the current scene. Returns its name, or null.
    static async Task<string?> AddAsync(ObsClient obs, string name, string kind)
    {
        string? scene = Json.Str(Json.Data(await obs.RequestAsync("GetCurrentProgramScene")), "currentProgramSceneName");
        if (scene == null) return null;
        foreach (var candidate in new[] { name, name + " (OBS Clipper)" })
        {
            var created = await obs.RequestAsync("CreateInput", new()
            {
                ["sceneName"] = scene, ["inputName"] = candidate, ["inputKind"] = kind,
                ["inputSettings"] = new Dictionary<string, object> { ["device_id"] = "default" }, ["sceneItemEnabled"] = true,
            });
            if (ObsClient.Succeeded(created)) return candidate;
        }
        return null;
    }

    // Bitmask of the audio tracks recordings (and so replays) contain.
    public static async Task<int> RecordedTracksAsync(ObsClient obs)
    {
        // Simple output mode always records track 1.
        if (await ProfileParameterAsync(obs, "Output", "Mode") != "Advanced") return 1;
        return int.TryParse(await ProfileParameterAsync(obs, "AdvOut", "RecTracks"), out int mask) ? mask : 1;
    }

    // Adds Desktop Audio if nothing records the PC's sound, and fixes sources that can't be
    // heard in clips: muted, volume at zero, on no recorded track, or set to a device that's gone.
    // Returns what was fixed, empty when everything was fine.
    // newObs: OBS was just installed, so it also gets the microphone its first run would have added.
    public static async Task<List<string>> FixAsync(ObsClient obs, bool newObs)
    {
        var fixes = new List<string>();
        var sources = await PcSoundSourcesAsync(obs);
        if (sources.Count == 0)
        {
            if (await AddAsync(obs, "Desktop Audio", "wasapi_output_capture") is not string name)
                throw new SetupException("OBS wouldn't add Desktop Audio. In OBS, add an \"Audio Output Capture\" source, then try again.");
            sources.Add((name, "wasapi_output_capture"));
            fixes.Add($"added {name}");
        }
        if (newObs && !(await InputNamesAsync(obs)).Any(i => i.Kind == "wasapi_input_capture")
            && await AddAsync(obs, "Mic/Aux", "wasapi_input_capture") is string mic)
            fixes.Add($"added {mic}");

        int recorded = await RecordedTracksAsync(obs);
        bool restartBuffer = false;
        if (recorded == 0)
        {
            await obs.RequestAsync("SetProfileParameter", new() { ["parameterCategory"] = "AdvOut", ["parameterName"] = "RecTracks", ["parameterValue"] = "1" });
            recorded = 1;
            restartBuffer = true;
            fixes.Add("turned on audio track 1 for recordings");
        }
        int firstTrack = Enumerable.Range(1, 6).First(t => (recorded & (1 << (t - 1))) != 0);

        foreach (var (name, kind) in sources)
        {
            var input = new Dictionary<string, object> { ["inputName"] = name };
            if (Json.Bool(Json.Data(await obs.RequestAsync("GetInputMute", input)), "inputMuted") == true)
            {
                await obs.RequestAsync("SetInputMute", new() { ["inputName"] = name, ["inputMuted"] = false });
                fixes.Add($"unmuted {name}");
            }
            var volume = Json.Data(await obs.RequestAsync("GetInputVolume", input));
            if (volume.ValueKind == JsonValueKind.Object && volume.TryGetProperty("inputVolumeMul", out var mul) && mul.GetDouble() < 0.01)
            {
                await obs.RequestAsync("SetInputVolume", new() { ["inputName"] = name, ["inputVolumeMul"] = 1.0 });
                fixes.Add($"turned up {name}");
            }
            var tracks = Json.Data(await obs.RequestAsync("GetInputAudioTracks", input));
            if (tracks.ValueKind == JsonValueKind.Object && tracks.TryGetProperty("inputAudioTracks", out var on)
                && !Enumerable.Range(1, 6).Any(t => (recorded & (1 << (t - 1))) != 0 && Json.Bool(on, t.ToString()) == true))
            {
                await obs.RequestAsync("SetInputAudioTracks", new()
                {
                    ["inputName"] = name, ["inputAudioTracks"] = new Dictionary<string, object> { [firstTrack.ToString()] = true },
                });
                fixes.Add($"put {name} on audio track {firstTrack}");
            }
            if (kind == "wasapi_output_capture" && await DeviceAsync(obs, name) is (_, false))
            {
                await obs.RequestAsync("SetInputSettings", new()
                {
                    ["inputName"] = name, ["inputSettings"] = new Dictionary<string, object> { ["device_id"] = "default" },
                });
                fixes.Add($"switched {name} to the default device (its device is gone)");
            }
        }

        if (restartBuffer && ObsClient.OutputActive(await obs.RequestAsync("GetReplayBufferStatus")))
        {
            await obs.RequestAsync("StopReplayBuffer");
            for (int i = 0; i < 20 && ObsClient.OutputActive(await obs.RequestAsync("GetReplayBufferStatus")); i++)
                await Task.Delay(250);
            await obs.RequestAsync("StartReplayBuffer");
        }
        return fixes;
    }

    // The device an audio output capture records and whether it's still there.
    public static async Task<(string Name, bool Present)?> DeviceAsync(ObsClient obs, string inputName)
    {
        var settings = Json.Data(await obs.RequestAsync("GetInputSettings", new() { ["inputName"] = inputName }));
        string id = settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("inputSettings", out var s)
            ? Json.Str(s, "device_id") ?? "default" : "default";
        var items = await DevicesAsync(obs, inputName);
        if (items.Count == 0) return null;
        var match = items.FirstOrDefault(i => i.Id == id);
        return match.Id == null ? (id, false) : (match.Name, match.Enabled);
    }

    // The playback devices OBS offers for an audio output capture.
    public static async Task<List<(string Id, string Name, bool Enabled)>> DevicesAsync(ObsClient obs, string inputName)
    {
        var devices = new List<(string, string, bool)>();
        var data = Json.Data(await obs.RequestAsync("GetInputPropertiesListPropertyItems", new() { ["inputName"] = inputName, ["propertyName"] = "device_id" }));
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("propertyItems", out var items))
            foreach (var item in items.EnumerateArray())
                devices.Add((Json.Str(item, "itemValue") ?? "", Json.Str(item, "itemName") ?? "", Json.Bool(item, "itemEnabled") != false));
        return devices;
    }

    static async Task<string?> ProfileParameterAsync(ObsClient obs, string category, string name)
    {
        var data = Json.Data(await obs.RequestAsync("GetProfileParameter", new() { ["parameterCategory"] = category, ["parameterName"] = name }));
        return Json.Str(data, "parameterValue") ?? Json.Str(data, "defaultParameterValue");
    }
}

// A text report of everything that matters for clips, for whoever's helping with a problem.
// Leaves out the WebSocket password.
static class Diagnostics
{
    public static async Task<string> BuildAsync(AppConfig config, ObsClient obs)
    {
        var text = new StringBuilder();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        text.AppendLine($"OBS Clipper {version} diagnostics, {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine($"Windows: {RuntimeInformation.OSDescription}");
        text.AppendLine($"GPU: {string.Join(" | ", ObsSettings.GpuNames().Split(" | ").Distinct())}");
        text.AppendLine($"OBS: {config.ObsPath} (exists: {File.Exists(config.ObsPath)}, running: {ObsProcess.IsRunning()}, WebSocket on: {ObsWebSocketConfig.Read().Enabled}, connected: {obs.Connected})");
        text.AppendLine($"ffmpeg: {config.FfmpegPath} (exists: {File.Exists(config.FfmpegPath)})");
        text.AppendLine($"Clips folder: {config.ClipsFolder}");
        text.AppendLine("Hotkeys: " + string.Join(", ", config.Hotkeys.Select(b => $"{KeyNames.Describe(b)} = {b.Seconds} s")));

        Section(text, "OBS audio");
        if (obs.Connected)
            await AudioAsync(text, obs);
        else
            text.AppendLine("OBS isn't connected, so its audio sources can't be read.");

        Section(text, "OBS output settings");
        string ini = ObsSettings.ProfileIni();
        text.AppendLine(ini);
        foreach (var section in new[] { "Output", "SimpleOutput", "AdvOut", "Audio", "Video" })
        {
            var lines = Ini.Section(ini, section);
            if (lines.Count > 0) text.AppendLine($"[{section}] " + string.Join("  ", lines));
        }

        Section(text, "Latest clip");
        text.AppendLine(LatestClip(config));

        Section(text, "OBS log (audio lines)");
        foreach (var line in ObsLogAudioLines()) text.AppendLine(line);

        Section(text, "OBS Clipper log (last 40 lines)");
        foreach (var line in Tail(Path.Combine(AppConfig.Dir, "log.txt"), 40)) text.AppendLine(line);
        return text.ToString();
    }

    static async Task AudioAsync(StringBuilder text, ObsClient obs)
    {
        var obsVersion = Json.Data(await obs.RequestAsync("GetVersion"));
        text.AppendLine($"OBS version: {Json.Str(obsVersion, "obsVersion")}");
        text.AppendLine($"Replay buffer on: {ObsClient.OutputActive(await obs.RequestAsync("GetReplayBufferStatus"))}");
        int recorded = await ObsAudio.RecordedTracksAsync(obs);
        text.AppendLine("Recorded tracks: " + string.Join(", ", Enumerable.Range(1, 6).Where(t => (recorded & (1 << (t - 1))) != 0)));

        var list = Json.Data(await obs.RequestAsync("GetInputList"));
        if (list.ValueKind != JsonValueKind.Object || !list.TryGetProperty("inputs", out var inputs)) return;
        string? firstDesktop = null;
        foreach (var input in inputs.EnumerateArray())
        {
            if (Json.Str(input, "inputName") is not string name) continue;
            string kind = Json.Str(input, "inputKind") ?? "";
            var request = new Dictionary<string, object> { ["inputName"] = name };
            // Only audio sources have tracks.
            var tracks = Json.Data(await obs.RequestAsync("GetInputAudioTracks", request));
            if (tracks.ValueKind != JsonValueKind.Object || !tracks.TryGetProperty("inputAudioTracks", out var on)) continue;
            bool? muted = Json.Bool(Json.Data(await obs.RequestAsync("GetInputMute", request)), "inputMuted");
            var volume = Json.Data(await obs.RequestAsync("GetInputVolume", request));
            string db = volume.ValueKind == JsonValueKind.Object && volume.TryGetProperty("inputVolumeDb", out var v) ? $"{v.GetDouble():0.#} dB" : "?";
            var onTracks = Enumerable.Range(1, 6).Where(t => Json.Bool(on, t.ToString()) == true);
            string line = $"- {name} [{kind}] muted: {muted}, volume: {db}, tracks: {string.Join(",", onTracks)}";
            if (kind.StartsWith("wasapi_output_capture") || kind.StartsWith("wasapi_input_capture"))
            {
                if (await ObsAudio.DeviceAsync(obs, name) is var (device, present)) line += $", device: {device}{(present ? "" : " (NOT FOUND)")}";
                if (kind.StartsWith("wasapi_output_capture")) firstDesktop ??= name;
            }
            text.AppendLine(line);
        }
        if (firstDesktop == null)
        {
            text.AppendLine("No Desktop Audio source: game sound isn't recorded.");
            return;
        }
        text.AppendLine("Playback devices: " + string.Join(", ", (await ObsAudio.DevicesAsync(obs, firstDesktop)).Select(d => d.Name)));
    }

    // Shows whether the newest clip has an audio stream.
    static string LatestClip(AppConfig config)
    {
        try
        {
            var clip = new DirectoryInfo(config.ClipsFolder).EnumerateFiles()
                .Where(f => f.Extension is ".mp4" or ".mkv" or ".mov")
                .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            if (clip == null) return "No clips yet.";
            if (!File.Exists(config.FfmpegPath)) return $"{clip.Name} (ffmpeg missing, can't inspect it)";
            var psi = new ProcessStartInfo(config.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-hide_banner", "-i", clip.FullName }) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            var output = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10_000)) p.Kill();
            var streams = output.Result.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("Duration") || l.StartsWith("Stream #"));
            return $"{clip.Name} ({clip.LastWriteTime:yyyy-MM-dd HH:mm})\n" + string.Join("\n", streams);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "Couldn't inspect the latest clip: " + ex.Message;
        }
    }

    static IEnumerable<string> ObsLogAudioLines()
    {
        var logs = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "logs"));
        var newest = logs.Exists ? logs.EnumerateFiles("*.txt").OrderByDescending(f => f.LastWriteTime).FirstOrDefault() : null;
        if (newest == null) return new[] { "No OBS log found." };
        var pattern = new Regex(@"audio|wasapi|samples per sec|speaker|device", RegexOptions.IgnoreCase);
        return new[] { newest.Name }.Concat(Tail(newest.FullName, 5000).Where(l => pattern.IsMatch(l)).TakeLast(60));
    }

    static IEnumerable<string> Tail(string path, int count)
    {
        try
        {
            // OBS keeps its log open, so share it.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>();
            while (reader.ReadLine() is string line)
            {
                lines.Enqueue(line);
                if (lines.Count > count) lines.Dequeue();
            }
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new[] { $"Couldn't read {Path.GetFileName(path)}: {ex.Message}" };
        }
    }

    static void Section(StringBuilder text, string title) => text.AppendLine().AppendLine($"== {title} ==");
}

// Windows' built-in Quick Assist: lets someone helping take control of this PC.
static class RemoteHelp
{
    public static bool Open()
    {
        foreach (var target in new[] { "ms-quick-assist:", "quickassist.exe", "ms-windows-store://pdp/?productid=9P7BP5VNWKX5" })
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
                return true;
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return false;
    }
}

static class Json
{
    public static JsonElement Data(JsonElement? response) =>
        response is JsonElement r && r.TryGetProperty("responseData", out var data) ? data : default;

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.ValueKind == JsonValueKind.True : null;
}
