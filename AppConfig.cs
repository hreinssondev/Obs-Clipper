using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OBSClipper;

sealed class HotkeyBinding
{
    // Windows virtual-key code; 0 means not set yet.
    public int Key { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    // Clip length; 0 saves the whole replay buffer.
    public int Seconds { get; set; }

    public HotkeyBinding Copy() => (HotkeyBinding)MemberwiseClone();
}

sealed class AppConfig
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OBSClipper");
    static string FilePath => Path.Combine(Dir, "config.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public List<HotkeyBinding> Hotkeys { get; set; } = new();
    public string ClipsFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "OBS Clips");
    public string NameFormat { get; set; } = ClipNamer.DefaultFormat;
    public string FfmpegPath { get; set; } = FindFfmpeg();
    public string ObsPath { get; set; } = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";
    public bool StartObs { get; set; } = true;
    public bool KeepReplayBufferOn { get; set; } = true;
    public bool PlaySound { get; set; }
    // First-launch setup finished (or this PC was set up by hand).
    public bool SetupDone { get; set; }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Options) ?? Defaults();
        }
        catch (Exception ex)
        {
            Log.Write("Could not read config, using defaults: " + ex.Message);
        }
        var config = Defaults();
        config.Save();
        return config;
    }

    // Norwegian layout: + is VK_OEM_PLUS, \ is VK_OEM_4, ' is VK_OEM_2.
    static AppConfig Defaults() => new()
    {
        Hotkeys =
        {
            new() { Key = 0xBB, Seconds = 60 },
            new() { Key = 0x6B, Seconds = 60 },
            new() { Key = 0xDB, Seconds = 30 },
            new() { Key = 0xBF, Seconds = 15 },
        },
    };

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            Log.Write("Could not save config: " + ex.Message);
        }
    }

    public static string FindFfmpeg()
    {
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(packages, "Gyan.FFmpeg*"))
            {
                var exe = Directory.EnumerateFiles(dir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (exe != null) return exe;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return "";
    }
}

static class ClipNamer
{
    public const string DefaultFormat = "MM.dd.yy - HH:mm";

    // Windows forbids ":" in file names, so it becomes U+A789 (꞉), which looks the same.
    public static string Format(DateTime time, string format)
    {
        string name;
        try
        {
            name = time.ToString(string.IsNullOrWhiteSpace(format) ? DefaultFormat : format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            name = time.ToString(DefaultFormat, CultureInfo.InvariantCulture);
        }
        name = name.Replace(':', '\uA789').Replace('/', '-').Replace('\\', '-');
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? "Clip" : name;
    }
}

static class Log
{
    static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppConfig.Dir);
                var path = Path.Combine(AppConfig.Dir, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                    File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}\r\n");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
