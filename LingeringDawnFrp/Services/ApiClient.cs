using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LingeringDawnFrp.Helpers;

namespace LingeringDawnFrp.Services;

/// <summary>接口调用失败。<see cref="Retryable"/> 为 false 时表示重试没有意义（例如 token 无效）。</summary>
public sealed class ApiException : Exception
{
    public ApiException(string message, bool retryable, HttpStatusCode? statusCode = null, string? responseBody = null)
        : base(message)
    {
        Retryable = retryable;
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public bool Retryable { get; }

    public HttpStatusCode? StatusCode { get; }

    public string? ResponseBody { get; }
}

public sealed class ApiClient
{
    private const string Endpoint = "https://frp.lingeringdawn.cloud/api/index.php";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// 拉取指定节点的 frpc 配置（面板返回的是 INI 文本）。
    /// </summary>
    /// <exception cref="ApiException">接口返回错误、内容为空或内容不是有效配置时抛出。</exception>
    public async Task<string> GetConfigAsync(string userToken, int nodeId)
    {
        if (string.IsNullOrWhiteSpace(userToken))
        {
            throw new ArgumentException("userToken 不能为空。", nameof(userToken));
        }

        var url = $"{Endpoint}?action=getconf&token={Uri.EscapeDataString(userToken)}&node={nodeId}";
        ApiException? lastError = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await HttpClient.SendAsync(request, CancellationToken.None);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    // 面板在出错时会返回 {"status":404,"message":"User or node not found"} 这类 JSON，
                    // 原实现把这个消息丢掉了，只报一个状态码，用户根本不知道为什么失败。
                    var detail = ExtractServerMessage(body);
                    var message = detail is null
                        ? $"接口返回状态码 {(int)response.StatusCode} ({response.StatusCode})"
                        : $"{detail}（HTTP {(int)response.StatusCode}）";

                    // 4xx 是明确的请求问题（token 错、节点不存在、无权限），重试三次毫无意义
                    var retryable = (int)response.StatusCode >= 500;

                    throw new ApiException(message, retryable, response.StatusCode, body);
                }

                if (string.IsNullOrWhiteSpace(body))
                {
                    throw new ApiException("接口返回的配置内容为空，请确认访问密钥与节点是否正确。", retryable: true);
                }

                if (!LooksLikeFrpcConfig(body))
                {
                    // 典型场景：面板吐了 HTML 错误页或 JSON 报错，却被当成配置写进 frpc.ini，
                    // 之后 frpc 只会给出难以理解的解析错误。这里提前拦住并给出原文片段。
                    throw new ApiException(
                        "接口返回的内容不是有效的 frpc 配置（缺少 [common] 段）。" + Environment.NewLine +
                        "返回内容片段：" + Preview(body),
                        retryable: false,
                        response.StatusCode,
                        body);
                }

                Logger.Log(LogLevel.Info, $"获取配置成功，节点 ID: {nodeId}，共 {body.Length} 字符。");
                return body;
            }
            catch (ApiException ex)
            {
                lastError = ex;
                Logger.Log(LogLevel.Warning, $"获取配置失败（第 {attempt}/3 次）：{ex.Message}");

                if (!ex.Retryable)
                {
                    throw;
                }
            }
            catch (Exception ex)
            {
                lastError = new ApiException(ex.Message, retryable: true);
                Logger.Log(LogLevel.Warning, $"获取配置失败（第 {attempt}/3 次）：{ex.Message}");
            }

            if (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt));
            }
        }

        throw new InvalidOperationException("获取配置失败，已达到最大重试次数。", lastError);
    }

    /// <summary>从面板的错误响应里取出 message 字段；取不到则返回 null。</summary>
    private static string? ExtractServerMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var trimmed = body.Trim();

        try
        {
            if (trimmed.StartsWith('{'))
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    var text = message.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 不是 JSON，按纯文本处理
        }

        // 非 JSON（例如 HTML 错误页）时，给一小段纯文本，避免把整页 HTML 塞进日志
        var plain = trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed;
        return plain.Contains('<') ? null : plain;
    }

    private static bool LooksLikeFrpcConfig(string body)
    {
        return body.Contains("[common]", StringComparison.OrdinalIgnoreCase);
    }

    private static string Preview(string body)
    {
        var flat = body.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length > 200 ? flat[..200] + "…" : flat;
    }
}
