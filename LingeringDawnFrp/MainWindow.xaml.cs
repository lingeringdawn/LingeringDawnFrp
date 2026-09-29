using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LingeringDawnFrp.Helpers;
using LingeringDawnFrp.Services;
using WinForms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfMessageBox = System.Windows.MessageBox;

namespace LingeringDawnFrp;

public partial class MainWindow : Window
{
    private const string PanelUrl = "https://frp.lingeringdawn.cloud/";

    /// <summary>日志框最多保留的行数，防止长时间运行后无限堆积。</summary>
    private const int MaxLogLines = 800;

    private const string AccessKeyFileName = "user.token";
    private const string NodeIdFileName = "node.id";
    private const string DefaultNodeIdText = "1";
    private const int MaxReconnectAttempts = 5;

    // 与面板保持一致：api/index.php 用 Regex::isLetter(token) 校验字符集，
    // checktoken 又要求长度恰为 16；而 token 由 md5(...) 前 16 位生成，
    // 因此实际固定是 16 位十六进制。原来写成 {16,64} 会放过面板根本不接受的输入。
    private static readonly Regex TokenPattern = new("^[a-fA-F0-9]{16}$", RegexOptions.Compiled);

    private readonly ApiClient _apiClient;
    private readonly ConfigManager _configManager;
    private readonly FrpProcessManager _frpProcessManager;
    private readonly StartupManager _startupManager;
    private readonly PanelAuthService _panelAuthService;
    private readonly string _tokenFilePath;
    private readonly string _nodeIdFilePath;

    private bool _isExitRequested;
    private bool _isSyncingTokenInput;
    private bool _trayTipShown;
    private bool _isInitializingAutoStart;
    private bool _isInitializingAutoReconnect;

    /// <summary>
    /// 界面控件尚未创建时收到的日志先缓存在这里，窗口就绪后再补显。
    /// 否则早期日志（或 XAML 解析期间触发的事件）会因为访问不到 LogBox 而抛异常。
    /// </summary>
    private readonly object _pendingLogLock = new();
    private readonly List<(string Line, LogLevel Level)> _pendingLogEntries = new();

    /// <summary>是否启用断线自动重连（由界面复选框控制）。</summary>
    private bool _isAutoReconnectEnabled;

    private int _reconnectAttempts;

    /// <summary>上次成功启动时使用的访问密钥，供自动重连复用。</summary>
    private string? _lastStartedAccessKey;

    private string? _lastSavedAccessKey;
    private int? _lastSavedNodeId;
    private WinForms.NotifyIcon? _notifyIcon;

    private TunnelState _state = TunnelState.Stopped;

    private enum TunnelState
    {
        Stopped,
        Starting,
        Running,
        Error
    }

    public MainWindow()
    {
        InitializeComponent();

        _apiClient = new ApiClient();
        _configManager = new ConfigManager();
        _frpProcessManager = new FrpProcessManager();
        _startupManager = new StartupManager();
        _panelAuthService = new PanelAuthService();
        // 所有运行时文件都放在可写目录里（程序目录不可写时自动退回 %LOCALAPPDATA%）
        _tokenFilePath = Path.Combine(_configManager.DataDirectory, AccessKeyFileName);
        _nodeIdFilePath = Path.Combine(_configManager.DataDirectory, NodeIdFileName);

        Loaded += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;

        _frpProcessManager.OutputDataReceived += line => AppendRawLog(LogLevel.Debug, line);
        _frpProcessManager.ProcessExited += () => Dispatcher.Invoke(OnFrpcExited);
        Logger.LogAdded += AppendRawLog;

        // 断线自动重连默认开启。不能把 IsChecked="True" 写在 XAML 上：
        // 那会在 InitializeComponent() 解析到该控件时就触发 Checked 事件，
        // 而此时 LogBox 等控件还未创建，写日志会抛 NullReferenceException。
        _isInitializingAutoReconnect = true;
        AutoReconnectCheckBox.IsChecked = true;
        _isInitializingAutoReconnect = false;
        _isAutoReconnectEnabled = true;

        FlushPendingLogs();

        UpdateState(TunnelState.Stopped);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeTrayIcon();

        try
        {
            // 这里的赋值会触发 Checked/Unchecked 事件；若不屏蔽，每次启动都会
            // 反过来重写一次注册表并打一条“已启用开机自启”的假日志。
            _isInitializingAutoStart = true;
            AutoStartCheckBox.IsChecked = _startupManager.Get();
            _isInitializingAutoStart = false;

            _isAutoReconnectEnabled = AutoReconnectCheckBox.IsChecked == true;

            LoadManualAccessKey();
            LoadManualNodeId();
            SetTokenVisibility(false);

            AppendLog($"数据目录：{_configManager.DataDirectory}");

            if (!string.IsNullOrWhiteSpace(Logger.LogFilePath))
            {
                LogPathText.Text = Logger.LogFilePath;
                AppendLog($"日志文件：{Logger.LogFilePath}");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"读取本地设置失败: {ex.Message}", LogLevel.Warning);
        }

        AppendLog("就绪。请先在浏览器中打开面板获取访问密钥。", LogLevel.Info);

        // 启动时静默检查更新：失败只写日志，不影响任何功能
        VersionText.Text = $"v{UpdateChecker.CurrentVersion}";
        _ = CheckUpdateAsync(silent: true);
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            await CheckUpdateAsync(silent: false);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 检查 GitHub 上是否有新版本。silent 为 true 时不弹窗（用于启动时的自动检查）。
    /// </summary>
    private async Task CheckUpdateAsync(bool silent)
    {
        var current = UpdateChecker.CurrentVersion;

        if (!silent)
        {
            AppendLog("正在检查更新…", LogLevel.Info);
        }

        var info = await UpdateChecker.CheckAsync();

        if (info is null)
        {
            VersionText.Text = $"v{current}";
            if (!silent)
            {
                Report("无法连接 GitHub 获取版本信息，请检查网络后重试。", "检查更新", MessageBoxImage.Warning, false);
            }

            return;
        }

        if (info.HasUpdate)
        {
            VersionText.Text = $"v{current} → {info.Tag}";
            AppendLog($"发现新版本 {info.Tag}（当前 v{current}）。发布页：{info.ReleaseUrl}", LogLevel.Info);

            if (!silent)
            {
                var choice = WpfMessageBox.Show(
                    $"发现新版本 {info.Tag}（当前 v{current}）。{Environment.NewLine}是否打开下载页面？",
                    "检查更新",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (choice == MessageBoxResult.Yes)
                {
                    OpenExternalUrl(string.IsNullOrWhiteSpace(info.AssetUrl) ? info.ReleaseUrl : info.AssetUrl);
                }
            }

            return;
        }

        VersionText.Text = $"v{current} · 已是最新";
        AppendLog($"当前已是最新版本（v{current}）。", LogLevel.Info);

        if (!silent)
        {
            Report($"当前已是最新版本（v{current}）。", "检查更新", MessageBoxImage.Information, false);
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        await StartTunnelAsync();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopTunnelAsync();
    }

    private void OpenPanelButton_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalUrl(PanelUrl);
    }



    /// <summary>
    /// 在浏览器里完成面板登录后，通过本机回环回调自动取回访问密钥。
    /// </summary>
    private async void BrowserTokenButton_Click(object sender, RoutedEventArgs e)
    {
        BrowserTokenButton.IsEnabled = false;

        try
        {
            AppendLog("正在打开浏览器获取访问密钥…若尚未登录面板，请先登录后再点一次。", LogLevel.Info);

            var result = await _panelAuthService.AcquireTokenAsync();

            if (!result.Success)
            {
                AppendLog(result.Message, LogLevel.Warning);
                Report(result.Message, "获取密钥失败", MessageBoxImage.Warning, isAutoReconnect: false);
                return;
            }

            if (!TokenPattern.IsMatch(result.Token))
            {
                AppendLog($"面板返回的密钥格式不正确（长度 {result.Token.Length}），已忽略。", LogLevel.Error);
                Report("面板返回的密钥格式不正确，请稍后重试。", "获取密钥失败", MessageBoxImage.Warning, false);
                return;
            }

            SetAccessKeyText(result.Token);
            SaveManualAccessKey(result.Token);

            AppendLog(
                $"已获取账号 {result.Username} 的访问密钥（16 位十六进制），已填入输入框并保存。",
                LogLevel.Info);
        }
        catch (Exception ex)
        {
            AppendLog($"获取访问密钥失败：{ex.Message}", LogLevel.Error);
            Report($"获取访问密钥失败：{ex.Message}", "错误", MessageBoxImage.Error, false);
        }
        finally
        {
            BrowserTokenButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 用系统默认浏览器打开面板。原来的实现是在窗口里内嵌 WebView2，
    /// 那样既带来额外的运行时依赖，也要自己维护一套浏览器内的登录状态。
    /// </summary>
    private static void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Logger.Log(LogLevel.Info, $"已在默认浏览器中打开：{url}");
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Error, $"打开浏览器失败：{ex.Message}");
            WpfMessageBox.Show(
                $"无法打开默认浏览器：{ex.Message}{Environment.NewLine}{Environment.NewLine}请手动访问：{Environment.NewLine}{url}",
                "打开失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void AutoStartCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializingAutoStart)
        {
            return;
        }

        SetAutoStartup(true);
    }

    private void AutoStartCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_isInitializingAutoStart)
        {
            return;
        }

        SetAutoStartup(false);
    }

    private void AutoReconnectCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializingAutoReconnect)
        {
            return;
        }

        _isAutoReconnectEnabled = true;
        AppendLog("已开启断线自动重连。");
    }

    private void AutoReconnectCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_isInitializingAutoReconnect)
        {
            return;
        }

        _isAutoReconnectEnabled = false;
        AppendLog("已关闭断线自动重连。");
    }

    private void SetAutoStartup(bool enable)
    {
        try
        {
            _startupManager.Set(enable);
            AppendLog(enable ? "已启用开机自启。" : "已关闭开机自启。");
        }
        catch (Exception ex)
        {
            AppendLog($"设置开机自启失败: {ex.Message}", LogLevel.Error);
            WpfMessageBox.Show($"设置开机自启失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void NodeIdTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = NodeIdTextBox.Text.Trim();
        if (int.TryParse(text, out var nodeId) && nodeId > 0)
        {
            SaveManualNodeId(nodeId);
        }
    }

    private async Task StartTunnelAsync(bool isAutoReconnect = false)
    {
        if (_state == TunnelState.Starting)
        {
            return;
        }

        try
        {
            UpdateState(TunnelState.Starting);

            var accessKey = ResolveAccessKey();
            if (string.IsNullOrWhiteSpace(accessKey))
            {
                UpdateState(TunnelState.Stopped);
                Report("请先输入有效访问密钥（16 位十六进制，可在面板用户中心查看）。", "访问密钥无效", MessageBoxImage.Information, isAutoReconnect);
                return;
            }

            var nodeId = ResolveNodeId();
            if (nodeId <= 0)
            {
                UpdateState(TunnelState.Stopped);
                Report("请先输入有效节点ID（正整数）。", "节点ID无效", MessageBoxImage.Information, isAutoReconnect);
                return;
            }

            SaveManualAccessKey(accessKey);
            SaveManualNodeId(nodeId);

            AppendLog($"正在请求节点 {nodeId} 的配置...");
            var config = await _apiClient.GetConfigAsync(accessKey, nodeId);

            _configManager.WriteConfig(config);
            AppendLog($"配置文件已写入: {_configManager.ConfigPath}");

            // 服务端是 frps 0.29.0：实测只有 0.28.x / 0.29.x 的 frpc 能登录成功，
            // 现代版本会以 "login to server failed: session shutdown" 失败 —— 那个报错
            // 完全看不出是版本问题，所以这里先探测版本并明确提示。
            var frpcVersion = _frpProcessManager.GetClientVersion();

            if (frpcVersion is null)
            {
                AppendLog("未能读取 frpc 版本（frpc.exe 缺失或无法执行）。", LogLevel.Warning);
            }
            else if (IsCompatibleFrpcVersion(frpcVersion))
            {
                AppendLog($"frpc 版本 {frpcVersion}，与服务端 frps 0.29.0 兼容。");
            }
            else
            {
                AppendLog(
                    $"⚠ 检测到 frpc {frpcVersion}，与服务端 frps 0.29.0 不兼容：" +
                    "实测只有 0.28.x / 0.29.x 能登录成功，其他版本会以 " +
                    "“login to server failed: session shutdown” 失败。请更换 frpc.exe。",
                    LogLevel.Warning);
            }

            _frpProcessManager.Start(_configManager.ConfigPath);

            // frpc 可能在启动后立刻退出（配置有误、服务端拒绝、token 失效）。
            // 而 Exited 事件会先把状态改回“已停止”，若这里无条件写成“运行中”，
            // 就会出现「界面显示运行中、实际进程已死」的假状态。
            await Task.Delay(400);

            if (!_frpProcessManager.IsRunning)
            {
                UpdateState(TunnelState.Stopped);
                AppendLog("frpc 启动后立即退出，请检查上方日志与 frpc.ini 内容。", LogLevel.Error);
                Report("frpc 启动后立即退出，请查看日志了解具体原因。", "启动失败", MessageBoxImage.Error, isAutoReconnect);
                return;
            }

            _lastStartedAccessKey = accessKey;
            _reconnectAttempts = 0;

            UpdateState(TunnelState.Running);
            AppendLog("隧道已启动。", LogLevel.Info);
        }
        catch (FileNotFoundException ex)
        {
            UpdateState(TunnelState.Error);
            AppendLog($"启动失败: {ex.Message}", LogLevel.Error);
            Report("未找到 frpc.exe，请将 frpc.exe 放到程序同目录。", "启动失败", MessageBoxImage.Error, isAutoReconnect);
        }
        catch (ApiException ex)
        {
            UpdateState(TunnelState.Error);
            AppendLog($"接口调用失败: {ex.Message}", LogLevel.Error);
            Report(ex.Message, "获取配置失败", MessageBoxImage.Warning, isAutoReconnect);
        }
        catch (HttpRequestException ex)
        {
            UpdateState(TunnelState.Error);
            AppendLog($"网络请求失败: {ex.Message}", LogLevel.Error);
            Report("网络请求失败，请检查网络后重试。", "请求失败", MessageBoxImage.Warning, isAutoReconnect);
        }
        catch (TaskCanceledException)
        {
            UpdateState(TunnelState.Error);
            AppendLog("网络请求超时，请稍后重试。", LogLevel.Error);
            Report("请求超时，请稍后重试。", "超时", MessageBoxImage.Warning, isAutoReconnect);
        }
        catch (Exception ex)
        {
            UpdateState(TunnelState.Error);
            AppendLog($"启动隧道失败: {ex.Message}", LogLevel.Error);
            Report($"启动隧道失败：{ex.Message}", "错误", MessageBoxImage.Error, isAutoReconnect);
        }
    }

    /// <summary>
    /// 统一的失败反馈：手动启动时弹窗提示，自动重连时只写日志，
    /// 避免断网期间反复弹出模态框把界面挡住。
    /// </summary>
    private void Report(string message, string title, MessageBoxImage icon, bool isAutoReconnect)
    {
        if (isAutoReconnect)
        {
            AppendLog($"[自动重连] {message}", LogLevel.Warning);
            return;
        }

        WpfMessageBox.Show(message, title, MessageBoxButton.OK, icon);
    }

    private async Task StopTunnelAsync()
    {
        try
        {
            // 先清掉重连依据：手动停止不应被 OnFrpcExited 当成“意外退出”而自动重连
            _lastStartedAccessKey = null;
            _reconnectAttempts = 0;

            await Task.Run(() => _frpProcessManager.Stop());
            UpdateState(TunnelState.Stopped);
            AppendLog("隧道已停止。", LogLevel.Info);
        }
        catch (Exception ex)
        {
            UpdateState(TunnelState.Error);
            AppendLog($"停止隧道失败: {ex.Message}", LogLevel.Error);
        }
    }

    private string? ResolveAccessKey()
    {
        var key = GetManualAccessKey();
        if (!string.IsNullOrWhiteSpace(key) && TokenPattern.IsMatch(key))
        {
            Logger.Log(LogLevel.Info, "已使用手动输入访问密钥。");
            return key;
        }

        return null;
    }

    private int ResolveNodeId()
    {
        var text = NodeIdTextBox.Text.Trim();
        return int.TryParse(text, out var nodeId) && nodeId > 0 ? nodeId : -1;
    }

    private void UpdateState(TunnelState state)
    {
        _state = state;

        switch (state)
        {
            case TunnelState.Stopped:
                StatusTextBlock.Text = "已停止";
                StatusDot.Fill = new SolidColorBrush(WpfColor.FromRgb(144, 144, 144));
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                break;
            case TunnelState.Starting:
                StatusTextBlock.Text = "启动中";
                StatusDot.Fill = new SolidColorBrush(WpfColor.FromRgb(255, 165, 0));
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = false;
                break;
            case TunnelState.Running:
                StatusTextBlock.Text = "运行中";
                StatusDot.Fill = new SolidColorBrush(WpfColor.FromRgb(31, 157, 85));
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = true;
                break;
            case TunnelState.Error:
                StatusTextBlock.Text = "错误";
                StatusDot.Fill = new SolidColorBrush(WpfColor.FromRgb(214, 69, 69));
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = _frpProcessManager.IsRunning;
                break;
        }
    }

    private void AppendLog(string message, LogLevel level = LogLevel.Info)
    {
        AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}", level);
    }

    private void AppendRawLog(LogLevel level, string line)
    {
        // frpc 把失败原因混在标准输出里，按关键字提一下级别，
        // 否则“连不上服务器”和普通信息一个颜色，很难一眼看到。
        AppendLine(line, ClassifyFrpcLine(line, level));
    }

    private static LogLevel ClassifyFrpcLine(string line, LogLevel fallback)
    {
        if (fallback != LogLevel.Debug || string.IsNullOrWhiteSpace(line))
        {
            return fallback;
        }

        if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("refused", StringComparison.OrdinalIgnoreCase)
            || line.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Error;
        }

        if (line.Contains("warn", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Warning;
        }

        return LogLevel.Debug;
    }

    /// <summary>
    /// frpc 意外退出时的处理：更新状态，并在开启自动重连时按退避间隔重试。
    /// </summary>
    private void OnFrpcExited()
    {
        UpdateState(TunnelState.Stopped);

        if (_isExitRequested || !_isAutoReconnectEnabled || string.IsNullOrWhiteSpace(_lastStartedAccessKey))
        {
            return;
        }

        if (_reconnectAttempts >= MaxReconnectAttempts)
        {
            AppendLog($"frpc 已连续退出 {MaxReconnectAttempts} 次，停止自动重连。", LogLevel.Error);
            return;
        }

        _reconnectAttempts++;
        var delaySeconds = Math.Min(30, 5 * _reconnectAttempts);

        AppendLog(
            $"检测到 frpc 退出，{delaySeconds} 秒后自动重连（第 {_reconnectAttempts}/{MaxReconnectAttempts} 次）。",
            LogLevel.Warning);

        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

            if (_isExitRequested || _state == TunnelState.Running)
            {
                return;
            }

            await StartTunnelAsync(isAutoReconnect: true);
        });
    }

    private void AppendLine(string line, LogLevel level)
    {
        // 控件还没创建（XAML 解析期间，或日志早于窗口构造）时先入队，
        // 等窗口就绪后由 FlushPendingLogs 补显，避免直接空引用崩溃。
        if (LogBox is null)
        {
            lock (_pendingLogLock)
            {
                if (_pendingLogEntries.Count >= MaxLogLines)
                {
                    _pendingLogEntries.RemoveAt(0);
                }

                _pendingLogEntries.Add((line, level));
            }

            return;
        }

        Dispatcher.Invoke(() =>
        {
            var paragraph = new Paragraph(new Run(line))
            {
                Margin = new Thickness(0, 0, 0, 2),
                Foreground = ResolveLogBrush(level)
            };

            LogBox.Document.Blocks.Add(paragraph);

            // 长时间运行不能无限堆积段落，只保留最近若干行
            while (LogBox.Document.Blocks.Count > MaxLogLines)
            {
                var first = LogBox.Document.Blocks.FirstBlock;
                if (first is null)
                {
                    break;
                }

                LogBox.Document.Blocks.Remove(first);
            }

            LogBox.ScrollToEnd();
        });
    }

    private void FlushPendingLogs()
    {
        if (LogBox is null)
        {
            return;
        }

        List<(string Line, LogLevel Level)> pending;

        lock (_pendingLogLock)
        {
            if (_pendingLogEntries.Count == 0)
            {
                return;
            }

            pending = new List<(string, LogLevel)>(_pendingLogEntries);
            _pendingLogEntries.Clear();
        }

        foreach (var (text, level) in pending)
        {
            AppendLine(text, level);
        }
    }

    private static WpfBrush ResolveLogBrush(LogLevel level)
    {
        var key = level switch
        {
            LogLevel.Error => "LogErrorBrush",
            LogLevel.Warning => "LogWarningBrush",
            LogLevel.Debug => "LogDebugBrush",
            _ => "LogInfoBrush"
        };

        // 这里必须全限定：UseWindowsForms 同时引入了 System.Windows.Forms.Application
        // 与 System.Drawing.Brushes，直接用简单名会产生二义。
        return System.Windows.Application.Current?.TryFindResource(key) as WpfBrush
               ?? System.Windows.Media.Brushes.Gainsboro;
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Document.Blocks.Clear();
        AppendLog("日志已清空。", LogLevel.Info);
    }

    /// <summary>
    /// 服务端为官方原版 frps 0.29.0。2026-09-29 实测：
    /// frpc 0.28.0 / 0.29.0 均可登录成功，0.52.3 直接失败。因此只接受 0.28.x / 0.29.x。
    /// </summary>
    private static bool IsCompatibleFrpcVersion(string version)
    {
        return version.StartsWith("0.28.", StringComparison.Ordinal)
            || version.StartsWith("0.29.", StringComparison.Ordinal);
    }

    private void InitializeTrayIcon()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "LingeringDawn Frp",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true
        };

        _notifyIcon.ContextMenuStrip = CreateTrayMenu();
        _notifyIcon.DoubleClick += (_, _) =>
        {
            if (IsVisible)
            {
                HideToTray(false);
            }
            else
            {
                ShowMainWindow();
            }
        };
    }

    private WinForms.ContextMenuStrip CreateTrayMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开窗口", null, (_, _) => ShowMainWindow());
        menu.Items.Add("打开面板", null, (_, _) => OpenExternalUrl(PanelUrl));
        menu.Items.Add("启动", null, (_, _) =>
        {
            _ = Dispatcher.InvokeAsync(async () => await StartTunnelAsync());
        });
        menu.Items.Add("停止", null, (_, _) =>
        {
            _ = Dispatcher.InvokeAsync(async () => await StopTunnelAsync());
        });
        menu.Items.Add("退出", null, (_, _) => ExitApplication());
        return menu;
    }

    private void ShowMainWindow()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    private void HideToTray(bool showTip)
    {
        ShowInTaskbar = false;
        Hide();

        if (showTip && !_trayTipShown)
        {
            _trayTipShown = true;
            _notifyIcon?.ShowBalloonTip(1200, "LingeringDawn Frp", "程序已最小化到系统托盘。", WinForms.ToolTipIcon.Info);
        }
    }

    private void ExitApplication()
    {
        _isExitRequested = true;
        Close();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExitRequested)
        {
            return;
        }

        e.Cancel = true;
        HideToTray(true);
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && !_isExitRequested)
        {
            HideToTray(false);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _frpProcessManager.Stop();
        _frpProcessManager.Dispose();

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        Logger.LogAdded -= AppendRawLog;
    }

    private void ShowTokenCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        SetTokenVisibility(true);
    }

    private void ShowTokenCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        SetTokenVisibility(false);
    }

    private void TokenTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncingTokenInput)
        {
            return;
        }

        _isSyncingTokenInput = true;
        TokenPasswordBox.Password = TokenTextBox.Text;
        _isSyncingTokenInput = false;
    }

    private void TokenPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isSyncingTokenInput)
        {
            return;
        }

        _isSyncingTokenInput = true;
        TokenTextBox.Text = TokenPasswordBox.Password;
        _isSyncingTokenInput = false;
    }

    private void SetTokenVisibility(bool show)
    {
        if (show)
        {
            TokenTextBox.Visibility = Visibility.Visible;
            TokenPasswordBox.Visibility = Visibility.Collapsed;
            TokenTextBox.Focus();
        }
        else
        {
            TokenTextBox.Visibility = Visibility.Collapsed;
            TokenPasswordBox.Visibility = Visibility.Visible;
            TokenPasswordBox.Focus();
        }
    }

    private string GetManualAccessKey()
    {
        return ShowTokenCheckBox.IsChecked == true
            ? (TokenTextBox.Text ?? string.Empty).Trim()
            : TokenPasswordBox.Password.Trim();
    }

    private void LoadManualAccessKey()
    {
        try
        {
            if (!File.Exists(_tokenFilePath))
            {
                SetAccessKeyText(string.Empty);
                return;
            }

            var token = File.ReadAllText(_tokenFilePath).Trim();
            SetAccessKeyText(token);
            _lastSavedAccessKey = token;
            if (!string.IsNullOrWhiteSpace(token))
            {
                AppendLog("已加载本地保存的访问密钥。", LogLevel.Info);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"读取本地访问密钥失败: {ex.Message}", LogLevel.Warning);
        }
    }

    private void SaveManualAccessKey(string token)
    {
        try
        {
            if (string.Equals(_lastSavedAccessKey, token, StringComparison.Ordinal))
            {
                return;
            }

            File.WriteAllText(_tokenFilePath, token);
            _lastSavedAccessKey = token;
        }
        catch (Exception ex)
        {
            AppendLog($"保存访问密钥失败: {ex.Message}", LogLevel.Warning);
        }
    }

    private void SetAccessKeyText(string token)
    {
        _isSyncingTokenInput = true;
        TokenTextBox.Text = token;
        TokenPasswordBox.Password = token;
        _isSyncingTokenInput = false;
    }

    private void LoadManualNodeId()
    {
        try
        {
            if (!File.Exists(_nodeIdFilePath))
            {
                NodeIdTextBox.Text = DefaultNodeIdText;
                return;
            }

            var text = File.ReadAllText(_nodeIdFilePath).Trim();
            if (!int.TryParse(text, out var nodeId) || nodeId <= 0)
            {
                NodeIdTextBox.Text = DefaultNodeIdText;
                return;
            }

            NodeIdTextBox.Text = nodeId.ToString();
            _lastSavedNodeId = nodeId;
            AppendLog($"已加载本地保存的节点ID: {nodeId}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            NodeIdTextBox.Text = DefaultNodeIdText;
            AppendLog($"读取本地节点ID失败: {ex.Message}", LogLevel.Warning);
        }
    }

    private void SaveManualNodeId(int nodeId)
    {
        try
        {
            if (_lastSavedNodeId == nodeId)
            {
                return;
            }

            File.WriteAllText(_nodeIdFilePath, nodeId.ToString());
            _lastSavedNodeId = nodeId;
        }
        catch (Exception ex)
        {
            AppendLog($"保存节点ID失败: {ex.Message}", LogLevel.Warning);
        }
    }

}
