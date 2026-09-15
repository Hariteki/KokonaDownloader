using System.Runtime.CompilerServices;

namespace KokonaDownloader.App;

/// <summary>
/// 单文件独立运行引导。
/// .NET 单文件发布管线只打包主程序集与原生库，不会搬运 XBF/PRI/aria2c.exe/图标等散文件
/// （见 csproj 的 EmbedStandalonePayload 目标：这些文件被嵌入 exe 作为资源）。
/// 启动时（[ModuleInitializer]，先于 XAML 生成的 Main、早于 Application.Start 加载 XAML）：
/// 1) exe 旁已有 resources.pri（完整目录布局，如 dist / 桌面文件夹）→ 什么都不做；
/// 2) 否则把全部嵌入文件解压到 exe 旁（桌面等用户可写目录）；
/// 3) 若该目录只读（如未提权的 Program Files）→ 解压到 %LOCALAPPDATA%\KokonaDownloader\Standalone，
///    并把 exe 复制过去、从那里带原参数重新拉起本程序。
/// </summary>
internal static class StandaloneBootstrap
{
    private const string ResourcePrefix = "standalone/";

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
        // XAML 运行时从 exe 所在目录加载 XBF/PRI，所以必须用 exe 目录。
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return;
        var baseDir = Path.GetDirectoryName(exePath) + Path.DirectorySeparatorChar;

        // 完整目录布局：resources.pri 已在 exe 旁，无需处理
        if (File.Exists(Path.Combine(baseDir, "resources.pri")))
            return;

        var asm = typeof(StandaloneBootstrap).Assembly;
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToList();
        if (names.Count == 0)
            return; // 未嵌入载荷的开发构建（如 IDE 直接运行），无需处理

        // 1) 首选：解压到 exe 旁
        try
        {
            ExtractAll(asm, names, baseDir);
            return;
        }
        catch
        {
            // 目录只读 → 走回退路径
        }

        // 2) 回退：解压到 %LOCALAPPDATA%\KokonaDownloader\Standalone，从那里重新拉起
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KokonaDownloader", "Standalone");
        Directory.CreateDirectory(fallback);
        ExtractAll(asm, names, fallback);

        var targetExe = Path.Combine(fallback, Path.GetFileName(exePath));
        try
        {
            File.Copy(exePath, targetExe, overwrite: true);
        }
        catch
        {
            // 目标 exe 正被另一实例占用 → 直接复用已解压好的那份
        }

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
}
