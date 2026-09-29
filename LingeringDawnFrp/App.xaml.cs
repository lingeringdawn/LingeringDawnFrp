using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LingeringDawnFrp.Helpers;
using WpfApplication = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;

namespace LingeringDawnFrp;

public partial class App : WpfApplication
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;

        Logger.Log(LogLevel.Info, "应用启动完成。");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Log(LogLevel.Error, $"UI 线程未处理异常: {e.Exception}");
        WpfMessageBox.Show(BuildCrashMessage(), "LingeringDawn Frp", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Log(LogLevel.Error, $"任务未观察异常: {e.Exception}");
        e.SetObserved();
    }

    private void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // 域级异常可能发生在任意线程，直接弹窗会抛 InvalidOperationException，
        // 因此改为投递到 Dispatcher；UI 已不可用时至少保证日志已经落盘。
        Logger.Log(LogLevel.Error, $"域级未处理异常: {e.ExceptionObject}");

        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
                WpfMessageBox.Show(BuildCrashMessage(), "LingeringDawn Frp", MessageBoxButton.OK, MessageBoxImage.Error)));
        }
        catch
        {
            // 进程正在退出，忽略
        }
    }

    private static string BuildCrashMessage()
    {
        var logPath = Logger.LogFilePath;
        return string.IsNullOrWhiteSpace(logPath)
            ? "程序遇到未处理错误，已记录日志。"
            : $"程序遇到未处理错误，已记录日志：{Environment.NewLine}{logPath}";
    }
}
