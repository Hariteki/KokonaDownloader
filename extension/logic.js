/*
 * 海兔下载器扩展纯逻辑模块。
 * 不依赖任何浏览器扩展 API，ES3 兼容（可在 WSH/JScript 下直接单元测试）。
 * background.js / popup.js / options.js 与测试工程共用此文件，保证逻辑单一来源。
 */
var KokonaLogic = (function () {
    'use strict';

    function trim(s) {
        s = String(s == null ? '' : s);
        return s.replace(/^[\s\u00A0]+/, '').replace(/[\s\u00A0]+$/, '');
    }

    /** 默认设置：与桌面客户端 AppSettings 默认值对齐（端口 16800）。 */
    function defaults() {
        return { host: '127.0.0.1', port: 16800, secret: '', autoCapture: true };
    }

    /** 合并并规范化用户设置：容错处理粘贴了协议前缀、host:port、结尾斜杠等情况。 */
    function normalizeSettings(raw) {
        var d = defaults();
        raw = raw || {};

        var host = trim(raw.host);
        if (!host) host = d.host;
        host = host.replace(/^https?:\/\//i, '');
        var slash = host.indexOf('/');
        if (slash >= 0) host = host.substring(0, slash);
        var colon = host.indexOf(':');
        if (colon >= 0) host = host.substring(0, colon);
        if (!host) host = d.host;

        var port = parseInt(raw.port, 10);
        if (!(port > 0 && port <= 65535)) port = d.port;

        var secret = raw.secret == null ? '' : String(raw.secret);
        var autoCapture = raw.autoCapture !== false;

        return { host: host, port: port, secret: secret, autoCapture: autoCapture };
    }

    function baseUrl(s) {
        return 'http://' + s.host + ':' + s.port;
    }

    /** 仅支持可由客户端下载的外部协议（含磁力链接）。 */
    function isSupportedUrl(url) {
        var u = trim(url).toLowerCase();
        return u.indexOf('http://') === 0 || u.indexOf('https://') === 0 || u.indexOf('ftp://') === 0 || u.indexOf('magnet:') === 0;
    }

    /** 指向客户端自身 API 的地址不捕获，避免自环。 */
    function isOwnApiUrl(url, s) {
        var base = baseUrl(s).toLowerCase() + '/';
        var u = trim(url).toLowerCase();
        return u.indexOf(base) === 0;
    }

    /** 从完整路径（浏览器给出的拟保存路径）取文件名，兼容 / 与 \。 */
    function baseName(p) {
        p = String(p == null ? '' : p);
        var i = Math.max(p.lastIndexOf('/'), p.lastIndexOf('\\'));
        var name = i >= 0 ? p.substring(i + 1) : p;
        return trim(name);
    }

    /** 是否应自动捕获该下载项。 */
    function shouldCapture(item, s, extStartMs) {
        if (!item || !s) return false;
        if (!s.autoCapture) return false;
        if (!isSupportedUrl(item.url)) return false;
        if (isOwnApiUrl(item.url, s)) return false;
        if (!isFreshDownload(item, extStartMs)) return false;
        return true;
    }

    /**
     * 仅捕获"本次浏览器会话内新发起"的下载，拦截三类历史回放：
     *  - Edge 会话恢复/下载历史同步会把旧下载项重新触发 onCreated
     *  - 旧 aria2 类下载器/扩展遗留的已完成条目在启动时被回放
     * 判定（满足任一即视为历史项，不捕获）：
     *  1. 已经正常完成（state === 'complete'）：文件已在磁盘上，回放特征
     *  2. 已有实际保存路径（filename 非空）：说明文件已在磁盘上（回放特征）
     *  3. 开始时间早于扩展启动时刻：启动防火墙
     *  4. 开始时间距今超过 30 秒：非本次新发起
     *
     * 第 1 条在第八轮放宽过：原先"state 不是 in_progress 就拒绝"，可 404、连接被拒这类
     * 下载常在扩展的 onCreated 回调真正跑到之前就已经翻成 interrupted（MV3 service worker
     * 冷启动有延迟），于是**越是出错的链接越不会进客户端**，用户看到的是"点了没反应"。
     * 现在只拦"已完成"，中断态交给第 3/4 条时间窗判断是否本次新发起；
     * 第 2 条保持严格（有落盘路径仍视为回放），避免把历史项重新下一遍。
     * @param {object} item chrome.downloads.DownloadItem
     * @param {number} extStartMs 启动防火墙基准时刻（Date.now()，应按浏览器会话持久化）；不传则跳过该项检查
     */
    function isFreshDownload(item, extStartMs) {
        if (!item) return false;
        if (item.state === 'complete') return false;
        if (item.state && item.state !== 'in_progress' && item.state !== 'interrupted') return false;
        if (item.filename) return false;
        var t = Date.parse(item.startTime || '');
        if (isNaN(t)) return true;
        if (extStartMs && t < extStartMs) return false;
        if (Date.now() - t > 30000) return false;
        return true;
    }

    /** 构造发送给客户端 /api/download 的请求体（与 ApiService.ApiDownloadRequest 契约一致）。
     *  文件名只取浏览器已解析出的真实名（来自响应 Content-Disposition）；
     *  绝不从 URL 猜测——否则客户端会把 aria2 的 out 固定成 URL 里的临时名，
     *  覆盖服务器返回的真实文件名。不携带 filename 时由 aria2 自行按响应头解析。
     *  （客户端另有一道防线：与 URL 末段同名的伪文件名会被丢弃，
     *   见 DownloadEngine.DropUrlDerivedFileName，防止旧版扩展/外部脚本重新引入此问题。） */
    function buildDownloadPayload(item, s) {
        var url = item.url;
        var isMagnet = trim(url).toLowerCase().indexOf('magnet:') === 0;
        var name = item.filename ? baseName(item.filename) : '';
        var payload = { urls: [url] };
        if (name && !isMagnet) payload.filename = name;
        if (item.referrer) payload.referer = item.referrer;
        return payload;
    }

    /** 持久去重键：去掉锚点后对 URL 做 djb2 散列（避免明文存储完整下载地址）。 */
    function urlKey(url) {
        var u = trim(url);
        var hi = u.indexOf('#');
        if (hi >= 0) u = u.substring(0, hi);
        var h = 5381;
        for (var i = 0; i < u.length; i++) {
            h = ((h << 5) + h + u.charCodeAt(i)) >>> 0;
        }
        return h.toString(16);
    }

    /* 用户可见文案一律用 \u 转义写死，且本文件的中文注释只允许写成块注释：
       logic.js 除了被扩展按 UTF-8 加载，还会被 tests/test_logic.js 用 cscript 按系统 ANSI 码页
       eval。行注释若以中文收尾，其 UTF-8 尾字节可能被当作双字节字符的前导字节把换行一起吃掉，
       于是紧跟的那行 var 声明连带被注释掉（表现为"变量未定义"），只在中文码页下复现，极难查。 */
    var MSG_UNAUTHORIZED = '\u8fde\u63a5\u5bc6\u94a5\u9519\u8bef\uff0c\u8bf7\u70b9\u51fb\u5de5\u5177\u680f\u56fe\u6807\u91cd\u65b0\u7c98\u8d34\u5bc6\u94a5';
    var MSG_BUSY = '\u5ba2\u6237\u7aef\u6b63\u5fd9\uff08\u5e76\u53d1\u8bf7\u6c42\u5df2\u6ee1\uff09\uff0c\u8bf7\u7a0d\u540e\u91cd\u8bd5';
    var MSG_HTTP_PREFIX = '\u5ba2\u6237\u7aef\u8fd4\u56de\u9519\u8bef HTTP ';

    /**
     * 把客户端 /api/download 的响应映射为扩展内部结果（纯函数）。
     * 第四轮审计 N-2：这段映射原先埋在 background.js 的 async 流程里，只能靠浏览器手测；
     * 抽出来后每种状态码都能被 cscript 套件断言（含并发闸门返回的 503）。
     * 成功：{ ok: true, gid, duplicate, confirm }；失败：{ ok: false, code, message }。
     * code（与 background.js 抛出的 err.code 一致）：
     *   unauthorized 密钥错误（401）
     *   rejected     客户端拒绝该请求（400，如 URL 不合法）
     *   busy         客户端并发闸门已满（503），提示稍后重试
     *   http         其他 HTTP 错误
     * 客户端返回的 message 优先于内置文案（例如 400 会带上具体拒绝原因）。
     */
    function mapDownloadResponse(status, body) {
        var msg = (body && body.message) ? String(body.message) : '';
        if (status === 401) return { ok: false, code: 'unauthorized', message: MSG_UNAUTHORIZED };
        if (status === 503) return { ok: false, code: 'busy', message: msg || MSG_BUSY };
        if (!(status >= 200 && status < 300)) {
            return { ok: false, code: status === 400 ? 'rejected' : 'http', message: msg || (MSG_HTTP_PREFIX + status) };
        }
        return { ok: true, gid: body && body.gid, duplicate: !!(body && body.duplicate), confirm: !!(body && body.confirm) };
    }

    return {
        defaults: defaults,
        normalizeSettings: normalizeSettings,
        baseUrl: baseUrl,
        isSupportedUrl: isSupportedUrl,
        isOwnApiUrl: isOwnApiUrl,
        baseName: baseName,
        shouldCapture: shouldCapture,
        isFreshDownload: isFreshDownload,
        buildDownloadPayload: buildDownloadPayload,
        mapDownloadResponse: mapDownloadResponse,
        urlKey: urlKey
    };
})();
