/**
 * EXT-403：把"浏览器会内联渲染"的文件改成下载（Declarative Net Request）。
 *
 * 为什么需要：`downloads.onCreated` 只在浏览器**已经决定要下载**时触发。
 * PDF、图片直链、文本类附件这类响应，浏览器会直接在标签页里打开，永远不产生下载项，
 * 扩展也就完全看不到。MV3 拿不到 webRequestBlocking，唯一能在响应阶段干预的手段是
 * DNR 的 `modifyHeaders`：把响应头改成 `Content-Disposition: attachment`，
 * 浏览器就会走下载流程，从而落回我们的主链路。
 *
 * 已知限制（都写在这里，免得以后误以为是 bug）：
 *  - DNR **不能按响应头/Content-Type 做条件**，只能按 URL 正则匹配。所以靠扩展名判断，
 *    对 `?file=123` 这种不带扩展名的地址无效。
 *  - 只能 `set` 不能"仅在缺失时补"，服务器原本的 `Content-Disposition: inline` 会被覆盖。
 *  - 只作用于 `main_frame`（用户主动导航/点击）。不进 XHR，避免把站点的接口响应变成下载。
 */

/**
 * 规则 id 区间：一条扩展名一条规则（原因见下），所以按区间预留，同步时整段删掉重建。
 *
 * 为什么不合并成一条正则：Chrome 对单条 `regexFilter` 有 **2KB 编译后内存**限制，
 * 60 多个扩展名拼成一条会直接超出，规则被**静默跳过**（`updateDynamicRules` 不报错，
 * 但规则表里没有它）。这个坑只有真浏览器能撞出来——单测里正则照样能跑。
 */
export const FORCE_ATTACHMENT_RULE_BASE = 9001;
export const FORCE_ATTACHMENT_RULE_SLOTS = 128;

/** 需要清理的全部规则 id（多于实际条数也无害）。 */
export const FORCE_ATTACHMENT_RULE_IDS =
  Array.from({ length: FORCE_ATTACHMENT_RULE_SLOTS }, (_, i) => FORCE_ATTACHMENT_RULE_BASE + i);

/**
 * 保守清单：这些类型在浏览器里"以内联展示为主、用户点它通常是想要文件"。
 * 刻意不含 json/xml/txt/svg/html —— 那些多半是接口响应或页面，强制下载会破坏站点。
 */
export const FORCE_ATTACHMENT_EXTENSIONS = [
  // 文档
  'pdf', 'epub', 'mobi', 'azw3', 'djvu',
  // 压缩包 / 镜像
  'zip', 'rar', '7z', 'tar', 'gz', 'tgz', 'bz2', 'xz', 'zst', 'iso', 'img',
  // 安装包 / 可执行
  'exe', 'msi', 'msix', 'apk', 'deb', 'rpm', 'dmg', 'pkg', 'cab', 'bin',
  // 办公
  'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'odt', 'ods', 'odp', 'rtf',
  // 音视频（浏览器会内联播放）
  'mp3', 'flac', 'wav', 'ape', 'm4a', 'ogg', 'mp4', 'mkv', 'avi', 'mov', 'wmv', 'flv', 'webm', 'm4v',
  // 其它
  'torrent', 'jar',
];

/**
 * 单个扩展名的匹配正则。
 * 结尾必须是 `?`/`#`/行尾，否则 `a.pdfx` 会被 `a.pdf` 误命中。
 */
export function buildForceAttachmentPattern(exts = FORCE_ATTACHMENT_EXTENSIONS) {
  const alts = exts
    .map((e) => String(e).trim().toLowerCase())
    .filter(Boolean)
    .map((e) => e.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
    .join('|');
  return `^https?://[^?#]*\\.(${alts})(?:[?#]|$)`;
}

/** 单扩展名版（真正下发用的就是它）。 */
export function buildSingleExtensionPattern(ext) {
  const safe = String(ext).trim().toLowerCase().replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return `^https?://[^?#]*\\.${safe}(?:[?#]|$)`;
}

/** 生成 DNR 动态规则；关闭时返回空数组（调用方靠 removeRuleIds 清掉旧规则）。 */
export function buildForceAttachmentRules(enabled) {
  if (!enabled) return [];
  return FORCE_ATTACHMENT_EXTENSIONS.map((ext, i) => ({
    id: FORCE_ATTACHMENT_RULE_BASE + i,
    priority: 1,
    action: {
      type: 'modifyHeaders',
      responseHeaders: [
        { header: 'content-disposition', operation: 'set', value: 'attachment' },
      ],
    },
    condition: {
      regexFilter: buildSingleExtensionPattern(ext),
      isUrlFilterCaseSensitive: false,
      // 只拦顶层导航。放进 sub_frame 会把站点内嵌的 PDF 预览器也顶掉，
      // 放进 xmlhttprequest 则会把 fetch 到的文件变成下载，破坏页面逻辑。
      resourceTypes: ['main_frame'],
    },
  }));
}

/**
 * 是否应该真的下发规则。三个条件缺一不可：
 *  - 用户开了这个实验开关
 *  - 自动接管是开着的（接管关了还强制下载，只会让浏览器多下几个文件，纯副作用）
 *  - 有站点访问权限（DNR 改响应头必须有对应 host 权限，否则规则不会生效）
 */
export function decideForceAttachment({ forceAttachment, interceptEnabled, hostAccess }) {
  if (!forceAttachment) return { apply: false, reason: 'disabled' };
  if (!interceptEnabled) return { apply: false, reason: 'intercept-off' };
  if (!hostAccess) return { apply: false, reason: 'no-host-access' };
  return { apply: true, reason: 'on' };
}

/** 给 UI 的一句话说明（options / popup 共用）。 */
export function describeForceAttachment(decision) {
  const d = decision || { apply: false, reason: 'disabled' };
  if (d.apply) return '已生效：PDF / 压缩包等类型会直接交给海兔下载';
  if (d.reason === 'intercept-off') return '未生效：自动接管已关闭';
  if (d.reason === 'no-host-access') return '未生效：需要先在弹窗里授权访问网站';
  return '未开启';
}
