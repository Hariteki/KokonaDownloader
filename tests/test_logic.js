// logic.js unit tests (WSH/JScript, run with cscript //nologo tests\test_logic.js)
// All-ASCII: avoids ANSI/UTF-8 encoding mismatch under cscript.
var fso = new ActiveXObject("Scripting.FileSystemObject");
var here = fso.GetParentFolderName(WScript.ScriptFullName);
var logicPath = fso.BuildPath(fso.GetParentFolderName(here), "extension\\logic.js");
var src = fso.OpenTextFile(logicPath, 1).ReadAll();
eval(src); // load KokonaLogic

var pass = 0, fail = 0;
function ok(cond, name) {
    if (cond) { pass++; WScript.Echo("PASS  " + name); }
    else { fail++; WScript.Echo("FAIL  " + name); }
}
function pad(n) { return n < 10 ? "0" + n : "" + n; }
// Return a date string in JScript-parsable format "YYYY/MM/DD HH:mm:ss" (local time).
// logic.js uses Date.parse which accepts this format in both JScript and browsers.
function iso(ms) {
    var d = new Date(ms);
    return d.getFullYear() + "/" + pad(d.getMonth() + 1) + "/" + pad(d.getDate()) +
        " " + pad(d.getHours()) + ":" + pad(d.getMinutes()) + ":" + pad(d.getSeconds());
}
function mkItem(over) {
    var it = { url: "https://example.com/f.zip", state: "in_progress", filename: "", startTime: iso(Date.now()) };
    for (var k in over) it[k] = over[k];
    return it;
}
var S = { autoCapture: true, host: "127.0.0.1", port: 16800, secret: "" };
var NOW = Date.now();
var EXT_START = NOW - 5000; // extension started 5s ago

// ===== isFreshDownload =====
ok(KokonaLogic.isFreshDownload(mkItem({}), EXT_START) === true,
    "fresh in_progress download should be captured");
ok(KokonaLogic.isFreshDownload(mkItem({ state: "complete" }), EXT_START) === false,
    "complete item (history replay) should NOT be captured");
ok(KokonaLogic.isFreshDownload(mkItem({ state: "interrupted" }), EXT_START) === false,
    "interrupted item (history replay) should NOT be captured");
ok(KokonaLogic.isFreshDownload(mkItem({ filename: "C:/Users/x/Downloads/f.zip" }), EXT_START) === false,
    "item with real file path (already on disk = replay) should NOT be captured");
ok(KokonaLogic.isFreshDownload(mkItem({ startTime: iso(NOW - 86400000) }), EXT_START) === false,
    "download started 1 day ago (history) should NOT be captured");
ok(KokonaLogic.isFreshDownload(mkItem({ startTime: iso(NOW - 10000) }), EXT_START) === false,
    "download started before extension boot (startup firewall) should NOT be captured");
ok(KokonaLogic.isFreshDownload(mkItem({ startTime: iso(NOW - 60000) }), null) === false,
    "download started 60s ago (staleness window) should NOT be captured");
ok(KokonaLogic.isFreshDownload(mkItem({ startTime: "" }), EXT_START) === true,
    "download without startTime should be conservatively allowed");
ok(KokonaLogic.isFreshDownload(null, EXT_START) === false,
    "null item should NOT be captured");

// ===== shouldCapture integration =====
ok(KokonaLogic.shouldCapture(mkItem({}), S, EXT_START) === true,
    "shouldCapture: brand-new download + extStart -> capture");
ok(KokonaLogic.shouldCapture(mkItem({ state: "complete" }), S, EXT_START) === false,
    "shouldCapture: history replay -> NOT capture");
ok(KokonaLogic.shouldCapture(mkItem({ url: "http://127.0.0.1:16800/api/ping" }), S, EXT_START) === false,
    "shouldCapture: own API url -> NOT capture");
ok(KokonaLogic.shouldCapture(mkItem({ url: "blob:https://x.com/u" }), S, EXT_START) === false,
    "shouldCapture: blob scheme -> NOT capture");
ok(KokonaLogic.shouldCapture(mkItem({}), { autoCapture: false, host: "127.0.0.1", port: 16800 }, EXT_START) === false,
    "shouldCapture: autoCapture off -> NOT capture");

// ===== buildDownloadPayload =====
// Regression: the payload must NOT carry a filename guessed from the URL.
// A URL-guessed name would pin aria2's "out" to the temporary URL segment and
// override the real filename delivered via Content-Disposition (exe/zip bug).
var p1 = KokonaLogic.buildDownloadPayload(
    mkItem({ url: "https://example.com/download?id=12345" }), S);
ok(!("filename" in p1),
    "payload for temp-name URL must NOT carry a guessed filename");
ok(p1.urls.length === 1 && p1.urls[0] === "https://example.com/download?id=12345",
    "payload keeps the original url");

// Browser-resolved real filename (from Content-Disposition) is still forwarded.
var p2 = KokonaLogic.buildDownloadPayload(
    mkItem({ url: "https://example.com/download?id=12345", filename: "C:/Users/me/Downloads/real-file.exe" }), S);
ok(p2.filename === "real-file.exe",
    "browser-resolved filename is forwarded (basename only)");

// referrer passthrough.
var p3 = KokonaLogic.buildDownloadPayload(
    mkItem({ url: "https://example.com/a.zip", referrer: "https://example.com/page" }), S);
ok(p3.referer === "https://example.com/page", "referrer is forwarded");
ok(!("filename" in p3), "no filename when browser has none (aria2 resolves from headers)");

// Magnet links never carry a filename.
var p4 = KokonaLogic.buildDownloadPayload(
    mkItem({ url: "magnet:?xt=urn:btih:abc123", filename: "" }), S);
ok(!("filename" in p4), "magnet payload carries no filename");

// ===== normalizeSettings =====
// Settings can be typed/pasted by the user, so the normalizer must be forgiving.
var n1 = KokonaLogic.normalizeSettings({ host: "http://192.168.1.5/", port: "17000", secret: "k" });
ok(n1.host === "192.168.1.5", "normalizeSettings strips protocol prefix and trailing slash");
ok(n1.port === 17000, "normalizeSettings accepts port given as a string");

var n2 = KokonaLogic.normalizeSettings({ host: "127.0.0.1:17000", port: "" });
ok(n2.host === "127.0.0.1", "normalizeSettings strips :port suffix from the host field");
// Recorded current behaviour: the port embedded in the host string is dropped, not adopted.
// A user pasting "host:port" into the host box therefore keeps the default port.
ok(n2.port === 16800, "normalizeSettings: port embedded in host string is NOT adopted (falls back to default)");

var n3 = KokonaLogic.normalizeSettings({});
ok(n3.host === "127.0.0.1" && n3.port === 16800 && n3.secret === "" && n3.autoCapture === true,
    "normalizeSettings defaults match client defaults (16800)");

ok(KokonaLogic.normalizeSettings({ port: 99999 }).port === 16800,
    "normalizeSettings rejects out-of-range port");
ok(KokonaLogic.normalizeSettings({ port: 0 }).port === 16800,
    "normalizeSettings rejects zero port");
ok(KokonaLogic.normalizeSettings({ host: "   ", port: "" }).host === "127.0.0.1",
    "normalizeSettings blank host falls back to loopback");
ok(KokonaLogic.normalizeSettings({ autoCapture: false }).autoCapture === false,
    "normalizeSettings preserves autoCapture=false");
ok(KokonaLogic.normalizeSettings({ secret: null }).secret === "",
    "normalizeSettings null secret becomes empty string");
ok(KokonaLogic.normalizeSettings({ host: "localhost:16800/api" }).host === "localhost",
    "normalizeSettings strips both :port and path from host");

// ===== baseUrl =====
ok(KokonaLogic.baseUrl({ host: "127.0.0.1", port: 16800 }) === "http://127.0.0.1:16800",
    "baseUrl builds http://host:port");

// ===== isSupportedUrl =====
ok(KokonaLogic.isSupportedUrl("ftp://example.com/f.zip") === true, "ftp url supported");
ok(KokonaLogic.isSupportedUrl("magnet:?xt=urn:btih:abc") === true, "magnet url supported");
ok(KokonaLogic.isSupportedUrl("HTTPS://example.com/f.zip") === true, "scheme match is case-insensitive");
ok(KokonaLogic.isSupportedUrl("  https://example.com/f.zip  ") === true, "surrounding whitespace trimmed");
ok(KokonaLogic.isSupportedUrl("file:///C:/f.zip") === false, "file scheme NOT supported");
ok(KokonaLogic.isSupportedUrl("data:text/plain,hi") === false, "data scheme NOT supported");
ok(KokonaLogic.isSupportedUrl("") === false, "empty url NOT supported");

// ===== isOwnApiUrl (self-loop guard) =====
ok(KokonaLogic.isOwnApiUrl("http://127.0.0.1:16800/api/ping", S) === true,
    "own API url detected (would cause a self-loop)");
ok(KokonaLogic.isOwnApiUrl("http://127.0.0.1:16801/api/ping", S) === false,
    "different port is NOT own API");
ok(KokonaLogic.isOwnApiUrl("https://example.com/", S) === false,
    "foreign host is NOT own API");

// ===== baseName =====
ok(KokonaLogic.baseName("C:\\Users\\me\\Downloads\\a.zip") === "a.zip", "baseName handles windows path");
ok(KokonaLogic.baseName("/home/me/a.zip") === "a.zip", "baseName handles posix path");
ok(KokonaLogic.baseName("plain.zip") === "plain.zip", "baseName passes through bare name");
ok(KokonaLogic.baseName("C:\\dir\\") === "", "baseName of trailing separator is empty");
ok(KokonaLogic.baseName("") === "", "baseName of empty string is empty");

// ===== urlKey (in-flight duplicate guard) =====
ok(KokonaLogic.urlKey("https://a/b.zip") === KokonaLogic.urlKey("https://a/b.zip"),
    "urlKey is deterministic");
ok(KokonaLogic.urlKey("https://a/b.zip") !== KokonaLogic.urlKey("https://a/c.zip"),
    "different urls get different keys");
ok(KokonaLogic.urlKey("https://a/b.zip#frag") === KokonaLogic.urlKey("https://a/b.zip"),
    "urlKey ignores the fragment");
ok(KokonaLogic.urlKey("https://a/b.zip?x=1") !== KokonaLogic.urlKey("https://a/b.zip"),
    "urlKey keeps the query string distinct");
ok(/^[0-9a-f]+$/.test(KokonaLogic.urlKey("anything")), "urlKey is a lowercase hex string");

// ===== shouldCapture: remaining combinations =====
ok(KokonaLogic.shouldCapture(mkItem({ url: "ftp://example.com/f.zip" }), S, EXT_START) === true,
    "shouldCapture: ftp download captured");
ok(KokonaLogic.shouldCapture(mkItem({ url: "magnet:?xt=urn:btih:abc" }), S, EXT_START) === true,
    "shouldCapture: magnet link captured");
ok(KokonaLogic.shouldCapture(null, S, EXT_START) === false, "shouldCapture: null item");
ok(KokonaLogic.shouldCapture(mkItem({}), null, EXT_START) === false, "shouldCapture: null settings");

// ===== mapDownloadResponse (background.js response mapping, extracted for testability) =====
// Expected Chinese messages are built from code points: this file must stay ASCII because
// cscript reads sources as ANSI and would mangle literal CJK.
var EXPECT_UNAUTHORIZED = String.fromCharCode(0x8fde, 0x63a5, 0x5bc6, 0x94a5, 0x9519, 0x8bef, 0xff0c,
    0x8bf7, 0x70b9, 0x51fb, 0x5de5, 0x5177, 0x680f, 0x56fe, 0x6807, 0x91cd, 0x65b0, 0x7c98, 0x8d34, 0x5bc6, 0x94a5);
var EXPECT_BUSY = String.fromCharCode(0x5ba2, 0x6237, 0x7aef, 0x6b63, 0x5fd9, 0xff08, 0x5e76, 0x53d1,
    0x8bf7, 0x6c42, 0x5df2, 0x6ee1, 0xff09, 0xff0c, 0x8bf7, 0x7a0d, 0x540e, 0x91cd, 0x8bd5);
var EXPECT_HTTP_PREFIX = String.fromCharCode(0x5ba2, 0x6237, 0x7aef, 0x8fd4, 0x56de, 0x9519, 0x8bef) + " HTTP ";
var M = KokonaLogic.mapDownloadResponse;

ok(M(200, { gid: "abc" }).ok === true, "map: 200 is a success");
ok(M(200, { gid: "abc" }).gid === "abc", "map: gid passed through");
ok(M(200, { gid: "abc" }).duplicate === false, "map: duplicate defaults to false");
ok(M(200, { gid: "abc", duplicate: true }).duplicate === true, "map: duplicate flag surfaced");
ok(M(200, { gid: "abc", confirm: true }).confirm === true, "map: magnet confirm flag surfaced");
ok(M(200, null).ok === true && M(200, null).gid === null, "map: missing body tolerated");
ok(M(204, null).ok === true, "map: 204 counts as success");
ok(M(299, null).ok === true, "map: any 2xx counts as success");
ok(M(401, null).ok === false, "map: 401 is a failure");
ok(M(401, null).code === "unauthorized", "map: 401 -> unauthorized");
ok(M(401, { message: "ignored" }).message === EXPECT_UNAUTHORIZED,
    "map: 401 keeps the built-in key hint instead of server wording");
ok(M(400, null).code === "rejected", "map: 400 -> rejected");
ok(M(400, { message: "bad url" }).message === "bad url", "map: 400 surfaces the server reason");
ok(M(503, null).code === "busy", "map: client gate 503 -> busy, not a generic http error");
ok(M(503, null).message === EXPECT_BUSY, "map: 503 falls back to the built-in busy hint");
ok(M(503, { message: "srv busy" }).message === "srv busy", "map: 503 prefers the server reason");
ok(M(503, { message: "" }).message === EXPECT_BUSY, "map: empty server message falls back to the hint");
ok(M(500, null).code === "http", "map: 500 -> http");
ok(M(404, null).code === "http", "map: 404 -> http");
ok(M(500, null).message === EXPECT_HTTP_PREFIX + "500", "map: generic failure mentions the status code");
ok(M(503, null).ok === false && typeof M(503, null).code === "string",
    "map: failures always carry a string code for err.code");

WScript.Echo("-----");

WScript.Echo("passed " + pass + " / " + (pass + fail));
WScript.Quit(fail === 0 ? 0 : 1);
