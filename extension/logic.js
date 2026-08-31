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

    /** 仅支持可由客户端下载的外部协议。 */
    function isSupportedUrl(url) {
        var u = trim(url).toLowerCase();
        return u.indexOf('http://') === 0 || u.indexOf('https://') === 0 || u.indexOf('ftp://') === 0;
    }

    /** 指向客户端自身 API 的地址不捕获，避免自环。 */
    function isOwnApiUrl(url, s) {
        var base = baseUrl(s).toLowerCase() + '/';
        var u = trim(url).toLowerCase();
        return u.indexOf(base) === 0;
    }

    /** 从 URL 推断文件名：去掉查询串/锚点/协议，解码最后一段路径。 */
    function fileNameFromUrl(url) {
        var path = String(url == null ? '' : url);
        var qi = path.indexOf('?');
        if (qi >= 0) path = path.substring(0, qi);
        var hi = path.indexOf('#');
        if (hi >= 0) path = path.substring(0, hi);
        var si = path.indexOf('://');
        if (si >= 0) path = path.substring(si + 3);
        var slash = path.lastIndexOf('/');
        var name = slash >= 0 ? path.substring(slash + 1) : path;
        try { name = decodeURIComponent(name); } catch (e) { /* 解码失败保留原样 */ }
        name = trim(name);
        return name ? name : 'download';
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
     *  1. 已开始（state 非 in_progress）：已完成/已中断/未知态
     *  2. 已有实际保存路径（filename 非空）：说明文件已在磁盘上（回放特征）
     *  3. 开始时间早于扩展启动时刻：启动防火墙
     *  4. 开始时间距今超过 30 秒：非本次新发起
     * @param {object} item chrome.downloads.DownloadItem
     * @param {number} extStartMs 扩展启动时刻（Date.now()），可选；不传则跳过启动防火墙
     */
    function isFreshDownload(item, extStartMs) {
        if (!item) return false;
        if (item.state && item.state !== 'in_progress') return false;
        if (item.filename) return false;
        var t = Date.parse(item.startTime || '');
        if (isNaN(t)) return true;
        if (extStartMs && t < extStartMs) return false;
        if (Date.now() - t > 30000) return false;
        return true;
    }

    /** 构造发送给客户端 /api/download 的请求体（与 ApiService.ApiDownloadRequest 契约一致）。 */
    function buildDownloadPayload(item, s) {
        var url = item.url;
        var name = item.filename ? baseName(item.filename) : '';
        if (!name) name = fileNameFromUrl(url);
        var payload = { urls: [url], filename: name };
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

    return {
        defaults: defaults,
        normalizeSettings: normalizeSettings,
        baseUrl: baseUrl,
        isSupportedUrl: isSupportedUrl,
        isOwnApiUrl: isOwnApiUrl,
        fileNameFromUrl: fileNameFromUrl,
        baseName: baseName,
        shouldCapture: shouldCapture,
        isFreshDownload: isFreshDownload,
        buildDownloadPayload: buildDownloadPayload,
        urlKey: urlKey
    };
})();
