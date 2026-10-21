using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OBSClipper;

// Takes replays OBS wrote to its staging folder, trims them with ffmpeg if a
// short clip was asked for, and moves them into the clips folder Medal watches.
// Medal only ever sees finished files.
sealed class ClipSaver
{
    readonly Func<AppConfig> config;
    readonly object gate = new();
    // Lengths of saves sent to OBS but not written yet, oldest first.
    readonly List<int> expected = new();
    // Names taken by clips that are still being trimmed.
    readonly HashSet<string> reserved = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? ClipReady;

    public ClipSaver(Func<AppConfig> config) => this.config = config;

    public void Expect(int seconds)
    {
        lock (gate) expected.Add(seconds);
    }

    public void Unexpect(int seconds)
    {
        lock (gate)
        {
            int i = expected.LastIndexOf(seconds);
            if (i >= 0) expected.RemoveAt(i);
        }
    }

    // Replays saved from OBS itself (not a hotkey here) have nothing expected and stay whole.
    public void HandleReplay(string path)
    {
        int seconds = 0;
        lock (gate)
        {
            if (expected.Count > 0)
            {
                seconds = expected[0];
                expected.RemoveAt(0);
            }
        }
        Task.Run(() => Finish(path, seconds));
    }

    public void HandleRecording(string path) => Task.Run(() => Finish(path, 0));

    void Finish(string source, int seconds)
    {
        var cfg = config();
        source = source.Replace('/', '\\');
        string ext = Path.GetExtension(source);
        if (ext.Length == 0) ext = ".mp4";
        string dest;
        lock (gate)
        {
            dest = UniqueDestination(cfg, ext);
            reserved.Add(dest);
        }
        try
        {
            Directory.CreateDirectory(cfg.ClipsFolder);
            if (seconds <= 0 || !Trim(cfg.FfmpegPath, source, dest, seconds))
                MoveWithRetry(source, dest);
            Log.Write($"Saved {Path.GetFileName(dest)}" + (seconds > 0 ? $" ({seconds} s)" : ""));
            ClipReady?.Invoke(dest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Could not save {source}: {ex.Message}");
        }
        finally
        {
            lock (gate) reserved.Remove(dest);
        }
    }

    static bool Trim(string ffmpeg, string source, string dest, int seconds)
    {
        if (!File.Exists(ffmpeg))
        {
            Log.Write("ffmpeg not found, keeping the whole replay");
            return false;
        }
        string part = source + ".part" + Path.GetExtension(source);
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-sseof", "-" + seconds, "-i", source,
                                    "-map", "0", "-c", "copy", "-movflags", "+faststart", part })
            psi.ArgumentList.Add(arg);
        try
        {
            using var p = Process.Start(psi)!;
            string errors = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode == 0 && File.Exists(part))
            {
                MoveWithRetry(part, dest);
                File.Delete(source);
                return true;
            }
            Log.Write("ffmpeg failed: " + errors.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Log.Write("ffmpeg failed: " + ex.Message);
        }
        try { File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return false;
    }

    // OBS can hold the file for a moment after reporting it saved.
    static void MoveWithRetry(string from, string to)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(from, to, true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
    }

    string UniqueDestination(AppConfig cfg, string ext)
    {
        string stem = ClipNamer.Format(DateTime.Now, cfg.NameFormat);
        string dest = Path.Combine(cfg.ClipsFolder, stem + ext);
        for (int n = 2; File.Exists(dest) || reserved.Contains(dest); n++)
            dest = Path.Combine(cfg.ClipsFolder, $"{stem} ({n}){ext}");
        return dest;
    }
}
