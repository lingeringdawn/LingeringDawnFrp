using System;
using Microsoft.Win32;

namespace LingeringDawnFrp.Services;

public sealed class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppKeyName = "LingeringDawnFrp";

    public void Set(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

        if (key is null)
        {
            throw new InvalidOperationException("无法打开启动项注册表键。") ;
        }

        if (enable)
        {
            var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("无法获取应用程序路径。");
            key.SetValue(AppKeyName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppKeyName, throwOnMissingValue: false);
        }
    }

    public bool Get()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (key is null)
        {
            return false;
        }

        return key.GetValue(AppKeyName) is string;
    }
}
