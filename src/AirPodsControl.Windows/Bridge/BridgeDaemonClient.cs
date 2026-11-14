using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AirPodsControl.Core.Services;

namespace AirPodsControl.Windows.Bridge;

/// <summary>
/// Long-running <c>wsl.exe … python3 /opt/airpods/aapd.py</c> process, spoken to with JSON lines over stdin/stdout.
/// No ports, pipes or extra packages; the process lives in the kill-on-close job object and keeps the WSL VM running,
/// so a mode switch no longer pays for wsl.exe start-up, dbus or bluetoothd.
/// </summary>
public sealed class BridgeDaemonClient(ILog log, string distro) : IDisposable
{
    private sealed record Pending(TaskCompletionSource<JsonElement> Reply, Action<JsonElement>? OnEvent);

    private readonly object _lock = new();
    private readonly ConcurrentDictionary<int, Pending> _pending = new();
    private Process? _process;
    private TaskCompletionSource<bool>? _ready;
    private int _nextId;

    public bool IsRunning { get { lock (_lock) return _process is { HasExited: false } && _ready?.Task.IsCompletedSuccessfully == true; } }

    /// <summary>Starts the daemon if needed and waits for its "ready" line. Returns null or an error message.</summary>
    public async Task<string?> EnsureStartedAsync(double idleMinutes, TimeSpan timeout, CancellationToken ct)
    {
        TaskCompletionSource<bool> ready;
        lock (_lock)
        {
            if (_process is { HasExited: false } && _ready is not null) ready = _ready;
            else
            {
                StopLocked();
                ready = _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var psi = ProcessRunner.CreateStartInfo(WslClient.Exe, ["--distribution", distro, "--user", "root", "--exec",
                    "python3", "-u", "/opt/airpods/aapd.py", "--idle-exit", (idleMinutes * 60).ToString("0", CultureInfo.InvariantCulture)]);
                psi.StandardInputEncoding = new UTF8Encoding(false); // a BOM would corrupt the first JSON request
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (_, e) => { if (e.Data is not null) OnLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Debug("aapd stderr: " + e.Data); };
                p.Exited += (_, _) => OnExited(p);
                try { p.Start(); }
                catch (Win32Exception ex) { _ready = null; return "Cannot start wsl.exe: " + ex.Message; }
                ProcessRunner.AssignToJob(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                _process = p;
                log.Info("aapd: started (pid " + p.Id + ")");
            }
        }
        var done = await Task.WhenAny(ready.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (done != ready.Task) { Stop(); return "The bridge daemon did not start in time."; }
        return await ready.Task.ConfigureAwait(false) ? null : "The bridge daemon exited during start-up (scripts missing? see log).";
    }

    /// <summary>Sends one request and waits for its reply. Stage/packet lines for this request go to <paramref name="onEvent"/>.
    /// On timeout the daemon is stopped (its state is unknown) and the next request starts a fresh one.</summary>
    public async Task<JsonElement?> RequestAsync(string op, IDictionary<string, object?> fields, TimeSpan timeout, CancellationToken ct,
        Action<JsonElement>? onEvent = null)
    {
        Process? p;
        lock (_lock) p = _process;
        if (p is null || p.HasExited) return null;
        var id = Interlocked.Increment(ref _nextId);
        var pending = new Pending(new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously), onEvent);
        _pending[id] = pending;
        var body = new Dictionary<string, object?>(fields) { ["id"] = id, ["op"] = op };
        try
        {
            lock (_lock)
            {
                p.StandardInput.WriteLine(JsonSerializer.Serialize(body));
                p.StandardInput.Flush();
            }
            var done = await Task.WhenAny(pending.Reply.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (done != pending.Reply.Task)
            {
                log.Warn($"aapd: '{op}' timed out after {timeout.TotalSeconds:0}s — restarting the daemon next time");
                Stop();
                return null;
            }
            return await pending.Reply.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.Warn($"aapd: daemon exited during '{op}'");
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            log.Warn("aapd: request failed: " + ex.Message);
            Stop();
            return null;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private void OnLine(string line)
    {
        JsonElement root;
        try { using var doc = JsonDocument.Parse(line); root = doc.RootElement.Clone(); }
        catch (JsonException) { log.Debug("aapd: " + line); return; }
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        int? id = root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : null;
        switch (type)
        {
            case "ready":
                log.Info("aapd: ready " + line);
                lock (_lock) _ready?.TrySetResult(true);
                break;
            case "reply" when id is { } rid && _pending.TryGetValue(rid, out var pr):
                pr.Reply.TrySetResult(root);
                break;
            case "stage" or "pkt" when id is { } eid && _pending.TryGetValue(eid, out var pe):
                log.Debug("aapd: " + line);
                try { pe.OnEvent?.Invoke(root); } catch (Exception ex) { log.Warn("aapd event handler failed", ex); }
                break;
            default:
                log.Info("aapd: " + line);
                break;
        }
    }

    private void OnExited(Process p)
    {
        int code = -1;
        try { code = p.ExitCode; } catch { }
        log.Info("aapd: exited (code " + code + ")");
        lock (_lock)
        {
            if (_process == p) { _ready?.TrySetResult(false); _process = null; _ready = null; }
        }
        foreach (var kv in _pending) kv.Value.Reply.TrySetCanceled();
    }

    public void Stop() { lock (_lock) StopLocked(); }

    private void StopLocked()
    {
        var p = _process;
        _process = null;
        _ready?.TrySetResult(false);
        _ready = null;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                try { p.StandardInput.WriteLine("{\"op\": \"quit\"}"); p.StandardInput.Flush(); } catch { }
                if (!p.WaitForExit(1500)) p.Kill(entireProcessTree: true);
            }
        }
        catch { }
        p.Dispose();
    }

    public void Dispose() => Stop();
}
