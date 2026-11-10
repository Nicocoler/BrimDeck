using System.Collections.Specialized;
using System.Net;
using System.Text;

namespace BrimDeck.Core;

public sealed class StoSsoLoopback : IDisposable
{
    // Fixed port so SSO admins can register one returnUrl; ephemeral ports never match a registered callback.
    public const int DefaultPort = 38473;
    // STO user-center substitutes {0} with the temporary authorization code (same pattern as SITE's LoginByCode?Code={0}).
    public static string RegisteredCallback(int port = DefaultPort) => $"http://127.0.0.1:{port}/callback/?Code={{0}}";

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    // Listener prefix (no query). Use ReturnUrl when opening SSO / registering the subsystem.
    public string CallbackUrl { get; }
    public string ReturnUrl { get; }

    public StoSsoLoopback(int? port = null)
    {
        var errors = new List<Exception>();
        foreach (int candidate in port is { } fixedPort ? [fixedPort] : PrefixedPorts())
        {
            var prefix = $"http://127.0.0.1:{candidate}/callback/";
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add(prefix);
                _listener.Start();
                CallbackUrl = prefix;
                ReturnUrl = RegisteredCallback(candidate);
                return;
            }
            catch (Exception ex) when (ex is HttpListenerException or ArgumentException)
            {
                errors.Add(ex);
                try { _listener.Stop(); } catch { /* retry next port */ }
            }
        }
        throw new InvalidOperationException(Loc.T(
            $"无法在本机启动登录回调（端口 {DefaultPort}）。请关闭占用该端口的程序后重试。",
            $"Could not start the sign-in callback on port {DefaultPort}. Free that port and try again."),
            errors.LastOrDefault());
    }

    private static IEnumerable<int> PrefixedPorts()
    {
        yield return DefaultPort;
        for (int i = 0; i < 12; i++) yield return Random.Shared.Next(49152, 65535);
    }

    public async Task<string> WaitForCodeAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var deadline = Task.Delay(timeout, linked.Token);
        while (!linked.IsCancellationRequested)
        {
            var contextTask = _listener.GetContextAsync();
            var done = await Task.WhenAny(contextTask, deadline);
            if (done != contextTask)
                throw new TimeoutException(Loc.T("登录超时，请重试。", "Sign-in timed out. Try again."));
            var context = await contextTask;
            var path = context.Request.Url?.AbsolutePath ?? "";
            if (path.Contains("favicon", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                continue;
            }

            var code = ReadAuthorizationCode(context.Request);
            if (!string.IsNullOrWhiteSpace(code))
            {
                await WriteHtmlAsync(context.Response, 200,
                    "<p>登录成功，可以关闭此页面。</p><p>Signed in. You can close this window.</p>", linked.Token);
                return code.Trim();
            }

            var raw = context.Request.Url?.ToString() ?? context.Request.RawUrl ?? "";
            var keys = DescribeQuery(context.Request.QueryString);
            await WriteHtmlAsync(context.Response, 400,
                "<p>未收到授权码（authorizationCode）。</p>"
                + "<p>No authorization code was returned.</p>"
                + "<p style=\"font:12px Consolas,monospace;word-break:break-all\">" + HtmlEncode(raw) + "</p>"
                + "<p style=\"font:12px Consolas,monospace\">" + HtmlEncode(keys) + "</p>"
                + "<p>请确认单点已为 STO-BrimDeck 登记回调：<code>" + HtmlEncode(RegisteredCallback()) + "</code></p>",
                linked.Token);
            throw new InvalidDataException(Loc.T(
                "单点回调未带 Code。请为 STO-BrimDeck 登记回调地址 " + RegisteredCallback() + " 。收到：" + raw,
                "SSO callback had no Code. Register " + RegisteredCallback() + " for STO-BrimDeck. Got: " + raw));
        }
        throw new OperationCanceledException(linked.Token);
    }

    internal static string? ReadAuthorizationCode(HttpListenerRequest request)
    {
        var query = request.QueryString;
        // STO templates use Code={0}; OAuth-style flows may use authorizationCode / code.
        foreach (var key in new[] { "Code", "code", "authorizationCode", "AuthorizationCode", "authCode", "token", "ticket" })
        {
            var value = query[key];
            if (!string.IsNullOrWhiteSpace(value) && value != "{0}") return value;
        }
        // Some gateways append "callback/&Code=" when returnUrl had no "?".
        var raw = request.Url?.Query ?? request.RawUrl ?? "";
        foreach (var key in new[] { "Code", "code", "authorizationCode", "authCode" })
        {
            var marker = key + "=";
            int at = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var rest = raw[(at + marker.Length)..];
            int end = rest.IndexOfAny(['&', '#']);
            var value = Uri.UnescapeDataString(end < 0 ? rest : rest[..end]);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static string DescribeQuery(NameValueCollection query)
    {
        if (query.Count == 0) return "(no query keys)";
        var parts = new List<string>();
        foreach (string? key in query.AllKeys)
            parts.Add((key ?? "(null)") + "=" + (query[key]?.Length > 0 ? $"({query[key]!.Length} chars)" : ""));
        return string.Join(", ", parts);
    }

    private static string HtmlEncode(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    private static async Task WriteHtmlAsync(HttpListenerResponse response, int status, string body, CancellationToken ct)
    {
        var html = "<!doctype html><meta charset=utf-8><title>BrimDeck</title>" + body;
        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = status;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct);
        response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        _listener.Close();
        _cts.Dispose();
    }
}
