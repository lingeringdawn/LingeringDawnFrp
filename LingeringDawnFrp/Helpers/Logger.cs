using System;
using System.IO;
using System.Text;

namespace LingeringDawnFrp.Helpers;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// 日志同时送到两个地方：界面上的日志框（通过 <see cref="LogAdded"/> 事件）
/// 和磁盘文件。原实现只发事件不落盘，导致 App.xaml.cs 的崩溃提示里那句
/// “已记录日志”其实没有任何文件可查，事后完全无法排查。
/// </summary>
public static class Logger
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private const string LogFileName = "app.log";

    private static readonly object LockObj = new();

    private static string? _logFilePath;
    private static bool _fileDisabled;

    public static event Action<LogLevel, string>? LogAdded;

    /// <summary>日志文件路径；尚未确定或无法写入时为 null。</summary>
    public static string? LogFilePath
    {
        get
        {
            lock (LockObj)
            {
                return _logFilePath;
            }
        }
    }

    public static void Log(LogLevel level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";

        WriteToFile(line);

        try
        {
            LogAdded?.Invoke(level, line);
        }
        catch
        {
            // 界面订阅者异常不能影响主流程
        }
    }

    private static void WriteToFile(string line)
    {
        lock (LockObj)
        {
            if (_fileDisabled)
            {
                return;
            }

            try
            {
                _logFilePath ??= ResolveLogFilePath();
                if (_logFilePath is null)
                {
                    _fileDisabled = true;
                    return;
                }

                RotateIfNeeded(_logFilePath);
                File.AppendAllText(_logFilePath, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
                // 写日志失败绝不能让程序崩掉，静默降级为仅界面输出
                _fileDisabled = true;
            }
        }
    }

    /// <summary>
    /// 优先写在程序目录（便携用法）；目录不可写时（例如装在 Program Files）
    /// 退回到 %LOCALAPPDATA%\LingeringDawnFrp，保证日志始终有地方落。
    /// </summary>
    private static string? ResolveLogFilePath()
    {
        var candidates = new[]
        {
            AppDomain.CurrentDomain.BaseDirectory,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LingeringDawnFrp")
        };

        foreach (var directory in candidates)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, LogFileName);

                // 探针写入，确认目录真的可写
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    stream.WriteByte(0x20);
                }

                return path;
            }
            catch
            {
                // 试下一个候选目录
            }
        }

        return null;
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxLogBytes)
            {
                return;
            }

            var backup = path + ".1";
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Move(path, backup);
        }
        catch
        {
            // 轮转失败则继续追加，不影响功能
        }
    }
}
