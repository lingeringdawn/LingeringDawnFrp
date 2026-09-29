using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LingeringDawnFrp.Helpers;

namespace LingeringDawnFrp.Services;

/// <summary>GitHub 上最新一次发布的信息。</summary>
public sealed class UpdateInfo
{
    public string Tag { get; init; } = string.Empty;

    public string LatestVersion { get; init; } = string.Empty;

    /// <summary>发布页地址（用户不懂直接下载时给这个）。</summary>
    public string ReleaseUrl { get; init; } = string.Empty;

    /// <summary>发布包直链（取第一个 .zip 附件）。</summary>
    public string AssetUrl { get; init; } = string.Empty;

    public bool HasUpdate { get; init; }
}

/// <summary>
/// 检测 GitHub 上是否有新版本。
/// 只读公开 API，失败一律静默降级（离线不应该影响隧道功能）。
/// </summary>
public static class UpdateChecker
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/lingeringdawn/LingeringDawnFrp/releases/latest";

    /// <summary>当前程序版本（取程序集信息版本，去掉 +hash 之类的后缀）。</summary>
    public static string CurrentVersion
    {
        get
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();

                var informational = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(informational))
                {
                    var plus = informational.IndexOf('+');
                    return (plus > 0 ? informational[..plus] : informational).Trim();
                }

                var version = assembly.GetName().Version;
                return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
            }
            catch
            {
                return "0.0.0";
            }
        }
    }

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LingeringDawnFrp");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var json = await http.GetStringAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString() ?? string.Empty
                : string.Empty;
            var releaseUrl = root.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString() ?? string.Empty
                : string.Empty;

            var assetUrl = string.Empty;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameElement)
                        ? nameElement.GetString() ?? string.Empty
                        : string.Empty;

                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    assetUrl = asset.TryGetProperty("browser_download_url", out var downloadElement)
                        ? downloadElement.GetString() ?? string.Empty
                        : string.Empty;
                    break;
                }
            }

            var latest = tag.TrimStart('v', 'V').Trim();

            return new UpdateInfo
            {
                Tag = tag,
                LatestVersion = latest,
                ReleaseUrl = releaseUrl,
                AssetUrl = assetUrl,
                HasUpdate = IsNewer(latest, CurrentVersion)
            };
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"检查更新失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>比较形如 1.2.3 的版本号；解析不出来时一律认为没有新版本（避免误报）。</summary>
    internal static bool IsNewer(string candidate, string current)
    {
        if (!TryParse(candidate, out var a) || !TryParse(current, out var b))
        {
            return false;
        }

        return a.CompareTo(b) > 0;
    }

    private static bool TryParse(string text, out Version version)
    {
        version = new Version(0, 0, 0);

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split('.');
        var numbers = new int[3];

        for (var i = 0; i < 3; i++)
        {
            if (i >= parts.Length)
            {
                break;
            }

            if (!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0)
            {
                return false;
            }
        }

        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }
}
