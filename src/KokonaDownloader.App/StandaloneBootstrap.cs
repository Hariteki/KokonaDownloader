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
/// 完整性与并发（v1.0.8 第二轮审计修复）：
/// - 运行时目录带一份清单（嵌入载荷内容哈希 + 文件列表 + exe 哈希/大小/mtime）。
///   清单与当前嵌入载荷一致且文件齐全 → 跳过解压；exe 未变化 → 跳过 304.9 MB 拷贝。
///   （原先每次冷启动无条件重解压 ~7 MB 并覆盖整个 exe。）
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
        catch
        {
            // 尽力而为：解压彻底失败时让 XAML 初始化按原有方式报错，不在此处吞掉。
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
    /// 1) 清单与当前嵌入载荷一致（内容哈希 + 文件齐全且大小匹配）→ 跳过解压，否则全量重解；
    /// 2) exe 仅在"目标缺失 / 大小或 mtime 变化且内容哈希确实不同"时拷贝，
    ///    拷贝成功才更新清单记录（失败则保留旧记录，下次启动自愈重试）。
    /// </summary>
    private static void EnsureRuntimeComplete(System.Reflection.Assembly asm, List<string> names, string runtimeDir, string exePath)
    {
        var root = Path.GetFullPath(runtimeDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        // 当前嵌入载荷指纹：全部资源按名称序的内容哈希（~7 MB，毫秒级）
        var payloadHash = HashPayload(asm, names);
        var manifest = ReadManifest(root);

        var upToDate = manifest != null
            && string.Equals(manifest.PayloadHash, payloadHash, StringComparison.OrdinalIgnoreCase)
            && manifest.Files.Count == names.Count
            && manifest.Files.All(f => File.Exists(root + f.Rel) && new FileInfo(root + f.Rel).Length == f.Size);

        if (!upToDate)
        {
            ExtractAll(asm, names, runtimeDir);
            manifest = new Manifest
            {
                PayloadHash = payloadHash,
                Files = names.Select(n =>
                {
                    var rel = n[ResourcePrefix.Length..].Replace('\\', '/');
                    return new ManifestFile { Rel = rel, Size = new FileInfo(root + rel).Length };
                }).ToList(),
            };
        }

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
