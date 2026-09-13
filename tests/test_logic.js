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

WScript.Echo("-----");
WScript.Echo("passed " + pass + " / " + (pass + fail));
WScript.Quit(fail === 0 ? 0 : 1);
