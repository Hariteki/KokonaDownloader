# 海兔下载器 v1.0.5 单文件 exe 独立运行修复报告

## 问题描述
单文件 exe 复制到任意目录（如桌面 `C:\Users\lumin\Desktop\KokonaDownloader.exe`）后无法启动，XAML 运行时找不到 XBF/PRI 资源，抛出 `XamlParseException`。

## 根因分析
1. **.NET 8 `PublishSingleFile` 不打包松散文件**：`resources.pri`、`*.xbf`、`aria2c.exe`、`icons\` 等文件不会被嵌入到单文件 exe 中。
2. **`AppContext.BaseDirectory` 指向临时解压目录**：在单文件 bundle 中，`AppContext.BaseDirectory` 指向 .NET 运行时的临时解压目录，而非 exe 所在目录。
3. **XAML 运行时从 exe 目录加载资源**：WinUI 3 的 XAML 运行时通过 `ms-appx:` URI 从 exe 所在目录加载 XBF/PRI 资源，导致找不到文件。

## 修复方案
### 1. 嵌入资源到 exe
- **csproj 修改**：将 8 个 XBF 文件 + `resources.pri` + `aria2c.exe` + 2 个图标文件嵌入为 `EmbeddedResource`，通过自定义 MSBuild 任务 `SetStandaloneLogicalNames` 设置逻辑名称（`standalone/...`）。
- **MSBuild 任务**：`SetStandaloneLogicalNames` 是一个 Roslyn 内联任务，遍历 `EmbeddedResource` 项，为匹配前缀的项设置 `LogicalName` 属性。

### 2. 启动时解压到 exe 目录
- **`StandaloneBootstrap.cs`**：新增 `[ModuleInitializer]` 方法，在 XAML 初始化前运行：
  - 从 `Environment.ProcessPath` 推导 exe 所在目录（而非 `AppContext.BaseDirectory`）。
  - 若 `resources.pri` 已存在于 exe 旁则跳过解压（开发构建/已解压场景）。
  - 否则从嵌入资源解压到 exe 所在目录。
  - 若目录只读则回退到 `%LOCALAPPDATA%\KokonaDownloader\Standalone` 并重新拉起。

### 3. 只读目录回退
- 若 exe 所在目录只读，解压到 `%LOCALAPPDATA%\KokonaDownloader\Standalone`，复制 exe 到该目录，并重新拉起。

## 验证结果
### 测试 A：干净临时目录
- **结果**：✅ 通过
- **详情**：12 个文件全部解压到 exe 旁（`resources.pri`、8 个 XBF、`aria2c.exe`、2 个图标），API ping 返回 200（版本 1.0.5），无 `XamlParseException`。

### 测试 B：dist 目录（resources.pri 已存在）
- **结果**：✅ 通过
- **详情**：跳过解压，正常运行，API ping 返回 200。

### 测试 C：只读目录
- **结果**：✅ 通过
- **详情**：回退到 `%LOCALAPPDATA%\KokonaDownloader\Standalone` 并重新拉起，API ping 返回 200。

### 测试 D：桌面副本
- **结果**：✅ 通过
- **详情**：替换 `C:\Users\lumin\Desktop\KokonaDownloader.exe` 和 `C:\Users\lumin\Desktop\KokonaSingleFile\`，新 exe 大小 318,180,184 字节，SHA256 `6330A747E7C0F6CD0A23EECF0204D03495739C79072EE4D646F56D75A269FC0F`。

## 已知小问题（不影响使用）
- `POST /api/settings` 对未知字段返回 500「API 处理异常」（应容错忽略）。
- `GET /api/settings` 未返回 `minimizeToTrayOnClose` 字段。

## 交付物
- **GitHub 仓库**：`Hariteki/KokonaDownloader`，commit `cb800e1`，tag `v1.0.5` 已移至该 commit。
- **GitHub Release**：`v1.0.5`（id: 388315627），已删除旧 exe（id: 563176127），上传新 exe（id: 564634382，318,180,184 字节），更新 release body。
- **桌面副本**：`C:\Users\lumin\Desktop\KokonaDownloader.exe` 和 `C:\Users\lumin\Desktop\KokonaSingleFile\` 已刷新。

## 技术细节
- **嵌入资源列表**（12 个）：
  - `standalone/App.xbf`
  - `standalone/BtPieceGrid.xbf`
  - `standalone/MagnetConfirmWindow.xbf`
  - `standalone/MainWindow.xbf`
  - `standalone/NewDownloadDialog.xbf`
  - `standalone/ProgressWindow.xbf`
  - `standalone/SettingsWindow.xbf`
  - `standalone/Themes/ThemeSwatchPicker.xbf`
  - `standalone/resources.pri`
  - `standalone/aria2c.exe`
  - `standalone/icons/tray.ico`
  - `standalone/icons/tray.png`
- **MSBuild 任务**：`SetStandaloneLogicalNames`（Roslyn 内联任务，`ITask` 接口，`[Output] ResultItems` 往返）。
- **PRI 链**：`EmbedStandalonePayload` → `_GetSdkToolPaths;GetMrtPackagingOutputs;_GetDefaultResourceLanguage;_GenerateProjectPriFile`；`CopyXbfToBinForPri` → `_GenerateProjectPriConfigurationFiles`；`ProjectPriFileName=resources.pri`；`CopyXamlResourcesToPublishDir` → `Publish`。
