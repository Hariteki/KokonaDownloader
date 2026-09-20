using System.Runtime.InteropServices;

// CA5392（第四轮审计 P2-9）：本项目的全部 P/Invoke 目标（user32 / kernel32 / shcore /
// dwmapi / comctl32 / shell32）都是系统 DLL，默认搜索顺序却包含"exe 所在目录"。
// 单文件版会把运行时解压到 %LOCALAPPDATA%\KokonaDownloader\Standalone（用户可写），
// 那个目录里的同名 DLL 就有机会被优先加载 —— 属 DLL 劫持面。
// 在程序集级声明"只从 System32 加载"，等价于给每个 DllImport 单独标注，
// 且未来新增 P/Invoke 自动继承，不会漏。
// （该特性只允许标注在程序集或方法上：[module:] 会报 CS0592，实测过。）
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
