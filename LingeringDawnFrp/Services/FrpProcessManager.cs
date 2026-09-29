using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using LingeringDawnFrp.Helpers;

namespace LingeringDawnFrp.Services;

public sealed class FrpProcessManager : IDisposable
{
    private readonly object _sync = new();
    private Process? _process;
    private string? _lastConfigPath;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public event Action<string>? OutputDataReceived;
    public event Action? ProcessExited;

    public void Start(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            throw new ArgumentException("configPath 不能为空。", nameof(configPath));
        }

        lock (_sync)
        {
            if (_process is { HasExited: false })
            {
                Logger.Log(LogLevel.Warning, "frpc 已在运行，忽略重复启动。");
                return;
            }

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var exePath = Path.Combine(baseDir, "frpc.exe");
            if (!File.Exists(exePath))
            {
                throw new FileNotFoundException("未找到 frpc.exe，请将其放到程序同目录。", exePath);
            }

            _lastConfigPath = configPath;
            var configArgument = Path.GetFileName(configPath);

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"-c {configArgument}",
                    WorkingDirectory = baseDir,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };

            _process.OutputDataReceived += OnOutputDataReceived;
            _process.ErrorDataReceived += OnErrorDataReceived;
            _process.Exited += OnProcessExited;

            if (!_process.Start())
            {
                throw new InvalidOperationException("frpc 进程启动失败。");
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            Logger.Log(LogLevel.Info, "frpc 进程启动成功。");
        }
    }

    public void Stop()
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            process.OutputDataReceived -= OnOutputDataReceived;
            process.ErrorDataReceived -= OnErrorDataReceived;
            process.Exited -= OnProcessExited;

            if (process.HasExited)
            {
                return;
            }

            try
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    process.CloseMainWindow();
                    if (process.WaitForExit(1500))
                    {
                        return;
                    }
                }
            }
            catch
            {
                // 无主窗口的控制台程序直接进入 Kill 流程
            }

            process.Kill(true);
            process.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, $"停止 frpc 进程时发生异常: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Restart()
    {
        string? configPath;

        lock (_sync)
        {
            configPath = _lastConfigPath;
        }

        if (string.IsNullOrWhiteSpace(configPath))
        {
            throw new InvalidOperationException("尚未启动过 frpc，无法重启。");
        }

        Stop();
        Start(configPath);
    }

    /// <summary>
    /// 读取 frpc 版本（执行 frpc.exe --version）。读不到时返回 null。
    ///
    /// 存在的意义：服务端是 frps 0.29.0，实测只有 0.28.x / 0.29.x 的客户端能登录成功，
    /// 现代版本（如 0.52.3）会直接以 "login to server failed: session shutdown" 失败 ——
    /// 这个报错完全看不出是版本问题，所以要在启动前主动提示。
    /// </summary>
    public string? GetClientVersion()
    {
        try
        {
            var exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "frpc.exe");
            if (!File.Exists(exePath))
            {
                return null;
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--version",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // 忽略
                }

                return null;
            }

            var match = System.Text.RegularExpressions.Regex.Match(output, @"\d+\.\d+\.\d+");
            return match.Success ? match.Value : null;
        }
        catch
        {
            return null;
        }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            OutputDataReceived?.Invoke(e.Data);
        }
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            OutputDataReceived?.Invoke($"[ERR] {e.Data}");
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        Process? processToDispose = null;
        lock (_sync)
        {
            if (sender is Process process && ReferenceEquals(_process, process))
            {
                processToDispose = _process;
                _process = null;
            }
        }

        if (processToDispose is not null)
        {
            try
            {
                processToDispose.OutputDataReceived -= OnOutputDataReceived;
                processToDispose.ErrorDataReceived -= OnErrorDataReceived;
                processToDispose.Exited -= OnProcessExited;
                processToDispose.Dispose();
            }
            catch
            {
                // 忽略清理异常
            }
        }

        Logger.Log(LogLevel.Info, "frpc 进程已退出。");
        ProcessExited?.Invoke();
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
