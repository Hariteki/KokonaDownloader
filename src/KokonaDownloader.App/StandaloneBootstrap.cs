using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace KokonaDownloader.App;

/// <summary>
/// 单文件独立运行引导。
/// .NET 单文件发布管线只打包主程序集与原生库，不会搬运 XBF/PRI/aria2c.exe/图标等散文件
/// （见 csproj 的 EmbedStandalonePayload 目标：这些文件被嵌入 exe 作为资源）。
/// 启动时（[ModuleInitializer]，先于 XAML 生成的 Main、早于 Application.Start 加载 XAML）：
/// 1) exe 旁已有 resources.pri（完整目录布局，如 dist / 桌面文件夹）→ 什么都不做；
/// 2) 否则确保 %LOCALAPPDATA%\KokonaDownloader\Standalone 里有完整运行时文件，
///    并把 exe 复制过去、从那里带原参数重新拉起本程序。
///
/// 完整性与并发（v1.0.8 第二/四轮审计修复）：
/// - 运行时目录带一份清单（嵌入载荷内容哈希 + 逐文件"大小+SHA256" + exe 哈希/大小/mtime）。
///   清单与当前嵌入载荷一致且文件"大小与内容都匹配" → 跳过解压；exe 未变化 → 跳过 304.9 MB 拷贝。
///   （原先每次冷启动无条件重解压 ~7 MB 并覆盖整个 exe；再早一版只比文件大小，
///    同尺寸但内容损坏的文件无法自愈 —— 第四轮 N-4 补上逐文件哈希。）
/// - 引导用命名互斥锁串行化：并发冷启动时，后到者等锁后复查完整性直接复用，
///   不再出现两个实例同时 ExtractAll 撞文件锁、静默中止引导导致文件不全的问题。
/// </summary>
internal static class StandaloneBootstrap
{
    private const string ResourcePrefix = "standalone/";
    private const string ManifestFileName = ".kokona_bootstrap.json";
    private const string BootstrapMutexName = @"Local\KokonaDownloader_Standalone_Bootstrap";

    [ModuleInitializer]
    internal static void InitializeStandaloneBootstrap()
    {
        try
        {
            EnsureRuntimeFiles();
        }
        catch (Exception ex)
        {
            // 这里确实会吞掉引导异常（第四轮 N-5：原注释写"不在此处吞掉"与实际相反）。
            // 之所以吞：ModuleInitializer 阶段任何异常都会让 CLR 直接放弃类型初始化并炸在启动路径上，
            // 表现为一闪而退且无痕迹。改为记一条日志（App.Log 全静态、自带 try/catch，此阶段可安全调用），
            // 之后仍会因缺 resources.pri 在 XAML 初始化处抛 XamlParseException —— 日志即定位线索。
            App.Log($"[bootstrap] 单文件引导失败（将退化为 XAML 初始化报错）: {ex}");
        }
    }

    internal static void EnsureRuntimeFiles()
    {
        // 单文件 exe 的 AppContext.BaseDirectory 指向临时解压目录，不是 exe 所在目录。
        // XAML 运行时从 exe 所在目录加载 XBF/PRI，所以必须把运行时文件放到 exe 所在目录。
        // 用户要求：exe 目录必须保持干净（只有 exe 本身），所以运行时文件统一解压到
        // %LOCALAPPDATA%\KokonaDownloader\Standalone，并把 exe 复制过去、从那里重新拉起。
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return;
        var baseDir = Path.GetDirectoryName(exePath) + Path.DirectorySeparatorChar;

        // 完整目录布局：resources.pri 已在 exe 旁（如 dist 目录 / 桌面文件夹），无需处理
        if (File.Exists(Path.Combine(baseDir, "resources.pri")))
            return;

        var asm = typeof(StandaloneBootstrap).Assembly;
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToList();
        if (names.Count == 0)
            return; // 未嵌入载荷的开发构建（如 IDE 直接运行），无需处理

        var runtimeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KokonaDownloader", "Standalone");
        Directory.CreateDirectory(runtimeDir);

        // 串行化并发冷启动：原先两个实例同时 ExtractAll 会撞文件锁，异常被上面的 catch
        // 吞掉后"静默中止引导"，第二实例带着不完整的文件启动。持锁者完成后，
        // 等待方复查完整性（清单匹配即跳过），不会重复解压。
        using var mutex = new Mutex(false, BootstrapMutexName);
        var ownsLock = false;
        try { ownsLock = mutex.WaitOne(TimeSpan.FromSeconds(120)); }
        catch (AbandonedMutexException) { ownsLock = true; } // 持锁者已退出，锁归本进程

        try
        {
            if (ownsLock)
                EnsureRuntimeComplete(asm, names, runtimeDir, exePath);
            else
                ExtractAll(asm, names, runtimeDir); // 等锁超时（极罕见）：退回旧的尽力而为路径
        }
        finally
        {
            if (ownsLock) mutex.ReleaseMutex();
        }

        var targetExe = Path.Combine(runtimeDir, Path.GetFileName(exePath));
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = targetExe,
            UseShellExecute = false,
        };
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
            psi.ArgumentList.Add(arg);
        System.Diagnostics.Process.Start(psi);
        Environment.Exit(0);
    }

    /// <summary>
    /// 持引导锁时确保运行时目录完整：
    /// 1) 清单与当前嵌入载荷一致（内容哈希 + 逐个文件"大小与 SHA256 都匹配"）→ 跳过解压，否则全量重解；
    /// 2) exe 仅在"目标缺失 / 大小或 mtime 变化且内容哈希确实不同"时拷贝，
    ///    拷贝成功才更新清单记录（失败则保留旧记录，下次启动自愈重试）。
    /// 校验代价：约 7 MB 载荷逐文件哈希，SHA256 在本级 SSD 上约 10 ms，远低于一次解压。
    /// </summary>
    private static void EnsureRuntimeComplete(System.Reflection.Assembly asm, List<string> names, string runtimeDir, string exePath)
    {
        var root = Path.GetFullPath(runtimeDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        // 当前嵌入载荷指纹：全部资源按名称序的内容哈希（~7 MB，毫秒级）
        var payloadHash = HashPayload(asm, names);
        var manifest = ReadManifest(root);

        // 逐文件校验（第四轮 N-4）：只比大小会放过"同尺寸内容损坏"的 XBF/PRI（一旦损坏就是启动即崩且永不自愈），
        // 因此校验通过的标准是"大小与内容都对"。校验失败即重解压，实现自愈。
        var verified = manifest != null
            && string.Equals(manifest.PayloadHash, payloadHash, StringComparison.OrdinalIgnoreCase)
            ? VerifyFiles(root, names, manifest)
            : null;

        List<ManifestFile> fileRecords;
        if (verified == null)
        {
            ExtractAll(asm, names, runtimeDir);
            fileRecords = names
                .Select(n => n[ResourcePrefix.Length..].Replace('\\', '/'))
                .Select(rel => new ManifestFile { Rel = rel, Size = new FileInfo(root + rel).Length, Hash = HashFileSafe(root + rel) })
                .ToList();
        }
        else
        {
            fileRecords = verified; // 校验过程中已顺带刷新（旧清单缺哈希时在此补齐）
        }

        // 复用旧清单的 exe 记录（ExeSize/ExeMtimeUtc/ExeHash），只更新载荷部分
        manifest ??= new Manifest();
        manifest.PayloadHash = payloadHash;
        manifest.Files = fileRecords;

        // exe 拷贝判定：大小+mtime 与记录一致 → 同一文件，跳过（不哈希 304.9 MB）；
        // 不一致 → 哈希比对，内容确实变了才覆盖拷贝。
        var targetExe = root + Path.GetFileName(exePath);
        var srcInfo = new FileInfo(exePath);
        if (!File.Exists(targetExe))
        {
            if (TryCopyExe(exePath, targetExe))
                manifest = WithExe(manifest, payloadHash, srcInfo);
        }
        else if (manifest == null || manifest.ExeSize != srcInfo.Length || manifest.ExeMtimeUtc != srcInfo.LastWriteTimeUtc)
        {
            if (HashFile(exePath) != manifest?.ExeHash)
            {
                // 内容确实变了（用户换过 exe）：覆盖拷贝；目标被运行中实例占用时保留旧版尽力复用
                if (TryCopyExe(exePath, targetExe))
                    manifest = WithExe(manifest, payloadHash, srcInfo);
            }
            else
            {
                // 仅 mtime 变化、内容未变：只刷新记录，不拷贝
                manifest = WithExe(manifest, payloadHash, srcInfo);
            }
        }

        if (manifest != null) WriteManifest(root, manifest);
    }

    private static bool TryCopyExe(string src, string dst)
    {
        try
        {
            File.Copy(src, dst, overwrite: true);
            return true;
        }
        catch
        {
            // 目标 exe 正被另一实例占用 → 直接复用已解压好的那份（尽力而为）
            return false;
        }
    }

    private static Manifest WithExe(Manifest? old, string payloadHash, FileInfo srcInfo) => new()
    {
        PayloadHash = payloadHash,
        Files = old?.Files ?? new List<ManifestFile>(),
        ExeSize = srcInfo.Length,
        ExeMtimeUtc = srcInfo.LastWriteTimeUtc,
        ExeHash = HashFile(srcInfo.FullName),
    };

    private static string HashPayload(System.Reflection.Assembly asm, List<string> names)
    {
        using var sha = SHA256.Create();
        var buf = new byte[81920];
        foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal))
        {
            using var src = asm.GetManifestResourceStream(name);
            if (src == null) continue;
            int read;
            while ((read = src.Read(buf, 0, buf.Length)) > 0)
                sha.TransformBlock(buf, 0, read, buf, 0);
        }
        // 必须先终结再取 Hash：本运行时（.NET 8）未终结时读 .Hash 会抛
        // CryptographicUnexpectedOperationException，曾导致整个引导被静默中止。
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>重解压后取实测哈希；极端情况（文件被占用/杀软暂锁）读不到就记空串，
    /// 下次启动会走"清单缺哈希"路径重新校验，不会误判为永久有效。</summary>
    private static string HashFileSafe(string path)
    {
        try { return HashFile(path); }
        catch { return string.Empty; }
    }

    /// <summary>逐个载荷文件校验"大小 + 内容"（第四轮 N-4）。
    /// 通过则返回可直接写回清单的记录（顺带补齐旧清单缺失的哈希）；
    /// 任一文件缺失、大小不符、或内容与清单不符，即返回 null 表示需要重解压自愈。
    /// 旧清单（无 Hash 字段）本轮只校验大小，并把实测哈希写回，下次起即可校验内容。</summary>
    private static List<ManifestFile>? VerifyFiles(string root, List<string> names, Manifest manifest)
    {
        if (manifest.Files.Count != names.Count) return null;
        var byRel = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in manifest.Files)
            byRel[f.Rel] = f;
        var records = new List<ManifestFile>(names.Count);
        foreach (var name in names)
        {
            var rel = name[ResourcePrefix.Length..].Replace('\\', '/');
            var path = root + rel;
            if (!byRel.TryGetValue(rel, out var rec) || !File.Exists(path)) return null;
            if (new FileInfo(path).Length != rec.Size) return null;
            var actual = HashFile(path);
            if (!string.IsNullOrEmpty(rec.Hash) && !string.Equals(actual, rec.Hash, StringComparison.OrdinalIgnoreCase))
                return null; // 同尺寸但内容已损坏：判为过期，重解压自愈
            records.Add(new ManifestFile { Rel = rel, Size = rec.Size, Hash = actual });
        }
        return records;
    }

    private static void ExtractAll(System.Reflection.Assembly asm, List<string> names, string destDir)
    {
        var root = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var name in names)
        {
            var rel = name[ResourcePrefix.Length..].Replace('\\', '/');
            var dest = Path.GetFullPath(root + rel);
            // 防路径穿越：解压目标必须位于 destDir 之内
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var src = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"缺少嵌入资源 {name}");
            using var dst = File.Create(dest);
            src.CopyTo(dst);
        }
    }

    private sealed class Manifest
    {
        public string PayloadHash { get; set; } = "";
        public long ExeSize { get; set; }
        public DateTime ExeMtimeUtc { get; set; }
        public string ExeHash { get; set; } = "";
        public List<ManifestFile> Files { get; set; } = new();
    }

    private sealed class ManifestFile
    {
        public string Rel { get; set; } = "";
        public long Size { get; set; }
        /// <summary>文件内容 SHA256（小写十六进制）。空串表示来自旧版清单，本轮校验后补齐。</summary>
        public string Hash { get; set; } = "";
    }

    private static Manifest? ReadManifest(string root)
    {
        try
        {
            var p = root + ManifestFileName;
            if (!File.Exists(p)) return null;
            return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(p));
        }
        catch { return null; } // 清单损坏 → 视为过期，全量重解
    }

    private static void WriteManifest(string root, Manifest m)
    {
        try
        {
            File.WriteAllText(root + ManifestFileName, JsonSerializer.Serialize(m));
        }
        catch { /* 写清单失败不致命：下次启动会再解压一次 */ }
    }
}
