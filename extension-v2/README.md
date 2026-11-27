# 海兔下载器 · 浏览器扩展 v2

把浏览器的下载交给海兔下载器桌面客户端（多线程分段、断点续传）。

这是重写版（`extension-v2/`），旧的 `extension/` 已废弃，只作对照保留。

**固定扩展 ID**：`oppjgcopibccjaoahhmbjogfknfbdoad`
`manifest.json` 里内置了 `key`，所以无论放在哪个目录、解压到哪台机器，ID 都不变——
配对信息（密钥）跟着 ID 走，挪目录不会丢。

---

## 安装

### 方式一：加载已解压的扩展（推荐，便于更新）

1. 地址栏打开 `edge://extensions`（Chrome 用 `chrome://extensions`）
2. 打开左下角「开发人员模式」
3. 点「加载解压缩的扩展」，选择 `extension-v2` 目录（或解压后的 zip 目录）
4. 若之前装过旧版「海兔下载器」，先停用它，避免两个同时抢下载

### 方式二：用打好的 zip

```bash
node tools/pack_extension.mjs
# → dist/kokona-extension-v2-<版本>.zip
```

解压后按方式一加载。商店上传用 `--for-store`（会去掉 `manifest.key`，由商店分配 ID）：

```bash
node tools/pack_extension.mjs --for-store
```

---

## 首次配对

1. 启动海兔客户端
2. 客户端「设置 → 浏览器扩展连接」里复制密钥
3. 点工具栏的海兔图标 → 点「粘贴密钥并连接」（扩展直接读剪贴板，不需要手动粘贴）
4. 状态点变绿即成功

可选但强烈建议：点「授权访问网站（用于带上登录 Cookie）」。
不授权也能下公开文件，但**需要登录的站点会拿到登录页而不是文件**。

---

## 接管开关

| 位置 | 开关 | 行为 |
| --- | --- | --- |
| **扩展弹窗（一键）** | 自动接管浏览器下载 | 顶部主开关，点一下即切换。以**客户端**为单一事实源：在线时直接写客户端 `interceptBrowserDownloads`；离线时先落本地 `autoCapture` 兜底并记 `pending`，客户端上线后自动追平。徽章在「接管关」时显示琥珀色 `II` |
| 客户端设置 | 浏览器扩展自动拦截下载 | **总闸**。扩展每次判定前向客户端要这个值，客户端关了扩展就不接管 |
| 扩展设置 | 自动接管浏览器下载 | 客户端不可达时的本地兜底 |
| 扩展设置 | 把网页内联文件改为下载（实验） | 默认关。见下 |

> 一键开关与客户端「浏览器扩展自动拦截下载」是同一个值，从哪边改另一边都能同步到；
> 工具栏图标徽章一眼可辨：绿=接管开着、琥珀 `II`=接管已关、灰 `OFF`=客户端未运行、红 `!`=密钥错。

### 接管时序（避免浏览器先把文件下完）

`downloads.onCreated` 触发时浏览器**已经开始下载**。扩展会立刻 `downloads.pause`
（只停传输，条目和 Cookie/referrer 都保留），然后再做 Range 探测和转发：

- 客户端接受 → `cancel` + `erase`，浏览器下载项消失
- 客户端不可达 / 忙 / 不支持分段 / 被放行 → `resume`，浏览器原样续传，不需要重新发起下载

这样大文件不会在"探测 + 请求客户端"的几百毫秒里被浏览器下完，也不会出现浏览器和客户端各下一份。

### 不支持多线程的链接

接管前扩展会对目标地址发一个 `Range: bytes=0-1` 的探测请求（读到响应头就断开）：

- **206**：真的兑现区间请求 → 照常接管，客户端可以分段多线程
- **200 / 416**：明确不支持分段（含"声明支持但不兑现"的服务器）→ **不接管**，浏览器下载继续，
  并弹一条提示。客户端拿过去也只能单连接跑，不如留给浏览器
- **探测失败/超时**（3 秒，或没有站点权限）：无法判断 → 维持原行为，照常接管，避免误伤

### 右键「用浏览器下载（不接管）」

链接/图片/音视频的右键菜单里有这一项。点它不做任何探测，直接把地址记入一次性放行名单
（`storage.session`，5 分钟过期、命中即消费），再调用浏览器原生下载；随后触发的
`downloads.onCreated` 会被放行——既不转发，也不取消。

同一个链接之后再下一次，仍会按正常规则接管。

### 弹窗里的站点放行按钮

**橙色大按钮「本网站不接管下载 · 站点名」**：把当前标签页的站点（小写主机名、去掉 `www.`）
写进 `storage.local` 的 `siteBypass` 名单，**整站所有页面（含子域）都不接管**。
例如在 `apple.com` 上点一次，`www.apple.com`、`support.apple.com` 下的下载都直接留给浏览器。
按钮会变成「恢复接管本网站」，再点一次即恢复。判定时 referrer 与文件地址任一命中都算，
CDN 直链也不会漏。

> 按钮不改动全局接管开关，也不会调用客户端；popup 不再显示小字说明，状态看圆点颜色。

### 强制附件（实验）

`downloads.onCreated` 只在浏览器**已经决定下载**时触发。PDF、压缩包这类响应浏览器会直接
内联打开，扩展看不到。打开这个开关后，扩展会用 DNR 把这类响应的 `Content-Disposition`
改成 `attachment`，浏览器就会下载，从而落回主链路。

代价：**这些类型的站点内联预览会变成下载**。所以默认关，且只在「自动接管开着」时生效。
设置页会显示真实状态（已生效 / 未开启 / 未授权 / 浏览器拒绝）。

---

## 权限说明

| 权限 | 用途 |
| --- | --- |
| `downloads` | 接住浏览器下载、失败时保留原生下载 |
| `contextMenus` | 右键「使用海兔下载器下载」「用浏览器下载（不接管）」「下载本页所有链接」 |
| `storage` | 保存客户端地址与密钥；会话起点用于挡历史回放 |
| `clipboardRead` | 「粘贴密钥并连接」按钮读取剪贴板里的密钥 |
| `scripting` + `activeTab` | 「下载本页所有链接」按需注入，不常驻 content script |
| `cookies` | 采集会话 Cookie，让需要登录的下载能成功 |
| `notifications` | 转发失败、客户端未运行等提示 |
| `declarativeNetRequestWithHostAccess` | 强制附件开关（改响应头） |
| `host_permissions: 127.0.0.1 / localhost` | 只与本机客户端通信 |
| `optional_host_permissions: http(s)://*/*` | 读 Cookie / 改响应头需要对应站点权限，**按需申请** |

没有 `<all_urls>` 的强制授权，站点访问是可选权限，用户拒绝也能用（只是不带登录态、强制附件不生效）。

---

## 已知限制

- **与 IDM 浏览器扩展不能共存**。IDM 在 `webRequest.onBeforeRequest` 阶段就劫走下载并唤起
  `IDMan.exe`，浏览器因此**从不产生下载项**，本扩展看起来像"完全没反应"。需要在
  `edge://extensions` 里停用其中之一。
- **拿不到 `Authorization` 头**。MV3 没有 `webRequestBlocking`，JS 里用 `fetch` 带的
  Bearer token 无法捕获；这类站点只能靠 Cookie。
- **强制附件按 URL 扩展名判断**，`?file=123` 这种不带扩展名的地址无效（DNR 不能按 Content-Type 条件）。
- **接管前多一次探测请求**。只读响应头，正文不落盘；没有站点权限时探测直接失败并按"无法判断"处理。
- **Edge 的下载气泡无法被扩展隐藏**。MV3 只能在下载开始后 `pause/cancel`，所以点击瞬间仍可能闪一下
  下载气泡；`.exe/.msi` 被 Edge 判为可疑时，浏览器自己的「保留/删除」安全提示属于浏览器 UI，
  扩展关不掉。此时客户端文件已下载完成，点「删除」只会清掉浏览器那份临时副本。
- **服务器不给校验器、长度又恰好不变**时，客户端无法识别"文件被换过"，属协议层面无解。
- 不做媒体嗅探（m3u8 / `<video>` 里的流）。那是独立项目，不是扩展改造。

---

## 开发

```bash
cd extension-v2

# 纯逻辑 + SW 级联调（53 条，真实 HTTP 服务，不依赖浏览器）
node --test test/logic.test.mjs test/force-attachment.test.mjs test/sw.test.mjs
```

真浏览器验收脚本在 `repo/tools/`：

| 脚本 | 验证什么 |
| --- | --- |
| `dnr_probe.mjs` | DNR `modifyHeaders` 在真实 Edge 里能否把 `.pdf` 变成下载（静态规则，最纯的机制验证） |
| `dnr_ext_probe.mjs [--ext=<目录>]` | 真扩展：开关 → 规则进浏览器 → 产生下载 → 关掉后恢复 |
| `ext_cookie_e2e.mjs [--client=real --secret=…]` | 登录态下载：403 → 带 Cookie → 200，含真客户端落盘比对 |
| `ext_bypass_live.mjs` | 真扩展 + 真客户端：真实点击 popup 的橙色「本网站不接管/恢复接管」，断言浏览器真放行、客户端真接管 |
| `pack_extension.mjs` | 打包，并校验 manifest 引用的文件都存在 |

真浏览器脚本都用 `--headless=new`，并加 `--disable-extensions-except` 只加载被测扩展
（否则本机 IDM 的外部扩展会插进来抢下载）。站点权限在测试副本里提升为必需权限，
以避开无头模式下的权限弹窗——**只改 manifest，不改代码**。

---

## 目录

```
extension-v2/
  manifest.json
  sw/background.js        事件路由与副作用（不含业务判定）
  lib/logic.js            纯函数：URL 判定 / 去重键 / 载荷组装 / 状态码映射 / Range 判定
  lib/probe.js            Range 能力嗅探（只读响应头，读完即断）
  lib/api.js              fetch 封装（超时 / 密钥 / 错误码）
  lib/settings.js         设置读写、会话起点与一次性放行名单
  lib/cookies.js          Cookie / UA / 语言采集，可选权限申请
  lib/intercept.js        「以客户端为准」的拦截开关 + 短缓存
  lib/force-attachment.js DNR 规则的纯逻辑（扩展名清单 / 正则 / 生效条件）
  lib/dnr.js              唯一碰 chrome.declarativeNetRequest 的地方
  ui/                     popup / options / welcome
  test/                   node --test（不进包）
```
