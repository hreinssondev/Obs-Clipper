using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OBSClipper;

// Minimal obs-websocket v5 client: connects on localhost, sends requests and
// reports the output events OBS Clipper cares about.
sealed class ObsClient
{
    const int EventsGeneral = 1, EventsOutputs = 64;

    public event Action<bool>? ConnectionChanged;
    public event Action<bool>? ReplayBufferChanged;
    public event Action<string>? ReplaySaved;
    public event Action<string>? RecordingStopped;

    public bool Connected { get; private set; }

    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> requests = new();
    readonly SemaphoreSlim sendLock = new(1, 1);
    volatile ClientWebSocket? socket;
    string lastError = "";

    public void Start() => Task.Run(() => RunAsync(stop.Token));

    public void Stop()
    {
        stop.Cancel();
        try { socket?.Abort(); } catch (ObjectDisposedException) { }
    }

    async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var settings = ObsWebSocketConfig.Read();
            if (settings.Enabled && ObsProcess.IsRunning())
            {
                try
                {
                    await SessionAsync(settings, ct);
                }
                catch (Exception ex) when (ex is WebSocketException or IOException or JsonException or InvalidOperationException or KeyNotFoundException)
                {
                    if (ex.Message != lastError) Log.Write("OBS connection: " + ex.Message);
                    lastError = ex.Message;
                }
                catch (OperationCanceledException) { }
                finally
                {
                    socket = null;
                    SetConnected(false);
                    foreach (var pending in requests.Values) pending.TrySetCanceled();
                    requests.Clear();
                }
            }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    async Task SessionAsync(ObsWebSocketConfig.Settings settings, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{settings.Port}"), ct);

        using (var hello = await ReceiveAsync(ws, ct))
        {
            if (hello == null || hello.RootElement.GetProperty("op").GetInt32() != 0) return;
            var d = hello.RootElement.GetProperty("d");
            var identify = new Dictionary<string, object> { ["rpcVersion"] = 1, ["eventSubscriptions"] = EventsGeneral | EventsOutputs };
            if (d.TryGetProperty("authentication", out var auth))
                identify["authentication"] = Authenticate(settings.Password, auth.GetProperty("salt").GetString()!, auth.GetProperty("challenge").GetString()!);
            await SendAsync(ws, new Dictionary<string, object> { ["op"] = 1, ["d"] = identify });
        }
        using (var identified = await ReceiveAsync(ws, ct))
        {
            if (identified == null || identified.RootElement.GetProperty("op").GetInt32() != 2)
            {
                if (lastError != "rejected") Log.Write($"OBS refused the connection ({ws.CloseStatusDescription ?? "no reason"})");
                lastError = "rejected";
                return;
            }
        }

        lastError = "";
        socket = ws;
        SetConnected(true);
        while (ws.State == WebSocketState.Open)
        {
            using var message = await ReceiveAsync(ws, ct);
            if (message == null) return;
            Handle(message.RootElement);
        }
    }

    void Handle(JsonElement message)
    {
        int op = message.GetProperty("op").GetInt32();
        var d = message.GetProperty("d");
        if (op == 7)
        {
            var id = d.GetProperty("requestId").GetString();
            if (id != null && requests.TryRemove(id, out var pending))
                pending.TrySetResult(d.Clone());
        }
        else if (op == 5)
        {
            d.TryGetProperty("eventData", out var data);
            switch (d.GetProperty("eventType").GetString())
            {
                case "ReplayBufferSaved":
                    if (Str(data, "savedReplayPath") is string replay) ReplaySaved?.Invoke(replay);
                    break;
                case "ReplayBufferStateChanged":
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("outputActive", out var active))
                        ReplayBufferChanged?.Invoke(active.ValueKind == JsonValueKind.True);
                    break;
                case "RecordStateChanged":
                    if (Str(data, "outputState") == "OBS_WEBSOCKET_OUTPUT_STOPPED" && Str(data, "outputPath") is string recording)
                        RecordingStopped?.Invoke(recording);
                    break;
            }
        }
    }

    public async Task<JsonElement?> RequestAsync(string type, Dictionary<string, object>? data = null)
    {
        var ws = socket;
        if (ws == null) return null;
        string id = Guid.NewGuid().ToString("N");
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        requests[id] = pending;
        try
        {
            var d = new Dictionary<string, object> { ["requestType"] = type, ["requestId"] = id };
            if (data != null) d["requestData"] = data;
            await SendAsync(ws, new Dictionary<string, object> { ["op"] = 6, ["d"] = d });
            if (await Task.WhenAny(pending.Task, Task.Delay(5000)) != pending.Task) return null;
            return await pending.Task;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            requests.TryRemove(id, out _);
        }
    }

    public static bool Succeeded(JsonElement? response) =>
        response is JsonElement r && r.TryGetProperty("requestStatus", out var status)
        && status.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.True;

    public static bool OutputActive(JsonElement? response) =>
        response is JsonElement r && r.TryGetProperty("responseData", out var data)
        && data.TryGetProperty("outputActive", out var active) && active.ValueKind == JsonValueKind.True;

    void SetConnected(bool connected)
    {
        if (Connected == connected) return;
        Connected = connected;
        Log.Write(connected ? "Connected to OBS" : "Disconnected from OBS");
        ConnectionChanged?.Invoke(connected);
    }

    async Task SendAsync(ClientWebSocket ws, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await sendLock.WaitAsync();
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            sendLock.Release();
        }
    }

    static async Task<JsonDocument?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16384];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return JsonDocument.Parse(message.ToArray());
    }

    static string Authenticate(string password, string salt, string challenge)
    {
        string secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
