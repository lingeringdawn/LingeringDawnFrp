using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LingeringDawnFrp.Helpers;

namespace LingeringDawnFrp.Services;

/// <summary>取回访问密钥的结果。</summary>
public sealed class PanelAuthResult
{
    public bool Success { get; private init; }

    public string Token { get; private init; } = string.Empty;

    public string Username { get; private init; } = string.Empty;

    public string Message { get; private init; } = string.Empty;

    public static PanelAuthResult Ok(string token, string username) => new()
    {
        Success = true,
        Token = token,
        Username = username,
        Message = "已获取访问密钥"
    };

    public static PanelAuthResult Fail(string message) => new()
    {
        Success = false,
        Message = message
    };
}

/// <summary>
/// 通过系统默认浏览器登录面板，并从本机回环回调取回 frp 访问密钥。
///
/// 为什么必须是这个方案：客户端无法读取浏览器的登录态 —— Chrome / Edge 的 Cookie
/// 由 DPAPI + AES-GCM 加密，桌面程序要解密它才算拿到会话，那种做法既脆弱又形同木马。
/// 因此改为「面板侧校验浏览器会话 → 回环回调把密钥交给客户端」。
///
/// 实现细节：用 TcpListener 而不是 HttpListener。HttpListener 走 http.sys，
/// 除非有管理员权限或预先注册 URL ACL，否则绑定 127.0.0.1 也会失败；
/// 直接监听回环端口没有这个限制，而且这里只需要处理一个 GET 请求。
/// </summary>
public sealed class PanelAuthService
{
    private const string AuthPageUrl = "https://frp.lingeringdawn.cloud/index.php?page=app_auth";

    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 打开浏览器并等待授权回调。
    /// </summary>
    /// <param name="openBrowser">
    /// 打开浏览器的方式，便于测试注入；为空时用系统默认浏览器。
    /// </param>
    public async Task<PanelAuthResult> AcquireTokenAsync(
        Action<string>? openBrowser = null,
        CancellationToken cancellationToken = default)
    {
        openBrowser ??= OpenInSystemBrowser;

        // 端口传 0 让系统分配一个空闲端口，避免与其它程序冲突
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var state = Guid.NewGuid().ToString("N");
            var redirect = $"http://127.0.0.1:{port}/";
            var url = $"{AuthPageUrl}&redirect={Uri.EscapeDataString(redirect)}&state={Uri.EscapeDataString(state)}";

            Logger.Log(LogLevel.Info, $"已在本机监听 127.0.0.1:{port}，等待浏览器授权回调。");
            openBrowser(url);

            var result = await WaitForCallbackAsync(listener, state, cancellationToken).ConfigureAwait(false);

            return result ?? PanelAuthResult.Fail(
                "等待超时（3 分钟）。若浏览器里尚未登录面板，请先登录，然后重新点击「浏览器获取密钥」。");
        }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch
            {
                // 忽略
            }
        }
    }

    private static async Task<PanelAuthResult?> WaitForCallbackAsync(
        TcpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + CallbackTimeout;

        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            TcpClient client;

            try
            {
                var acceptTask = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
                var finished = await Task.WhenAny(acceptTask, Task.Delay(remaining, CancellationToken.None))
                    .ConfigureAwait(false);

                if (finished != acceptTask)
                {
                    break;
                }

                client = await acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, $"回调监听异常：{ex.Message}");
                break;
            }

            using (client)
            {
                var target = await ReadRequestTargetAsync(client).ConfigureAwait(false);
                var query = ParseQueryString(target);

                if (!query.TryGetValue("state", out var receivedState))
                {
                    // 浏览器可能顺带请求 /favicon.ico 之类，忽略并继续等待
                    await WriteResponseAsync(client, 404, "已忽略该请求", "这不是面板的授权回调。")
                        .ConfigureAwait(false);
                    continue;
                }

                if (!string.Equals(receivedState, expectedState, StringComparison.Ordinal))
                {
                    // 防止本机其它进程伪造回调
                    await WriteResponseAsync(client, 400, "校验失败", "回调校验值不匹配，已忽略本次结果。")
                        .ConfigureAwait(false);
                    return PanelAuthResult.Fail("回调校验值不匹配：响应不是来自本次授权请求，已拒绝。");
                }

                query.TryGetValue("token", out var token);
                query.TryGetValue("username", out var username);
                token = (token ?? string.Empty).Trim();

                if (token.Length == 0)
                {
                    await WriteResponseAsync(client, 200, "未取到密钥", "面板未返回访问密钥，请确认该账号已分配 frp 密钥。")
                        .ConfigureAwait(false);
                    return PanelAuthResult.Fail("面板未返回访问密钥（请确认账号已分配密钥）。");
                }

                await WriteResponseAsync(
                        client,
                        200,
                        "已获取访问密钥",
                        $"账号 {username} 的访问密钥已发送到客户端，可以关闭本页面。")
                    .ConfigureAwait(false);

                return PanelAuthResult.Ok(token, username ?? string.Empty);
            }
        }

        return null;
    }

    /// <summary>读取请求行并取出请求目标（形如 /?token=..&amp;state=..）。</summary>
    private static async Task<string> ReadRequestTargetAsync(TcpClient client)
    {
        // 这里千万不能 using 掉流：NetworkStream 被释放时会连带关闭底层 socket，
        // 紧接着写响应就会抛 "The operation is not allowed on non-connected sockets"，
        // 浏览器拿不到结果页，密钥也就回不到客户端。流的生命周期交给外层 using(client)。
        var stream = client.GetStream();
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer).ConfigureAwait(false);

        if (read <= 0)
        {
            return string.Empty;
        }

        var text = Encoding.ASCII.GetString(buffer, 0, read);
        var firstLineEnd = text.IndexOf('\n');
        var firstLine = firstLineEnd >= 0 ? text[..firstLineEnd] : text;
        var parts = firstLine.Split(' ');

        return parts.Length >= 2 ? parts[1] : string.Empty;
    }

    private static Dictionary<string, string> ParseQueryString(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        var index = target.IndexOf('?');
        if (index < 0 || index == target.Length - 1)
        {
            return result;
        }

        foreach (var pair in target[(index + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separator]);
            var value = Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }

    private static async Task WriteResponseAsync(TcpClient client, int status, string title, string text)
    {
        var html =
            "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<title>" + WebUtility.HtmlEncode(title) + "</title><style>" +
            "body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;" +
            "background:#f5f3fb;font-family:'Microsoft YaHei UI',system-ui,sans-serif;color:#1e1a2b}" +
            ".card{background:#fff;border:1px solid #e7e1f3;border-radius:16px;padding:32px 36px;max-width:460px;" +
            "box-shadow:0 18px 40px rgba(59,42,99,.08)}" +
            "h1{margin:0 0 12px;font-size:19px}p{margin:0;line-height:1.7;color:#6e6785;font-size:14px}" +
            "</style></head><body><div class=\"card\"><h1>" + WebUtility.HtmlEncode(title) + "</h1><p>" +
            WebUtility.HtmlEncode(text) + "</p></div></body></html>";

        var body = Encoding.UTF8.GetBytes(html);
        var reason = status == 200 ? "OK" : "Error";
        var header = $"HTTP/1.1 {status} {reason}\r\n" +
                     "Content-Type: text/html; charset=utf-8\r\n" +
                     $"Content-Length: {body.Length}\r\n" +
                     "Connection: close\r\n\r\n";

        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header)).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static void OpenInSystemBrowser(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
