using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace KokonaDownloader.Core.Engine;

/// <summary>
/// 下载前的"真实文件名"预解析：对目标 URL 发一次极小的 <c>Range: bytes=0-0</c> 请求（1 字节），
/// 跟完重定向后读取最终响应的 Content-Disposition，按 <see cref="ContentDispositionParser"/>
/// （比 aria2 宽容）解出真实名字，交给调用方作为 <c>out</c> 下发。
///
/// 专治"临时链接解析不到真实文件名"：URL 末段是 token/编号时，名字只存在于响应头里，
/// 而 aria2 一旦遇到非法 ext-value（最常见是 <c>filename*=UTF-8''…(1).pdf</c> 里没编码的圆括号）
/// 会连整条响应头一起放弃，于是落回 URL 末段。实测数据见测试报告 §16。
///
/// 安全边界：
///  - 只在**没有显式文件名**的 HTTP/HTTPS 任务上尝试；
///  - 带签名参数的链接（<c>X-Amz-Signature</c> 等）只有"URL 里没有扩展名可兜底"时才预解析，
///    避免给正常直链多加一次请求、也避免反复触碰一次性签名 URL；
///  - 任何失败（超时/405/连接失败/解析不出）都返回 null，调用方保持原样交给 aria2，最坏情况等于修复前。
/// </summary>
public sealed class RemoteNameResolver
{
    /// <summary>与 aria2 默认可变头一致，尽量让预解析看到的响应和真正下载时看到的一致。</summary>
    public const string AgentHeader = "aria2/1.37.0";

    /// <summary>只取 1 字节，几乎不产生流量；服务器不支持 Range 时会退化成整包响应头（仍只读头即断）。</summary>
    private const string ProbeRange = "bytes=0-0";

    /// <summary>疑似签名/鉴权参数：命中则收紧预解析条件（见 <see cref="ShouldPreresolve"/>）。</summary>
    private static readonly string[] SignatureHints =
    {
        "signature", "sign=", "x-amz-signature", "expires=", "token=", "auth=", "pwd=", "key=", "oid="
    };

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        // 有些 CDN 对 HEAD 返回 405，这里统一用带 Range 的 GET，读到头就丢弃正文
        UseCookies = false,
        ServerCertificateCustomValidationCallback = null
    }) { Timeout = TimeSpan.FromSeconds(6) };

    private readonly Action<string> _log;

    public RemoteNameResolver(Action<string>? log = null) => _log = log ?? (_ => { });

    /// <summary>是否值得为这个任务多发一次探测请求。</summary>
    public static bool ShouldPreresolve(string? url, string? explicitFileName)
    {
        if (!string.IsNullOrWhiteSpace(explicitFileName)) return false; // 用户手填/浏览器已解析：不猜
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        var hasExt = Path.GetExtension(Uri.UnescapeDataString(uri.AbsolutePath)).Length > 1;
        var query = uri.Query.ToLowerInvariant();
        var queryMentionsName = query.Contains("filename") || query.Contains("disposition") || query.Contains("fn=");
        if (!hasExt || queryMentionsName) return true;
        return !SignatureHints.Any(h => query.Contains(h, StringComparison.Ordinal));
    }

    /// <summary>
    /// 返回服务端声明的文件名；拿不到（或名字不可用）返回 null。
    /// </summary>
    public async Task<string?> ResolveAsync(string url, string? referer, IReadOnlyList<string>? headers,
                                            CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, 0);
            req.Headers.TryAddWithoutValidation("User-Agent", AgentHeader);
            if (!string.IsNullOrWhiteSpace(referer)) req.Headers.TryAddWithoutValidation("Referer", referer);
            if (headers != null)
            {
                foreach (var line in headers)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var i = line.IndexOf(':');
                    if (i <= 0) continue;
                    var hname = line.Substring(0, i).Trim();
                    // 这几个必须由我们自己控制，不能让调用方覆盖
                    if (hname.Equals("Range", StringComparison.OrdinalIgnoreCase) ||
                        hname.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ||
                        hname.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                        hname.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                    req.Headers.TryAddWithoutValidation(hname, line.Substring(i + 1).Trim());
                }
            }

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            // 只认成功响应：404/403/500 的错误页常常也挂着 Content-Disposition（下载站的"文件不存在.html"），
            // 拿它当落盘名会把一个错误响应的名字钉到任务上。状态不对就什么也不做，交给 aria2 处理。
            if (resp.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent)) return null;
            var header = ReadContentDisposition(resp);
            if (string.IsNullOrWhiteSpace(header)) return null;
            var name = ContentDispositionParser.ParseFileName(header);
            if (name != null)
                _log($"预解析文件名：{name}（来自 {resp.StatusCode:D} 响应的 Content-Disposition）");
            else
                _log($"预解析未取到文件名（Content-Disposition={TrimForLog(header!)}），交由 aria2 解析");
            return name;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // 探测失败不影响下载：aria2 会自己再请求一次并按它的方式解析
            _log($"预解析文件名失败（忽略，交给 aria2）: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Content-Disposition 在 .NET 里属于**实体头**：它出现在 <c>resp.Content.Headers</c> 而不是
    /// <c>resp.Headers</c>，只查后者会永远查不到（本方法就是被集成测试逼出来的：修前两个用例全落回 URL 末段）。
    /// 有些服务器会把它写在错误的位置，因此两边都查，并且**用原始字符串**读——
    /// Typed 的 <c>ContentDisposition</c> 属性会被非法 ext-value（没编码的圆括号）卡住，正是我们要宽容的情况。
    /// </summary>
    private static string? ReadContentDisposition(HttpResponseMessage resp)
    {
        foreach (var bag in new System.Net.Http.Headers.HttpHeaders[] { resp.Content.Headers, resp.Headers })
        {
            if (bag.TryGetValues("Content-Disposition", out var values))
            {
                var v = values.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        return null;
    }

    private static string TrimForLog(string s) =>
        s.Length <= 120 ? s : string.Concat(s.AsSpan(0, 120), "…");
}
