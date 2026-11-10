using System.Net;
using System.Text;

namespace BrimDeck.Core;

public sealed class StoSsoLoopback : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    public string CallbackUrl { get; }

    public StoSsoLoopback()
    {
        for (int i = 0; i < 20; i++)
        {
            int port = Random.Shared.Next(49152, 65535);
            var prefix = $"http://127.0.0.1:{port}/callback/";
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add(prefix);
            try
            {
                _listener.Start();
                CallbackUrl = prefix;
                return;
            }
            catch (HttpListenerException) { }
        }
        throw new InvalidOperationException(Loc.T("无法启动本地登录回调。", "Could not start the local sign-in callback."));
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
            var code = context.Request.QueryString["authorizationCode"]
                ?? context.Request.QueryString["code"];
            var html = "<!doctype html><meta charset=utf-8><title>BrimDeck</title>"
                + "<p>登录成功，可以关闭此页面。</p><p>Signed in. You can close this window.</p>";
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, linked.Token);
            context.Response.Close();
            if (!string.IsNullOrWhiteSpace(code)) return code.Trim();
        }
        throw new OperationCanceledException(linked.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        _listener.Close();
        _cts.Dispose();
    }
}
