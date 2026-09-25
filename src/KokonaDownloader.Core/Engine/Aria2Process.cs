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

    /// <summary>
    /// 启动 aria2 子进程。<paramref name="finishedSessionEntries"/> 是"客户端确认早已下载完成"的
    /// URL+目录，启动前一并从会话文件里过滤掉（见 TombstoneStore.PurgeSessionFile 的重载）。
    /// </summary>
    public void Start(ICollection<(string Url, string? Dir)>? finishedSessionEntries = null)
    {
        if (IsRunning) return;
        Directory.CreateDirectory(_config.WorkDir);
        CleanupOrphan();
        var sessionFile = Path.Combine(_config.WorkDir, "aria2.session");
        var logFile = Path.Combine(_config.WorkDir, "aria2.log");
        if (!File.Exists(sessionFile)) File.WriteAllText(sessionFile, string.Empty);
        // 会话文件在异常退出时可能仍残留已删除任务，启动前用墓碑过滤，阻断重启后复活；
        // 顺带把"其实早就下完"的条目也滤掉——aria2 的 --input-file 不认这是完成过的下载，
        // 会把它当新任务重新加入（新 gid、0 B、排队中），用户看到的就是"已完成的下载又排了一遍队"。
        var purged = _tombstones?.PurgeSessionFile(sessionFile, finishedSessionEntries) ?? 0;
        if (purged > 0) _log?.Invoke($"已从会话文件过滤 {purged} 条（已删除或早已下载完成）");

        var args = BuildArgs(_config, sessionFile, logFile);

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
        // aria2c 控制台输出里大量是空白/纯空格行（实测占历史日志 96%）：只记录有实际内容的行，
        // 否则 app.log 会以 ~8MB/天 无界膨胀且几乎全是噪音。
        _process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log?.Invoke($"[aria2] {e.Data}"); };
        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log?.Invoke($"[aria2!err] {e.Data}"); };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        try { File.WriteAllText(PidFile, _process.Id.ToString()); } catch { }
        _log?.Invoke($"aria2 已启动 PID={_process.Id} 端口={_config.RpcPort}");
    }

    private string PidFile => Path.Combine(_config.WorkDir, "aria2.pid");

    /// <summary>
    /// 组装 aria2c 命令行（公开只为单测：第九轮之后"某个行为是否真的下发给了 aria2"必须能在测试里断言，
    /// 不能只靠肉眼看日志）。
    /// </summary>
    public static string[] BuildArgs(EngineConfig config, string sessionFile, string logFile)
    {
        var args = new List<string>
        {
            "--enable-rpc",
            $"--rpc-secret={config.RpcSecret}",
            "--rpc-listen-all=false",
            $"--rpc-listen-port={config.RpcPort}",
            $"--dir={config.DefaultDownloadDir}",
            $"--input-file={sessionFile}",
            $"--save-session={sessionFile}",
            "--save-session-interval=10",
            $"--max-concurrent-downloads={config.MaxConcurrentDownloads}",
            $"--split={config.DefaultConnections}",
            "--min-split-size=1M",
            "--max-connection-per-server=16",
            "--continue=true",
            // 第八轮 P3-6 的取证结论（.verify/fixtest/retry_probe.mjs，两组对照实测）：
            // 默认参数下 aria2 对 503 这类"临时 5xx"一次都不重试（服务器只收到 1 次请求就报 errorCode=29），
            // 加上 --retry-wait=2 后同一场景第 3 次请求即下载成功——所以这不是"重试太少"而是"根本不重试"。
            // 404 两种参数下都只请求 1 次（errorCode=3 Resource not found）：aria2 视其为永久错误，属合理行为。
            "--retry-wait=2",
            "--max-tries=5",
            "--auto-save-interval=10",
            "--allow-overwrite=true",
            "--enable-mmap=true",
            // 第十轮（测试报告 §16）：服务器把中文直接写进 filename="中文.pdf"（裸 UTF-8 字节，没按 RFC 编码）时，
            // aria2 默认按 Latin-1 解释这些字节，落盘名变成乱码 ä¸­æ.pdf；实测同一份 aria2c 加上这个开关后
            // 名字正确（.verify/nameprobe）。
            "--content-disposition-default-utf8=true",
            $"--log={logFile}",
            "--log-level=warn",
            "--summary-interval=0",
            "--console-log-level=warn",
            "--quiet=false",
            // 关掉控制台读数刷新：它会每秒往 stdout 打一行（内容基本是空白/进度条残影），
            // 被重定向进 app.log 后占全部行数的 96%（实测 44 MB / 64 万行），纯属噪声。
            "--show-console-readout=false"
        };
        if (config.GlobalSpeedLimit > 0)
            args.Add($"--max-overall-download-limit={config.GlobalSpeedLimit}");

        // BT/磁力支持：DHT + PEX + LPD，follow-torrent=mem 避免往用户目录写 .torrent；
        // bt-detach-seed-only 让做种任务不占用 max-concurrent-downloads 配额（Motrix 同款方案）
        if (config.BtEnabled)
        {
            var btArgs = new List<string>
            {
                "--enable-dht=true",
                "--enable-peer-exchange=true",
                "--bt-enable-lpd=true",
                $"--listen-port={config.BtListenPort}",
                $"--dht-listen-port={config.BtListenPort}",
                $"--dht-file-path={Path.Combine(config.WorkDir, "dht.dat")}",
                "--dht-entry-point=router.bittorrent.com:6881",
                "--follow-torrent=mem",
                "--bt-detach-seed-only=true",
                $"--bt-max-peers={config.BtMaxPeers}",
                // 伪装 Transmission UA/peer-id，避免部分 tracker 封锁 aria2（Motrix 同款做法）
                "--user-agent=Transmission/2.92",
                "--peer-id-prefix=-TR2920-"
            };
            // 做种策略：关闭做种用 seed-time=0 立即完成；否则按分享率/时长先到为准
            if (!config.BtSeedEnabled)
                btArgs.Add("--seed-time=0");
            else
            {
                if (config.SeedRatio > 0)
                    btArgs.Add($"--seed-ratio={config.SeedRatio.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                if (config.SeedTimeMinutes > 0)
                    btArgs.Add($"--seed-time={config.SeedTimeMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (!string.IsNullOrWhiteSpace(config.BtTrackers))
                btArgs.Add($"--bt-tracker={config.BtTrackers}");
            args.AddRange(btArgs);
        }
        return args.ToArray();
    }

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
