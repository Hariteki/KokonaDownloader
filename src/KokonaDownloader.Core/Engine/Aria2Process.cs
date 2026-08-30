using System.Diagnostics;

namespace KokonaDownloader.Core.Engine;

/// <summary>
/// aria2c 子进程生命周期管理：启动（--enable-rpc 后台模式）、健康检查、优雅退出。
/// 设计决策：aria2 以 --enable-rpc + --rpc-listen-all=false（仅本机）方式启动，
/// 会话文件持久化保证重启后任务恢复；退出时先 aria2.shutdown 再兜底 Kill。
/// </summary>
public sealed class Aria2Process : IDisposable
{
    private Process? _process;
    private readonly EngineConfig _config;
    private readonly Action<string>? _log;
    private readonly TombstoneStore? _tombstones;

    public bool IsRunning => _process is { HasExited: false };
    public int? ExitCode => _process?.HasExited == true ? _process.ExitCode : null;

    public Aria2Process(EngineConfig config, TombstoneStore? tombstones = null, Action<string>? log = null)
    {
        _config = config;
        _tombstones = tombstones;
        _log = log;
    }

    public void Start()
    {
        if (IsRunning) return;
        Directory.CreateDirectory(_config.WorkDir);
        CleanupOrphan();
        var sessionFile = Path.Combine(_config.WorkDir, "aria2.session");
        var logFile = Path.Combine(_config.WorkDir, "aria2.log");
        if (!File.Exists(sessionFile)) File.WriteAllText(sessionFile, string.Empty);
        // 会话文件在异常退出时可能仍残留已删除任务，启动前用墓碑过滤，阻断重启后复活
        var purged = _tombstones?.PurgeSessionFile(sessionFile) ?? 0;
        if (purged > 0) _log?.Invoke($"已从会话文件过滤 {purged} 条已删除任务");

        var args = new[]
        {
            "--enable-rpc",
            $"--rpc-secret={_config.RpcSecret}",
            "--rpc-listen-all=false",
            $"--rpc-listen-port={_config.RpcPort}",
            $"--dir={_config.DefaultDownloadDir}",
            $"--input-file={sessionFile}",
            $"--save-session={sessionFile}",
            "--save-session-interval=10",
            $"--max-concurrent-downloads={_config.MaxConcurrentDownloads}",
            $"--split={_config.DefaultConnections}",
            "--min-split-size=1M",
            "--max-connection-per-server=16",
            "--continue=true",
            "--auto-save-interval=10",
            "--allow-overwrite=true",
            "--enable-mmap=true",
            $"--log={logFile}",
            "--log-level=warn",
            "--summary-interval=0",
            "--console-log-level=warn",
            "--quiet=false"
        };
        if (_config.GlobalSpeedLimit > 0)
            args = args.Append($"--max-overall-download-limit={_config.GlobalSpeedLimit}").ToArray();

        var psi = new ProcessStartInfo
        {
            FileName = _config.Aria2Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _process = new Process { StartInfo = psi };
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) _log?.Invoke($"[aria2] {e.Data}"); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) _log?.Invoke($"[aria2!err] {e.Data}"); };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        try { File.WriteAllText(PidFile, _process.Id.ToString()); } catch { }
        _log?.Invoke($"aria2 已启动 PID={_process.Id} 端口={_config.RpcPort}");
    }

    private string PidFile => Path.Combine(_config.WorkDir, "aria2.pid");

    /// <summary>
    /// 清理上次异常退出遗留的孤儿 aria2c 进程：
    /// 读取 PID 文件，若该进程仍存活且占用 RPC 端口则终止它，避免端口与密钥冲突。
    /// </summary>
    private void CleanupOrphan()
    {
        try
        {
            if (!File.Exists(PidFile)) return;
            var pidText = File.ReadAllText(PidFile).Trim();
            File.Delete(PidFile);
            if (!int.TryParse(pidText, out var pid)) return;
            var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return;
            // 确认是 aria2c 进程再终止，避免误杀
            if (!proc.ProcessName.StartsWith("aria2", StringComparison.OrdinalIgnoreCase)) return;
            _log?.Invoke($"清理孤儿 aria2 进程 PID={pid}");
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(3000);
        }
        catch (ArgumentException) { /* 进程已不存在 */ }
        catch (Exception ex) { _log?.Invoke($"清理孤儿进程异常: {ex.Message}"); }
    }

    /// <summary>等待 RPC 就绪（轮询 getVersion），超时抛异常。</summary>
    public async Task WaitForReadyAsync(Aria2RpcClient client, int timeoutMs = 15000, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsRunning)
                throw new InvalidOperationException($"aria2 进程意外退出 (exit={ExitCode})");
            if (await client.PingAsync(ct).ConfigureAwait(false)) return;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"aria2 RPC 在 {timeoutMs}ms 内未就绪");
    }

    /// <summary>优雅停止：aria2.shutdown → 等待退出 → 兜底 Kill。</summary>
    public async Task StopAsync(Aria2RpcClient? client, int waitMs = 5000)
    {
        if (_process is null || _process.HasExited) return;
        try
        {
            if (client != null)
            {
                using var cts = new CancellationTokenSource(2000);
                await client.SaveSessionAsync(cts.Token).ConfigureAwait(false);
                await client.ShutdownAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch { /* 进程可能已半死，忽略走兜底 */ }

        try { File.Delete(PidFile); } catch { }
        try
        {
            if (await WaitForExitAsync(waitMs).ConfigureAwait(false)) return;
            _process.Kill(entireProcessTree: true);
            _log?.Invoke("aria2 进程被强制终止");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"停止 aria2 异常: {ex.Message}");
        }
    }

    /// <summary>立即强杀 aria2 进程树（快速退出用）：会话每 10 秒自动落盘、
    /// 启动前有墓碑过滤兜底，硬杀不会造成任务丢失或复活。</summary>
    public void KillNow()
    {
        var p = _process;
        if (p is null || p.HasExited) return;
        try
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(1000);
        }
        catch (Exception ex) { _log?.Invoke($"强杀 aria2 失败: {ex.Message}"); }
    }

    private async Task<bool> WaitForExitAsync(int ms)
    {
        var p = _process;
        if (p == null) return true;
        try
        {
            using var cts = new CancellationTokenSource(ms);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return p.HasExited; }
        catch { return true; }
    }

    public void Dispose()
    {
        try { if (IsRunning) _process?.Kill(entireProcessTree: true); } catch { }
        _process?.Dispose();
    }
}
