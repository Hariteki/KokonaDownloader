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

WScript.Echo("-----");
WScript.Echo("passed " + pass + " / " + (pass + fail));
WScript.Quit(fail === 0 ? 0 : 1);
