using System;
using System.IO;
using System.Text;

namespace LingeringDawnFrp.Services;

public sealed class ConfigManager
{
    /// <summary>可写的数据目录（frpc.ini、user.token、node.id 都放这里）。</summary>
    public string DataDirectory { get; }

    public string ConfigPath { get; }

    public ConfigManager(string? dataDirectory = null)
    {
        DataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? ResolveWritableDirectory()
            : dataDirectory;

        ConfigPath = Path.Combine(DataDirectory, "frpc.ini");
    }

    /// <summary>
    /// 优先用程序目录（便携用法）；不可写时（例如装在 Program Files、
    /// 或者放在只读共享盘上）退回到 %LOCALAPPDATA%\LingeringDawnFrp。
    /// 原实现无条件写程序目录，装在 Program Files 下会直接失败。
    /// </summary>
    public static string ResolveWritableDirectory()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        if (IsWritable(baseDir))
        {
            return baseDir;
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LingeringDawnFrp");

        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static bool IsWritable(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        var probe = Path.Combine(directory, ".write-test");

        try
        {
            using (var stream = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0x20);
            }

            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void WriteConfig(string configContent)
    {
        if (configContent is null)
        {
            throw new ArgumentNullException(nameof(configContent));
        }

        var directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 先写临时文件再替换，避免 frpc 正读着配置时被截断
        var tempPath = ConfigPath + ".tmp";
        File.WriteAllText(tempPath, configContent, new UTF8Encoding(false));

        if (File.Exists(ConfigPath))
        {
            File.Replace(tempPath, ConfigPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, ConfigPath);
        }
    }

    public string ReadConfig()
    {
        return File.ReadAllText(ConfigPath, Encoding.UTF8);
    }

    public bool ConfigExists()
    {
        return File.Exists(ConfigPath);
    }
}
