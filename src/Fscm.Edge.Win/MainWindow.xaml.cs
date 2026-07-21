// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Printing;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fscm.Edge.Win.Models;
using Fscm.Edge.Win.Services;
using Microsoft.Win32;
using QRCoder;
using DrawingIcon = System.Drawing.Icon;
using FormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using FormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using FormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;

namespace Fscm.Edge.Win;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF Window lifetime is handled through Closing, where the runtime manager is disposed.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "IDisposableAnalyzers.Correctness",
    "IDISP006:Implement IDisposable",
    Justification = "The WPF window owns the tray icon and runtime manager; both are disposed during explicit tray exit.")]
public partial class MainWindow : Window
{
    private const int ProductPageSize = 20;
    private const string CopyCodeTargetTag = "CopyCodeTarget";
    private readonly EdgeRuntimeManager _runtime = new();
    private readonly AppUpdateService _updates = new();
    private readonly StartupService _startup = new();
    private readonly LocalPrinterService _printerService = new();
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly FormsNotifyIcon _notifyIcon;
    private readonly List<EdgePrintJob> _allPrintJobs = [];
    private IReadOnlyList<PrintTemplateProfile> _printTemplates = [];
    private IReadOnlyList<PrintTemplateProfile> _labelTemplates = [];
    private IReadOnlyList<PrintTemplateProfile> _availableSkuPrintTemplates = [];
    private IReadOnlyList<LocalPrinter> _localPrinters = [];
    private IReadOnlyList<SkuSummary> _productSkus = [];
    private IReadOnlyList<BoxLabelSummary> _boxLabels = [];
    private IReadOnlyList<BatchPrintItem> _batchImportedItems = [];
    private IReadOnlyList<BatchPrintItem> _batchPreviewItems = [];
    private uint _batchCenterNodeId;
    private bool _batchPrintBusy;
    private int _boxLabelPage = 1;
    private long _boxLabelTotal;
    private string _productQueryMode = "sku";
    private int _productPage = 1;
    private long _productTotal;
    private int _productCurrentCount;
    private uint? _selectedProductId;
    private string _selectedProductCode = string.Empty;
    private string? _editingPrintTemplateId;
    private bool _loadingPrintTemplate;
    private bool _loadingLabelTemplate;
    private bool _settingsFormLoaded;
    private int _printingJobs;
    private static readonly IReadOnlyList<PrintSizePreset> PrintSizePresets =
    [
        new() { Id = "label_60x40mm", Name = "标签 60 x 40 mm", WidthMillimeters = 60, HeightMillimeters = 40 },
        new() { Id = "shipping_100x150mm", Name = "面单 100 x 150 mm", WidthMillimeters = 100, HeightMillimeters = 150 },
        new() { Id = "label_100x150mm", Name = "标签 10 x 15 cm（四联）", WidthMillimeters = 100, HeightMillimeters = 150 },
        new() { Id = "poster_600x400mm", Name = "大幅 60 x 40 cm", WidthMillimeters = 600, HeightMillimeters = 400 },
        new() { Id = "a4", Name = "A4 210 x 297 mm", WidthMillimeters = 210, HeightMillimeters = 297 },
        new() { Id = "custom", Name = "自定义尺寸" },
    ];

    private EdgeCenterRegistrationResult? _lastRegistration;
    private RemoteCenterStatus? _lastRemoteStatus;
    private AppUpdateRelease? _availableUpdate;
    private bool _allowExit;
    private bool _isShuttingDown;
    private bool _hasShownTrayMessage;
    private bool _checkingForUpdates;
    private bool _updatePromptShown;
    private bool _loadingStartupSetting;

    public MainWindow()
    {
        InitializeComponent();
        _notifyIcon = CreateNotifyIcon();
        MoveRemoteSettingsIntoAdvancedPage();
        SetProductQueryMode("sku");

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _timer.Tick += async (_, _) => await RefreshAllAsync();

        Loaded += async (_, _) =>
        {
            RefreshPrinters();
            LoadSettingsIntoForm();
            LoadManifest();
            LoadStartupSetting();
            await StartRuntimeAsync();
            await RefreshAllAsync();
            _timer.Start();
            _ = CheckForUpdatesAsync(interactive: false);
        };

        Closing += OnWindowClosing;
    }

    private FormsNotifyIcon CreateNotifyIcon()
    {
        var menu = new FormsContextMenuStrip();
        var openItem = new FormsToolStripMenuItem("打开 FSCM Edge");
        openItem.Click += (_, _) => Dispatcher.Invoke(ShowFromTray);
        var exitItem = new FormsToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Dispatcher.Invoke(RequestExit);
        menu.Items.Add(openItem);
        menu.Items.Add(exitItem);

        var notifyIcon = new FormsNotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "FSCM Edge 本地边缘服务",
            ContextMenuStrip = menu,
            Visible = true,
        };
        notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        return notifyIcon;
    }

    private static DrawingIcon LoadTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/fscm-edge.ico"));
        if (resource?.Stream is null)
        {
            return DrawingIcon.ExtractAssociatedIcon(Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty)
                ?? System.Drawing.SystemIcons.Application;
        }

        using var stream = resource.Stream;
        using var icon = new DrawingIcon(stream);
        return (DrawingIcon)icon.Clone();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        Activate();
        Focus();
    }

    private void HideToTray()
    {
        Hide();
        if (_hasShownTrayMessage)
        {
            return;
        }

        _hasShownTrayMessage = true;
        _notifyIcon.BalloonTipTitle = "FSCM Edge 仍在后台运行";
        _notifyIcon.BalloonTipText = "本地边缘服务会继续工作，可从托盘图标重新打开或退出。";
        _notifyIcon.ShowBalloonTip(2500, _notifyIcon.BalloonTipTitle, _notifyIcon.BalloonTipText, System.Windows.Forms.ToolTipIcon.Info);
    }

    private void RequestExit()
    {
        _allowExit = true;
        Close();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowExit)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (_isShuttingDown)
        {
            return;
        }

        e.Cancel = true;
        _isShuttingDown = true;
        _ = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        _timer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        await _runtime.StopAsync();
        _runtime.Dispose();
        _updates.Dispose();
        Close();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await RefreshAllAsync(forceRemoteCheck: true, showProgress: true);
    }

    private void LoadStartupSetting()
    {
        _loadingStartupSetting = true;
        try
        {
            LaunchAtSignInCheckBox.IsChecked = _startup.IsEnabled();
            ApplicationControlStatusText.Text = LaunchAtSignInCheckBox.IsChecked == true
                ? "已设置为登录 Windows 时启动 FSCM Edge。"
                : "未设置开机启动。";
        }
        catch (Exception ex)
        {
            LaunchAtSignInCheckBox.IsChecked = false;
            ApplicationControlStatusText.Text = $"无法读取开机启动设置：{ex.Message}";
        }
        finally
        {
            _loadingStartupSetting = false;
        }
    }

    private void OnLaunchAtSignInChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingStartupSetting)
        {
            return;
        }

        try
        {
            bool enabled = LaunchAtSignInCheckBox.IsChecked == true;
            _startup.SetEnabled(enabled);
            ApplicationControlStatusText.Text = enabled
                ? "已设置为登录 Windows 时启动 FSCM Edge。"
                : "已关闭开机启动。";
        }
        catch (Exception ex)
        {
            _loadingStartupSetting = true;
            LaunchAtSignInCheckBox.IsChecked = LaunchAtSignInCheckBox.IsChecked != true;
            _loadingStartupSetting = false;
            ApplicationControlStatusText.Text = $"无法保存开机启动设置：{ex.Message}";
            MessageBox.Show(ex.Message, "FSCM Edge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRestartApplicationClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "FSCM Edge 将停止本地边缘服务并重新启动。",
                "重启应用",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            string executablePath = Environment.ProcessPath ??
                Process.GetCurrentProcess().MainModule?.FileName ??
                throw new InvalidOperationException("Unable to locate the FSCM Edge executable.");
            _updates.StartApplicationAfterExit(executablePath, Process.GetCurrentProcess().Id);
            _allowExit = true;
            Close();
        }
        catch (Exception ex)
        {
            ApplicationControlStatusText.Text = $"无法重启应用：{ex.Message}";
            MessageBox.Show(ex.Message, "FSCM Edge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(interactive: true);
    }

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_checkingForUpdates || _isShuttingDown)
        {
            return;
        }

        _checkingForUpdates = true;
        CheckForUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查 FSCM Edge 更新...";
        try
        {
            AppUpdateCheckResult result = await _updates.CheckAsync();
            _availableUpdate = result.Release;
            UpdateStatusText.Text = result.Release is null
                ? result.Message
                : $"发现 FSCM Edge {result.Release.Version}。";

            if (result.Release is null)
            {
                if (interactive)
                {
                    MessageBox.Show(
                        result.Message,
                        "FSCM Edge 更新",
                        MessageBoxButton.OK,
                        result.IsServiceAvailable ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }

                return;
            }

            if (!interactive && _updatePromptShown)
            {
                return;
            }

            _updatePromptShown = true;
            string publishedAt = result.Release.PublishedAt is null
                ? string.Empty
                : $"\n发布时间：{result.Release.PublishedAt.Value.LocalDateTime:yyyy-MM-dd HH:mm}";
            string releaseNotes = string.IsNullOrWhiteSpace(result.Release.ReleaseNotes)
                ? string.Empty
                : $"\n\n更新说明：\n{result.Release.ReleaseNotes}";
            MessageBoxResult choice = MessageBox.Show(
                $"发现 FSCM Edge {result.Release.Version}。应用将下载更新、退出并打开安装向导。{publishedAt}{releaseNotes}",
                "FSCM Edge 更新",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (choice == MessageBoxResult.Yes)
            {
                await StartUpdateAsync(result.Release);
            }
            else
            {
                UpdateStatusText.Text = $"已跳过 FSCM Edge {result.Release.Version} 更新。";
            }
        }
        finally
        {
            _checkingForUpdates = false;
            if (!_isShuttingDown)
            {
                CheckForUpdatesButton.IsEnabled = true;
            }
        }
    }

    private async Task StartUpdateAsync(AppUpdateRelease release)
    {
        CheckForUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = $"正在下载 FSCM Edge {release.Version}...";
        try
        {
            string installerPath = await _updates.DownloadInstallerAsync(release);
            _updates.StartUpdateLauncher(installerPath, Process.GetCurrentProcess().Id);
            UpdateStatusText.Text = "更新已准备完成，正在退出并启动安装向导...";
            _allowExit = true;
            Close();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"更新下载失败：{ex.Message}";
            MessageBox.Show(ex.Message, "FSCM Edge 更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        await StartRuntimeAsync();
        await RefreshAllAsync(forceRemoteCheck: true);
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        await _runtime.StopAsync();
        await RefreshAllAsync();
    }

    private async void OnRestartClick(object sender, RoutedEventArgs e)
    {
        await _runtime.RestartAsync();
        await RefreshAllAsync(forceRemoteCheck: true);
    }

    private async void OnSaveRemoteClick(object sender, RoutedEventArgs e)
    {
        var settings = _runtime.LoadEdgeSettings();
        settings.CenterUrl = CenterUrlTextBox.Text.Trim();
        settings.NodeId = NodeIdTextBox.Text.Trim();
        settings.NodeName = NodeNameTextBox.Text.Trim();
        settings.LanBaseUrl = LanBaseUrlTextBox.Text.Trim();
        if (!ApplyNamespaceInput(settings))
        {
            return;
        }

        ApplyApiTokenInput(settings);
        if (!ApplyCacheInputs(settings))
        {
            return;
        }

        _runtime.SaveEdgeSettings(settings);
        ApiTokenPasswordBox.Password = string.Empty;
        LoadSettingsIntoForm();
        await RefreshAllAsync(forceRemoteCheck: true);
        MessageBox.Show("远端服务配置已保存。如本地服务已启动，可在高级设置页重启使后端读取最新配置。", "FSCM Edge");
    }

    private async void OnTestRemoteClick(object sender, RoutedEventArgs e)
    {
        TestRemoteButton.IsEnabled = false;
        RemoteDetailText.Text = "正在测试远端服务连接...";
        RemoteStatusMessageText.Text = "正在测试远端服务连接...";
        SetBadge(RemoteStatusBadge, RemoteStatusText, "测试中", BadgeKind.Warning);

        try
        {
            var settings = _runtime.LoadEdgeSettings();
            settings.CenterUrl = CenterUrlTextBox.Text.Trim();
            settings.NodeId = NodeIdTextBox.Text.Trim();
            settings.NodeName = NodeNameTextBox.Text.Trim();
            settings.LanBaseUrl = LanBaseUrlTextBox.Text.Trim();
            if (!ApplyNamespaceInput(settings))
            {
                return;
            }

            ApplyApiTokenInput(settings);
            if (!ApplyCacheInputs(settings))
            {
                return;
            }

            _runtime.SaveEdgeSettings(settings);
            ApiTokenPasswordBox.Password = string.Empty;

            _lastRemoteStatus = await _runtime.CheckRemoteCenterAsync();
            ApplyRemoteStatus(_lastRemoteStatus);

            var title = _lastRemoteStatus.IsReachable ? "连接成功" : "连接失败";
            MessageBox.Show(
                _lastRemoteStatus.Message,
                title,
                MessageBoxButton.OK,
                _lastRemoteStatus.IsReachable ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            TestRemoteButton.IsEnabled = true;
            LoadSettingsIntoForm();
        }
    }

    private async void OnRefreshTerminalsClick(object sender, RoutedEventArgs e)
    {
        await RefreshTerminalsAsync();
    }

    private async void OnFindTerminalClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EdgeTerminal terminal || !terminal.CanStartFind)
        {
            return;
        }

        await RunTerminalCommandAsync(terminal, stop: false);
    }

    private async void OnStopFindingTerminalClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EdgeTerminal terminal || !terminal.CanStopFind)
        {
            return;
        }

        await RunTerminalCommandAsync(terminal, stop: true);
    }

    private async Task RunTerminalCommandAsync(EdgeTerminal terminal, bool stop)
    {
        TerminalActionStatusText.Text = stop ? $"正在停止 {terminal.Name} 的响铃..." : $"正在让 {terminal.Name} 响铃...";
        try
        {
            _ = stop
                ? await _runtime.StopFindingTerminalAsync(terminal.TerminalId)
                : await _runtime.FindTerminalAsync(terminal.TerminalId);
            TerminalActionStatusText.Text = stop
                ? $"{terminal.Name} 已停止响铃。"
                : $"{terminal.Name} 已开始响铃，60 秒后自动停止。";
            await RefreshTerminalsAsync();
        }
        catch (Exception ex)
        {
            TerminalActionStatusText.Text = $"终端操作失败：{ex.Message}";
            MessageBox.Show(ex.Message, "寻找终端", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnRefreshPrintJobsClick(object sender, RoutedEventArgs e)
    {
        await RefreshPrintJobsAsync();
    }

    private async void OnPullPrintJobsClick(object sender, RoutedEventArgs e)
    {
        var pullButton = sender as Button;
        if (pullButton is not null)
        {
            pullButton.IsEnabled = false;
        }

        try
        {
            var pulled = await _runtime.PullRemotePrintJobAsync();
            await RefreshPrintJobsAsync();
            PrintQueueSummaryText.Text = pulled
                ? $"已向中心主动领取任务 · {DateTimeOffset.Now:HH:mm:ss}"
                : "当前无可领取任务，或本地打印队列尚未空闲。";
        }
        finally
        {
            if (pullButton is not null)
            {
                pullButton.IsEnabled = true;
            }
        }
    }

    private void OnSavePrintPollIntervalClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PrintPollIntervalTextBox.Text, out var seconds) || seconds is < 1 or > 300)
        {
            MessageBox.Show("领取周期请输入 1 到 300 秒之间的整数。", "FSCM Edge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = _runtime.LoadEdgeSettings();
        settings.PrintPollIntervalSeconds = seconds;
        _runtime.SaveEdgeSettings(settings);
        MessageBox.Show("领取周期已保存。请在高级设置中重启边缘服务后生效。", "FSCM Edge", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnTogglePrintConfigClick(object sender, RoutedEventArgs e)
    {
        PrintConfigCard.Visibility = PrintConfigCard.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (PrintConfigCard.Visibility == Visibility.Visible)
        {
            RefreshPrinters((PrinterComboBox.SelectedItem as LocalPrinter)?.Name);
        }
    }

    private async void OnRefreshCacheClick(object sender, RoutedEventArgs e)
    {
        await RefreshCacheStatusAsync();
    }

    private async void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        var cleared = await _runtime.ClearCacheAsync();
        CacheStatusText.Text = cleared ? "缓存已清空。" : "清空失败，请确认边缘服务正在运行。";
        await RefreshCacheStatusAsync();
    }

    private void OnRetryPrintJobClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("失败任务重试入口已预留，接入真实打印执行后会调用本地 edge 重试接口。", "FSCM Edge");
    }

    private async void OnRefreshPrintersClick(object sender, RoutedEventArgs e)
    {
        var selectedPrinter = (PrinterComboBox.SelectedItem as LocalPrinter)?.Name;
        var printers = _printerService.GetPrinters();
        ApplyPrinterSnapshot(printers, selectedPrinter);
        await _runtime.SyncPrintInventoryAsync(printers.Where(printer => printer.IsAvailable).Select(printer => printer.Name));
    }

    private async void OnPrintLabelClick(object sender, RoutedEventArgs e)
    {
        var text = LabelTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            LabelStatusText.Text = "请输入要生成二维码和打印的字符。";
            return;
        }

        if (text.Length > 200)
        {
            LabelStatusText.Text = "标签内容不能超过 200 个字符。";
            return;
        }

        if (LabelTemplateComboBox.SelectedItem is not PrintTemplateProfile template)
        {
            LabelStatusText.Text = "请先在打印配置中创建标签模板。";
            return;
        }

        NormalizeTemplateCompatibility(template);
        if (PrintTemplatePolicy.GetDisplayTextValidationError(template, text) is string displayTextError)
        {
            LabelStatusText.Text = displayTextError;
            return;
        }
        bool locationLayout = template.LayoutStyle == PrintTemplatePolicy.LocationCodeLayoutStyle;
        var labelPrefix = locationLayout ? string.Empty : LabelPrefixTextBox.Text.Trim();
        if (labelPrefix.Length > 20)
        {
            LabelStatusText.Text = "标签二维码前缀不能超过 20 个字符。";
            return;
        }

        var settings = _runtime.LoadEdgeSettings();
        settings.PrintTemplate = template.Id;
        settings.PrintWidthMillimeters = template.WidthMillimeters;
        settings.PrintHeightMillimeters = template.HeightMillimeters;
        settings.PrintOrientation = template.Orientation;
        settings.PrintMode = template.Mode;
        settings.PrintCopies = template.Copies;
        settings.PrintOffsetXMillimeters = template.OffsetXMillimeters;
        settings.PrintOffsetYMillimeters = template.OffsetYMillimeters;
        settings.PrintSafetyInsetMillimeters = template.SafetyInsetMillimeters;
        settings.SkuQrPrefix = template.SkuQrPrefix;
        var effectivePrinter = string.IsNullOrWhiteSpace(template.Printer) ? ResolveEffectivePrinter(settings) : template.Printer.Trim();
        if (string.IsNullOrWhiteSpace(effectivePrinter))
        {
            LabelStatusText.Text = "尚未配置默认打印机，请先到打印配置中选择打印机。";
            return;
        }

        var printerStatus = _printerService.GetPrinter(effectivePrinter);
        if (printerStatus is not { IsAvailable: true })
        {
            LabelStatusText.Text = printerStatus is null
                ? $"打印机 {effectivePrinter} 不存在或无法读取状态。"
                : $"打印机 {effectivePrinter} 当前不可打印：{printerStatus.StatusText}。";
            return;
        }

        settings.DefaultPrinter = effectivePrinter;
        if (!locationLayout && !string.Equals(template.LabelQrPrefix, labelPrefix, StringComparison.Ordinal))
        {
            template.LabelQrPrefix = labelPrefix;
            _runtime.SavePrintTemplates(_printTemplates);
        }

        LabelPrinterText.Text = $"当前打印机：{effectivePrinter}";
        LabelStatusText.Text = $"正在发送到 {effectivePrinter}...";
        try
        {
            await RunOnStaAsync(() => new QrPrintService().PrintLabel(settings, labelPrefix + text, text, template));
            LabelStatusText.Text = $"标签已发送，模板：{template.DisplayName}。";
        }
        catch (Exception ex)
        {
            LabelStatusText.Text = $"标签打印失败：{ex.Message}";
        }
    }

    private void OnPreviewLabelClick(object sender, RoutedEventArgs e)
    {
        if (LabelTemplateComboBox.SelectedItem is not PrintTemplateProfile template)
        {
            LabelStatusText.Text = "请先选择标签模板。";
            return;
        }

        ShowLabelTemplatePreview(template, LabelTextBox.Text.Trim());
    }

    private void ShowLabelTemplatePreview(PrintTemplateProfile template, string text)
    {
        NormalizeTemplateCompatibility(template);
        text = string.IsNullOrWhiteSpace(text)
            ? template.LayoutStyle == PrintTemplatePolicy.LocationCodeLayoutStyle ? "A-01-B-02-C3" : "SKU-001"
            : text;
        if (PrintTemplatePolicy.GetDisplayTextValidationError(template, text) is string displayTextError)
        {
            LabelStatusText.Text = displayTextError;
            return;
        }
        EdgeSettings settings = _runtime.LoadEdgeSettings();
        ApplyTemplateToSettings(settings, template);
        settings.DefaultPrinter = string.IsNullOrWhiteSpace(template.Printer)
            ? ResolveEffectivePrinter(settings)
            : template.Printer.Trim();
        try
        {
            PrintPreviewWindow preview = new(
                settings,
                template,
                template.LayoutStyle == PrintTemplatePolicy.LocationCodeLayoutStyle ? text : LabelPrefixTextBox.Text.Trim() + text,
                text)
            {
                Owner = this,
            };
            preview.ShowDialog();
            LabelStatusText.Text = $"已预览模板：{template.DisplayName}。";
        }
        catch (Exception ex)
        {
            LabelStatusText.Text = $"模板预览失败：{ex.Message}";
        }
    }

    private async void OnRefreshBatchPrintClick(object sender, RoutedEventArgs e)
    {
        await LoadBatchPrintPageAsync(forceNodeLookup: true);
    }

    private void OnBatchSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BatchRangePanel is null)
        {
            return;
        }
        string source = SelectedBatchSource();
        BatchRangePanel.Visibility = source == "range" ? Visibility.Visible : Visibility.Collapsed;
        BatchManualPanel.Visibility = source == "manual" ? Visibility.Visible : Visibility.Collapsed;
        BatchUploadPanel.Visibility = source == "upload" ? Visibility.Visible : Visibility.Collapsed;
        BatchSubmitButton.IsEnabled = false;
    }

    private async void OnPreviewBatchPrintClick(object sender, RoutedEventArgs e)
    {
        await PreviewBatchPrintAsync();
    }

    private async Task<bool> PreviewBatchPrintAsync()
    {
        if (_batchPrintBusy)
        {
            return false;
        }
        try
        {
            SetBatchPrintBusy(true);
            if (!await EnsureBatchCenterNodeAsync())
            {
                return false;
            }
            BatchPrintRequest request = BuildBatchPrintRequest();
            BatchPrintResult<BatchPrintPreview> result = await _runtime.PreviewBatchPrintAsync(request);
            if (!result.Succeeded || result.Data is null)
            {
                BatchPrintStatusText.Text = result.Message;
                BatchSubmitButton.IsEnabled = false;
                return false;
            }
            ApplyBatchPreview(result.Data);
            BatchPrintStatusText.Text = $"预览校验通过，共 {result.Data.TotalCount} 个独立打印任务。";
            return true;
        }
        catch (Exception ex)
        {
            BatchPrintStatusText.Text = $"生成预览失败：{ex.Message}";
            BatchSubmitButton.IsEnabled = false;
            return false;
        }
        finally
        {
            SetBatchPrintBusy(false);
        }
    }

    private async void OnSubmitBatchPrintClick(object sender, RoutedEventArgs e)
    {
        if (!await PreviewBatchPrintAsync())
        {
            return;
        }
        if (BatchWarningsText.Text.Length > 0 && MessageBox.Show(
                "预览包含重复内容警告，是否确认保留并提交？",
                "确认批量打印",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            SetBatchPrintBusy(true);
            BatchPrintRequest request = BuildBatchPrintRequest();
            BatchPrintResult<CenterPrintBatch> result = await _runtime.CreateBatchPrintAsync(request, $"windows-batch-print-{Guid.NewGuid():N}");
            if (!result.Succeeded || result.Data is null)
            {
                BatchPrintStatusText.Text = result.Message;
                return;
            }
            BatchPrintStatusText.Text = $"批次 {result.Data.Id} 已提交，共 {result.Data.TotalCount} 个任务。";
            await LoadBatchHistoryAsync();
        }
        catch (Exception ex)
        {
            BatchPrintStatusText.Text = $"提交批次失败：{ex.Message}";
        }
        finally
        {
            SetBatchPrintBusy(false);
        }
    }

    private async void OnImportBatchFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入批量打印内容",
            Filter = "支持的文件 (*.csv;*.xlsx)|*.csv;*.xlsx|CSV 文件 (*.csv)|*.csv|Excel 工作簿 (*.xlsx)|*.xlsx",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            int defaultCopies = ReadBatchNumber(BatchCopiesTextBox.Text, "默认份数", 1, PrintJobDispatchPolicy.MaxCopiesPerContent);
            _batchImportedItems = BatchPrintImportService.Read(dialog.FileName, defaultCopies);
            BatchImportFileText.Text = $"{Path.GetFileName(dialog.FileName)} · {_batchImportedItems.Count} 条";
            BatchSourceComboBox.SelectedValue = "upload";
            await PreviewBatchPrintAsync();
        }
        catch (Exception ex)
        {
            _batchImportedItems = [];
            BatchImportFileText.Text = "导入失败";
            BatchPrintStatusText.Text = ex.Message;
            BatchSubmitButton.IsEnabled = false;
        }
    }

    private void OnDownloadBatchTemplateClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存批量打印导入模板",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = "批量打印导入模板.csv",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, BatchPrintImportService.CsvTemplate, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            BatchPrintStatusText.Text = $"模板已保存：{dialog.FileName}";
        }
    }

    private async void OnPauseBatchClick(object sender, RoutedEventArgs e)
    {
        await UpdateBatchStatusAsync(sender, "pause", "暂停");
    }

    private async void OnResumeBatchClick(object sender, RoutedEventArgs e)
    {
        await UpdateBatchStatusAsync(sender, "resume", "继续");
    }

    private async void OnCancelBatchClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CenterPrintBatch batch ||
            MessageBox.Show($"确定取消批次 {batch.Id} 的剩余任务吗？", "取消批次", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        await UpdateBatchStatusAsync(sender, "cancel", "取消");
    }

    private async Task UpdateBatchStatusAsync(object sender, string action, string actionText)
    {
        if ((sender as FrameworkElement)?.DataContext is not CenterPrintBatch batch)
        {
            return;
        }
        bool allowed = action switch
        {
            "pause" => batch.Status == "running",
            "resume" => batch.Status == "paused",
            "cancel" => batch.Status is "running" or "paused",
            _ => false,
        };
        if (!allowed)
        {
            BatchPrintStatusText.Text = $"批次 {batch.Id} 当前状态不允许{actionText}。";
            return;
        }
        BatchPrintResult<CenterPrintBatch> result = await _runtime.UpdateCenterPrintBatchAsync(batch.Id, action);
        BatchPrintStatusText.Text = result.Succeeded ? $"批次 {batch.Id} 已{actionText}。" : result.Message;
        await LoadBatchHistoryAsync();
    }

    private async Task LoadBatchPrintPageAsync(bool forceNodeLookup = false)
    {
        EdgeSettings settings = _runtime.LoadEdgeSettings();
        BatchNodeText.Text = string.IsNullOrWhiteSpace(settings.NodeId) ? "未配置 Node ID" : $"{settings.NodeName} ({settings.NodeId})";
        string? selectedTemplateId = (BatchTemplateComboBox.SelectedItem as PrintTemplateProfile)?.Id;
        var templates = _labelTemplates.Where(template => template.IsPrinterAvailable).ToList();
        BatchTemplateComboBox.ItemsSource = templates;
        BatchTemplateComboBox.SelectedItem = templates.FirstOrDefault(template => template.Id == selectedTemplateId) ?? templates.FirstOrDefault();
        if (forceNodeLookup)
        {
            _batchCenterNodeId = 0;
        }
        if (await EnsureBatchCenterNodeAsync())
        {
            await LoadBatchHistoryAsync();
        }
    }

    private async Task<bool> EnsureBatchCenterNodeAsync()
    {
        if (_batchCenterNodeId > 0)
        {
            return true;
        }
        BatchPrintResult<uint> result = await _runtime.ResolveCurrentCenterNodeIdAsync();
        if (!result.Succeeded || result.Data == 0)
        {
            BatchPrintStatusText.Text = result.Message;
            return false;
        }
        _batchCenterNodeId = result.Data;
        return true;
    }

    private async Task LoadBatchHistoryAsync()
    {
        BatchPrintResult<IReadOnlyList<CenterPrintBatch>> result = await _runtime.GetCenterPrintBatchesAsync();
        if (!result.Succeeded || result.Data is null)
        {
            BatchPrintStatusText.Text = result.Message;
            return;
        }
        BatchHistoryGrid.ItemsSource = result.Data.Where(batch => batch.EdgeNodeId == _batchCenterNodeId).ToList();
    }

    private BatchPrintRequest BuildBatchPrintRequest()
    {
        if (_batchCenterNodeId == 0)
        {
            throw new InvalidOperationException("尚未解析当前中心节点。");
        }
        if (BatchTemplateComboBox.SelectedItem is not PrintTemplateProfile template)
        {
            throw new InvalidOperationException("请选择可用的标签模板。");
        }
        int copies = ReadBatchNumber(BatchCopiesTextBox.Text, "默认份数", 1, PrintJobDispatchPolicy.MaxCopiesPerContent);
        int interval = ReadBatchNumber(BatchIntervalTextBox.Text, "任务间隔", 1, 60);
        string sourceType = SelectedBatchSource();
        var source = new BatchPrintSource { Type = sourceType };
        if (sourceType == "range")
        {
            source.Range = new BatchPrintRangeSource
            {
                Start = BatchRangeStartTextBox.Text.Trim(),
                End = BatchRangeEndTextBox.Text.Trim(),
                Step = ReadBatchNumber(BatchRangeStepTextBox.Text, "步长", 1, int.MaxValue),
            };
        }
        else if (sourceType == "manual")
        {
            source.Items = ParseManualBatchItems(BatchManualTextBox.Text, copies);
        }
        else
        {
            if (_batchImportedItems.Count == 0)
            {
                throw new InvalidOperationException("请先导入 CSV 或 Excel 文件。");
            }
            source.Items = _batchImportedItems.Select(item => new BatchPrintItem { Content = item.Content, Copies = item.Copies }).ToList();
        }
        if (source.Items is not null)
        {
            PrintJobDispatchPolicy.EnsureContentCopiesAllowed(source.Items.Select(item => (item.Content, item.Copies)));
        }
        return new BatchPrintRequest
        {
            EdgeNodeId = _batchCenterNodeId,
            TemplateCode = template.Id,
            DefaultCopies = copies,
            IntervalSeconds = interval,
            FailurePolicy = BatchFailurePolicyComboBox.SelectedValue?.ToString() == "continue" ? "continue" : "pause",
            Source = source,
        };
    }

    private static List<BatchPrintItem> ParseManualBatchItems(string text, int defaultCopies)
    {
        var items = new List<BatchPrintItem>();
        foreach (string rawLine in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            string[] parts = line.Split('\t');
            int copies = defaultCopies;
            if (parts.Length > 1 && (!int.TryParse(parts[^1].Trim(), out copies) || !PrintJobDispatchPolicy.IsCopiesAllowed(copies)))
            {
                throw new InvalidOperationException($"手工内容第 {items.Count + 1} 行份数必须在 1 到 {PrintJobDispatchPolicy.MaxCopiesPerContent} 之间。");
            }
            string content = parts.Length > 1 ? string.Join('\t', parts[..^1]).Trim() : line;
            items.Add(new BatchPrintItem { Content = content, Copies = copies });
        }
        if (items.Count is < 1 or > 1000)
        {
            throw new InvalidOperationException("手工内容必须包含 1 到 1000 条记录。");
        }
        return items;
    }

    private void ApplyBatchPreview(BatchPrintPreview preview)
    {
        PrintJobDispatchPolicy.EnsureContentCopiesAllowed(preview.Items.Select(item => (item.Content, item.Copies)));
        string source = SelectedBatchSource() switch { "range" => "范围生成", "upload" => "文件导入", _ => "手工录入" };
        var remarks = _batchImportedItems.ToDictionary(item => item.SequenceNo, item => item.Remark);
        foreach (BatchPrintItem item in preview.Items)
        {
            item.Source = source;
            item.ValidationStatus = "有效";
            if (source == "文件导入" && remarks.TryGetValue(item.SequenceNo, out string? remark))
            {
                item.Remark = remark;
            }
        }
        _batchPreviewItems = preview.Items;
        BatchPreviewGrid.ItemsSource = _batchPreviewItems;
        BatchPreviewSummaryText.Text = $"{preview.TotalCount} 条";
        BatchWarningsText.Text = string.Join(Environment.NewLine, preview.Warnings);
        BatchSubmitButton.IsEnabled = preview.TotalCount > 0;
    }

    private void SetBatchPrintBusy(bool busy)
    {
        _batchPrintBusy = busy;
        BatchPreviewButton.IsEnabled = !busy;
        BatchSubmitButton.IsEnabled = !busy && _batchPreviewItems.Count > 0;
    }

    private string SelectedBatchSource()
    {
        return BatchSourceComboBox.SelectedValue?.ToString() ?? "range";
    }

    private static int ReadBatchNumber(string value, string field, int minimum, int maximum)
    {
        if (!int.TryParse(value.Trim(), out int parsed) || parsed < minimum || parsed > maximum)
        {
            throw new InvalidOperationException($"{field}必须是 {minimum} 到 {maximum} 之间的整数。");
        }
        return parsed;
    }

    private static Task RunOnStaAsync(Action action)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult(null);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "FSCM label print STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private void OnPrintSizePresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrintSizePresetComboBox.SelectedItem is not PrintSizePreset preset || preset.WidthMillimeters <= 0 || preset.HeightMillimeters <= 0)
        {
            return;
        }

        PrintWidthTextBox.Text = FormatMillimeters(preset.WidthMillimeters);
        PrintHeightTextBox.Text = FormatMillimeters(preset.HeightMillimeters);
    }

    private void OnSavePrintSettingsClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadPrintSettings(out var settings))
        {
            return;
        }

        _runtime.SaveEdgeSettings(settings);
        PrintConfigStatusText.Text = $"已保存：{settings.DefaultPrinter}，{FormatMillimeters(settings.PrintWidthMillimeters)} x {FormatMillimeters(settings.PrintHeightMillimeters)} mm。重启边缘服务后，局域网任务将使用新默认配置。";
    }

    private void OnPrintPreviewClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadPrintSettings(out var settings))
        {
            return;
        }

        var printerStatus = _printerService.GetPrinter(settings.DefaultPrinter);
        if (printerStatus is not { IsAvailable: true })
        {
            ShowPrintValidation(printerStatus is null
                ? $"打印机 {settings.DefaultPrinter} 不存在或无法读取状态。"
                : $"打印机 {settings.DefaultPrinter} 当前不可打印：{printerStatus.StatusText}。");
            return;
        }

        try
        {
            _runtime.SaveEdgeSettings(settings);

            PrintTemplateProfile template = BuildEditedPrintTemplate(settings);
            if (string.Equals(template.Type, "manufacturer_box_mark", StringComparison.OrdinalIgnoreCase))
            {
                ManufacturerBoxMark sample = ManufacturerBoxMarkPrintService.CreatePreviewSample();
                ManufacturerBoxMarkPrintService boxMarkPrinter = new();
                FixedDocument document = boxMarkPrinter.CreatePreviewDocument(settings, template, [sample], out string diagnostic);
                bool quadLayout = PrintTemplatePolicy.NormalizeLayoutStyle(template.LayoutStyle) == PrintTemplatePolicy.BoxMarkQuadLayoutStyle;
                PrintPreviewWindow preview = new(
                    document,
                    "厂家箱唛打印预览",
                    $"{template.DisplayName} · 100 x 150 mm · {(quadLayout ? "2 BOX + 2 SKU 二维码" : "1 BOX + 2 SKU 二维码")}",
                    diagnostic,
                    showConfirmButton: true,
                    confirmButtonText: "打印测试箱唛")
                {
                    Owner = this,
                };
                if (preview.ShowDialog() != true)
                {
                    PrintConfigStatusText.Text = "已取消测试打印，可返回修改厂家箱唛模板。";
                    return;
                }

                boxMarkPrinter.Print(
                    settings,
                    new EdgePrintJob
                    {
                        Id = "template-preview",
                        Copies = settings.PrintCopies,
                        BoxMarks = [sample],
                    },
                    template);
                PrintConfigStatusText.Text = $"厂家箱唛测试页已发送至 {settings.DefaultPrinter}。";
                return;
            }

            PrintPreviewWindow labelPreview = new(settings, template, settings.SkuQrPrefix + "SKU-001", "SKU-001") { Owner = this };
            if (labelPreview.ShowDialog() != true)
            {
                PrintConfigStatusText.Text = "已取消打印，可返回修改纸张尺寸或打印机配置。";
                return;
            }
            new QrPrintService().PrintLabel(settings, settings.SkuQrPrefix + "SKU-001", "SKU-001", template);
            PrintConfigStatusText.Text = $"模板测试标签已发送至 {settings.DefaultPrinter}。";
        }
        catch (Exception ex)
        {
            PrintConfigStatusText.Text = $"测试打印失败：{ex.Message}";
            MessageBox.Show(ex.Message, "打印测试失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnPrintFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyPrintFilter();
    }

    private void OnOpenRuntimeClick(object sender, RoutedEventArgs e)
    {
        _runtime.EnsureDefaultConfig();
        OpenPath(_runtime.RuntimeDirectory);
    }

    private void OnOpenStdoutLogClick(object sender, RoutedEventArgs e)
    {
        OpenLog(_runtime.StdoutLogPath);
    }

    private void OnOpenStderrLogClick(object sender, RoutedEventArgs e)
    {
        OpenLog(_runtime.StderrLogPath);
    }

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HomePage is null)
        {
            return;
        }

        var tag = (NavigationList.SelectedItem as ListBoxItem)?.Tag?.ToString() ?? "Home";
        SetPage(tag);
    }

    private void MoveRemoteSettingsIntoAdvancedPage()
    {
        if (RemotePage.Parent is Panel parent)
        {
            parent.Children.Remove(RemotePage);
            AdvancedContentStack.Children.Insert(0, RemotePage);
        }
    }

    private async Task StartRuntimeAsync()
    {
        SetBadge(HeaderStatusPill, HeaderStatusText, "启动中", BadgeKind.Warning);
        HeaderStatusDot.Fill = HeaderStatusText.Foreground;
        HeaderStatusDetailText.Foreground = HeaderStatusText.Foreground;
        HeaderStatusDetailText.Text = "正在启动本地服务";
        var status = await _runtime.StartAsync();
        ApplyRuntimeStatus(status);
        if (status.IsHealthy)
        {
            _lastRegistration = await _runtime.GetRuntimeRegistrationAsync();
            ApplyRegistrationStatus(_lastRegistration);
        }
    }

    private async Task<bool> RefreshAllAsync(bool forceRemoteCheck = false, bool showProgress = false)
    {
        if (showProgress)
        {
            SetRefreshButtonState(isBusy: true, "等待刷新");
            await _refreshGate.WaitAsync();
        }
        else if (!await _refreshGate.WaitAsync(0))
        {
            return false;
        }

        var succeeded = false;

        try
        {
            if (showProgress)
            {
                SetRefreshButtonState(isBusy: true, "刷新中");
            }

            if (!_settingsFormLoaded)
            {
                LoadSettingsIntoForm();
            }

            LoadManifest();

            var status = await _runtime.GetStatusAsync();
            ApplyRuntimeStatus(status);

            if (forceRemoteCheck || _lastRemoteStatus is null)
            {
                _lastRemoteStatus = await _runtime.CheckRemoteCenterAsync();
            }

            ApplyRemoteStatus(_lastRemoteStatus);

            if (status.IsHealthy && (_lastRegistration is null || forceRemoteCheck))
            {
                _lastRegistration = await _runtime.GetRuntimeRegistrationAsync();
            }

            ApplyRegistrationStatus(_lastRegistration);
            var printers = _printerService.GetPrinters();
            ApplyPrinterSnapshot(printers);
            await _runtime.SyncPrintInventoryAsync(printers.Where(printer => printer.IsAvailable).Select(printer => printer.Name));
            await RefreshTerminalsAsync();
            await RefreshPrintJobsAsync();
            await RefreshCacheStatusAsync();
            succeeded = true;
            return true;
        }
        catch (Exception ex)
        {
            HeaderStatusDetailText.Text = $"状态刷新失败 · {DateTimeOffset.Now:HH:mm:ss}";
            RefreshButton.ToolTip = ex.Message;
            return false;
        }
        finally
        {
            _refreshGate.Release();
            if (showProgress)
            {
                SetRefreshButtonState(isBusy: false, succeeded ? "已刷新" : "刷新失败");
                _ = RestoreRefreshButtonLabelAsync();
            }
        }
    }

    private void OnDetectPrintPaperClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadPrintSettings(out EdgeSettings settings))
        {
            return;
        }

        try
        {
            using LocalPrintServer server = new();
            using PrintQueue queue = server.GetPrintQueue(settings.DefaultPrinter);
            LocalPrinterService.EnsureQueueAvailable(queue);
            PreparedPrintTarget target = PrintTargetService.Prepare(queue, settings);
            PrintConfigStatusText.Text = target.Diagnostic;
        }
        catch (Exception ex)
        {
            PrintConfigStatusText.Text = $"纸张检测失败：{ex.Message}";
            MessageBox.Show(ex.Message, "纸张检测失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenPrinterPreferencesClick(object sender, RoutedEventArgs e)
    {
        if (PrinterComboBox.SelectedItem is not LocalPrinter printer)
        {
            ShowPrintValidation("请先选择打印机。");
            return;
        }

        ProcessStartInfo startInfo = new("rundll32.exe")
        {
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add("printui.dll,PrintUIEntry");
        startInfo.ArgumentList.Add("/e");
        startInfo.ArgumentList.Add("/n");
        startInfo.ArgumentList.Add(printer.Name);
        try
        {
            using Process? process = Process.Start(startInfo);
            PrintConfigStatusText.Text = $"已打开 {printer.Name} 的打印首选项；关闭窗口后点击“检测纸张”刷新。";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ShowPrintValidation($"无法打开打印首选项：{ex.Message}");
        }
    }

    private void SetRefreshButtonState(bool isBusy, string label)
    {
        RefreshButton.IsEnabled = !isBusy;
        RefreshButtonText.Text = label;
        if (isBusy)
        {
            var animation = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            RefreshIconRotation.BeginAnimation(RotateTransform.AngleProperty, animation);
            return;
        }

        RefreshIconRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        RefreshIconRotation.Angle = 0;
    }

    private async Task RestoreRefreshButtonLabelAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        if (RefreshButton.IsEnabled && RefreshButtonText.Text is "已刷新" or "刷新失败")
        {
            RefreshButtonText.Text = "刷新状态";
            RefreshButton.ToolTip = "重新检测运行状态";
        }
    }

    private async Task RefreshTerminalsAsync()
    {
        var terminals = await _runtime.GetTerminalsAsync();
        TerminalsGrid.ItemsSource = terminals;
        HomeTerminalCountText.Text = terminals.Count.ToString(CultureInfo.InvariantCulture);
        if (terminals.Count > 0 && !terminals.Any(terminal => terminal.Finding))
        {
            TerminalActionStatusText.Text = $"{terminals.Count} 台终端，{terminals.Count(terminal => terminal.CanStartFind)} 台可寻找";
        }
    }

    private async Task RefreshPrintJobsAsync()
    {
        _allPrintJobs.Clear();
        _allPrintJobs.AddRange(await _runtime.GetPrintJobsAsync());
        var hadQueuedJobs = _allPrintJobs.Any(job => string.Equals(job.Status, "queued", StringComparison.OrdinalIgnoreCase));
        await ProcessQueuedPrintJobsAsync();
        if (hadQueuedJobs)
        {
            _allPrintJobs.Clear();
            _allPrintJobs.AddRange(await _runtime.GetPrintJobsAsync());
        }

        var queued = _allPrintJobs.Count(job => string.Equals(job.Status, "queued", StringComparison.OrdinalIgnoreCase));
        var failed = _allPrintJobs.Count(job => string.Equals(job.Status, "failed", StringComparison.OrdinalIgnoreCase));
        var completed = _allPrintJobs.Count(job => job.Status.Equals("done", StringComparison.OrdinalIgnoreCase) ||
            job.Status.Equals("success", StringComparison.OrdinalIgnoreCase) ||
            job.Status.Equals("completed", StringComparison.OrdinalIgnoreCase));
        PrintQueueSummaryText.Text = _allPrintJobs.Count == 0
            ? "暂无打印任务"
            : $"共 {_allPrintJobs.Count} 个任务 · 排队 {queued} · 失败 {failed} · 已完成 {completed} · {DateTimeOffset.Now:HH:mm:ss} 更新";
        HomePrintQueueText.Text = queued.ToString(CultureInfo.InvariantCulture);
        HomePrintProcessedText.Text = completed.ToString(CultureInfo.InvariantCulture);
        ApplyPrintFilter();
    }

    private async Task ProcessQueuedPrintJobsAsync()
    {
        if (Interlocked.Exchange(ref _printingJobs, 1) == 1)
        {
            return;
        }

        try
        {
            var queued = _allPrintJobs
                .Where(job => string.Equals(job.Status, "queued", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var job in queued)
            {
                try
                {
                    var template = _printTemplates.FirstOrDefault(item =>
                        string.Equals(item.Id, job.TemplateId, StringComparison.OrdinalIgnoreCase));
                    if (template is null)
                    {
                        throw new InvalidOperationException($"打印模板不存在: {job.TemplateId}");
                    }

                    var settings = _runtime.LoadEdgeSettings();
                    settings.DefaultPrinter = string.IsNullOrWhiteSpace(template.Printer)
                        ? (string.IsNullOrWhiteSpace(job.Printer) ? ResolveEffectivePrinter(settings) : job.Printer)
                        : template.Printer;
                    if (string.IsNullOrWhiteSpace(settings.DefaultPrinter))
                    {
                        throw new InvalidOperationException("打印模板、打印任务和默认配置中都没有可用打印机。");
                    }

                    var printerStatus = _printerService.GetPrinter(settings.DefaultPrinter);
                    if (printerStatus is null)
                    {
                        throw new InvalidOperationException($"打印机 {settings.DefaultPrinter} 不存在或无法读取状态。");
                    }

                    if (!printerStatus.IsAvailable)
                    {
                        throw new InvalidOperationException($"打印机 {settings.DefaultPrinter} 当前不可打印：{printerStatus.StatusText}。");
                    }

                    var started = await _runtime.UpdatePrintJobStatusAsync(job.Id, "printing");
                    if (started is null)
                    {
                        continue;
                    }

                    settings.PrintTemplate = template.Id;
                    settings.PrintWidthMillimeters = template.WidthMillimeters;
                    settings.PrintHeightMillimeters = template.HeightMillimeters;
                    settings.PrintOrientation = template.Orientation;
                    settings.PrintMode = template.Mode;
                    settings.PrintCopies = Math.Max(1, template.Copies);
                    settings.PrintOffsetXMillimeters = template.OffsetXMillimeters;
                    settings.PrintOffsetYMillimeters = template.OffsetYMillimeters;
                    settings.PrintSafetyInsetMillimeters = template.SafetyInsetMillimeters;
                    settings.SkuQrPrefix = string.IsNullOrWhiteSpace(template.SkuQrPrefix) ? settings.SkuQrPrefix : template.SkuQrPrefix;

                    await RunOnStaAsync(() =>
                    {
                        if (string.Equals(started.Kind, "manufacturer_box_mark", StringComparison.OrdinalIgnoreCase))
                        {
                            settings.PrintCopies = Math.Max(1, started.Copies);
                            new ManufacturerBoxMarkPrintService().Print(settings, started, template);
                        }
                        else if (PrintJobDispatchPolicy.IsTextLabel(started.Kind))
                        {
                            if (string.IsNullOrWhiteSpace(started.Text))
                            {
                                throw new InvalidOperationException("标签内容为空。");
                            }

                            settings.PrintCopies = Math.Max(1, started.Copies);
                            new QrPrintService().PrintLabel(
                                settings,
                                string.IsNullOrWhiteSpace(started.QrCodeContent) ? started.Text : started.QrCodeContent,
                                started.Text,
                                template);
                        }
                        else
                        {
                            settings.PrintCopies = 1;
                            new QrPrintService().Print(settings, started, template);
                        }
                    });
                    await _runtime.UpdatePrintJobStatusAsync(job.Id, "completed");
                }
                catch (Exception ex)
                {
                    await _runtime.UpdatePrintJobStatusAsync(job.Id, "failed", ex.Message);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _printingJobs, 0);
        }
    }

    private async Task RefreshCacheStatusAsync()
    {
        var status = await _runtime.GetCacheStatusAsync();
        if (status is null)
        {
            CacheStatusText.Text = "边缘代理尚未就绪。";
            HomeCacheHitRateText.Text = "--";
            HomeCacheSizeText.Text = "边缘服务未就绪";
            return;
        }

        var total = status.Cache.Hits + status.Cache.Misses;
        var hitRate = total == 0 ? 0 : status.Cache.Hits * 100d / total;
        CacheStatusText.Text = $"{status.Cache.Entries} 项，{status.Cache.Bytes / 1024d / 1024d:0.0} MB，命中率 {hitRate:0.0}%，过期兜底 {status.Cache.StaleHits} 次，中心{(status.Center.Reachable ? "可用" : "不可用")}。";
        HomeCacheHitRateText.Text = $"{hitRate:0.0}%";
        HomeCacheSizeText.Text = $"{status.Cache.Entries} 项 · {status.Cache.Bytes / 1024d / 1024d:0.0} MB";
    }

    private void LoadSettingsIntoForm()
    {
        var settings = _runtime.LoadEdgeSettings();
        CenterUrlTextBox.Text = settings.CenterUrl;
        NodeIdTextBox.Text = settings.NodeId;
        NodeNameTextBox.Text = settings.NodeName;
        LanBaseUrlTextBox.Text = settings.LanBaseUrl;
        NamespaceIdTextBox.Text = settings.NamespaceId.ToString(CultureInfo.InvariantCulture);
        ApiTokenStatusText.Text = string.IsNullOrWhiteSpace(settings.ApiToken) ? "未配置" : "已配置，留空不修改";
        SidebarLanText.Text = _runtime.ResolveLanBaseUrl();
        HomeLanText.Text = _runtime.ResolveLanBaseUrl();
        RefreshLabelPrintUrl();
        RemoteNodeNameText.Text = $"节点名称：{settings.NodeName}";
        RemoteLanText.Text = $"局域网地址：{_runtime.ResolveLanBaseUrl()}";
        CapabilitiesItems.ItemsSource = settings.Capabilities;
        CacheModeComboBox.SelectedValue = settings.CacheMode;
        HomeSyncedText.Text = settings.CacheMode switch
        {
            "disabled" => "已关闭",
            "aggressive" => "积极缓存",
            _ => "标准缓存",
        };
        CacheMemoryTextBox.Text = settings.CacheMaxMemoryMegabytes.ToString(CultureInfo.InvariantCulture);
        CacheObjectTextBox.Text = settings.CacheMaxObjectMegabytes.ToString(CultureInfo.InvariantCulture);
        CacheStaleHoursTextBox.Text = settings.CacheMaxStaleHours.ToString(CultureInfo.InvariantCulture);
        PrintPollIntervalTextBox.Text = settings.PrintPollIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        LoadPrintSettingsIntoForm(settings);
        LoadPrintTemplates(settings.PrintTemplate);
        SkuQrPrefixTextBox.Text = settings.SkuQrPrefix;
        _settingsFormLoaded = true;
    }

    private void LoadPrintTemplates(string selectedId)
    {
        _printTemplates = PrintTemplatePolicy.OrderTemplates(_runtime.LoadPrintTemplates());
        foreach (var template in _printTemplates)
        {
            NormalizeTemplateCompatibility(template);
        }
        UpdateTemplateSortState();
        UpdateTemplatePrinterStatuses();

        _loadingPrintTemplate = true;
        PrintTemplatesList.ItemsSource = _printTemplates;
        PrintTemplatesList.SelectedItem = _printTemplates.FirstOrDefault(template => string.Equals(template.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? _printTemplates.FirstOrDefault();
        _loadingPrintTemplate = false;
        LoadLabelTemplates();
        RefreshProductPrintTemplates();
    }

    private void LoadLabelTemplates()
    {
        var settings = _runtime.LoadEdgeSettings();
        _labelTemplates = PrintTemplatePolicy.OrderLabelTemplates(_printTemplates, settings.PrintTemplate);
        if (_labelTemplates.Count == 0)
        {
            _labelTemplates =
            [
                new PrintTemplateProfile
                {
                    Id = "label_60x40mm",
                    TemplateNumber = "T01",
                    Name = "标签 60 × 40 mm",
                    SortOrder = 1,
                    Type = "label",
                    WidthMillimeters = 60,
                    HeightMillimeters = 40,
                    Orientation = "portrait",
                    Mode = "fit",
                    Copies = 1,
                    SkuQrPrefix = "T",
                    MaxDisplayLength = 16,
                },
            ];
        }

        foreach (PrintTemplateProfile template in _labelTemplates)
        {
            UpdateTemplatePrinterStatus(template, settings);
        }

        var availablePrinters = AvailablePrinterNames();
        var automaticallySelectable = _labelTemplates.Where(template =>
        {
            var printer = string.IsNullOrWhiteSpace(template.Printer)
                ? ResolveEffectivePrinter(settings)
                : template.Printer.Trim();
            return !string.IsNullOrWhiteSpace(printer) && availablePrinters.Contains(printer);
        });
        _loadingLabelTemplate = true;
        LabelTemplateTiles.ItemsSource = _labelTemplates;
        LabelTemplateComboBox.ItemsSource = _labelTemplates;
        LabelTemplateComboBox.SelectedItem = PrintTemplatePolicy.SelectAutomaticLabelTemplate(
            automaticallySelectable,
            explicitTemplateId: null,
            defaultTemplateId: settings.PrintTemplate)
            ?? PrintTemplatePolicy.SelectAutomaticLabelTemplate(
                _labelTemplates,
                explicitTemplateId: null,
                defaultTemplateId: settings.PrintTemplate);
        _loadingLabelTemplate = false;
        ApplyLabelTemplate(LabelTemplateComboBox.SelectedItem as PrintTemplateProfile);
        RefreshProductPrintTemplates();
    }

    private void RefreshProductPrintTemplates()
    {
        UpdateTemplatePrinterStatuses();
        PrintTemplatesList.Items.Refresh();
        var availablePrinters = AvailablePrinterNames();
        _availableSkuPrintTemplates = PrintTemplatePolicy.OrderLabelTemplates(
            _printTemplates
                .Where(template => !string.IsNullOrWhiteSpace(template.Printer) && availablePrinters.Contains(template.Printer.Trim())),
            _runtime.LoadEdgeSettings().PrintTemplate);

        var currentId = (ProductPrintTemplateComboBox.SelectedItem as PrintTemplateProfile)?.Id;
        var settings = _runtime.LoadEdgeSettings();
        var selected = PrintTemplatePolicy.SelectAutomaticLabelTemplate(
            _availableSkuPrintTemplates,
            currentId,
            settings.PrintTemplate);

        ProductPrintTemplateComboBox.ItemsSource = _availableSkuPrintTemplates;
        ProductPrintTemplateComboBox.SelectedItem = selected;
        ProductPrintTemplateText.Text = selected is null
            ? "没有可用标签模板"
            : $"标签模板：{selected.DisplayName}";
        ProductPrintButton.IsEnabled = selected is not null;
        ChangeProductPrintTemplateButton.IsEnabled = _availableSkuPrintTemplates.Count > 1;
        RefreshBoxLabelPrintTemplates(availablePrinters);
    }

    private void RefreshBoxLabelPrintTemplates(IReadOnlySet<string>? availablePrinters = null)
    {
        availablePrinters ??= AvailablePrinterNames();
        var currentId = (BoxLabelPrintTemplateComboBox.SelectedItem as PrintTemplateProfile)?.Id;
        IReadOnlyList<PrintTemplateProfile> templates = PrintTemplatePolicy.OrderManufacturerBoxMarkTemplates(_printTemplates, availablePrinters);
        BoxLabelPrintTemplateComboBox.ItemsSource = templates;
        BoxLabelPrintTemplateComboBox.SelectedItem = templates.FirstOrDefault(template => string.Equals(template.Id, currentId, StringComparison.OrdinalIgnoreCase)) ?? templates.FirstOrDefault();
        UpdateBoxLabelTemplateSelectionState();
    }

    private void OnBoxLabelPrintTemplateSelected(object sender, SelectionChangedEventArgs e)
    {
        UpdateBoxLabelTemplateSelectionState();
    }

    private void UpdateBoxLabelTemplateSelectionState()
    {
        if (BoxLabelPrintTemplateComboBox.SelectedItem is not PrintTemplateProfile template)
        {
            BoxLabelTemplateStatusText.Text = "没有厂家箱唛模板";
            BoxLabelPrintButton.IsEnabled = false;
            BoxLabelPreviewButton.IsEnabled = false;
            BoxLabelEditTemplateButton.IsEnabled = false;
            return;
        }

        BoxLabelPreviewButton.IsEnabled = true;
        BoxLabelEditTemplateButton.IsEnabled = true;
        if (string.IsNullOrWhiteSpace(template.Printer))
        {
            BoxLabelTemplateStatusText.Text = "未配置打印机，可预览或编辑";
            BoxLabelPrintButton.IsEnabled = false;
            return;
        }

        LocalPrinter? printer = FindPrinter(template.Printer);
        bool available = printer is { IsAvailable: true };
        BoxLabelTemplateStatusText.Text = available
            ? $"可打印 · {template.Printer}"
            : $"不可打印 · {template.Printer} · {printer?.StatusText ?? "状态未知"}";
        BoxLabelPrintButton.IsEnabled = available;
    }

    private void OnEditSelectedBoxMarkTemplateClick(object sender, RoutedEventArgs e)
    {
        if (BoxLabelPrintTemplateComboBox.SelectedItem is not PrintTemplateProfile template)
        {
            return;
        }

        ListBoxItem? printConfigItem = NavigationList.Items
            .OfType<ListBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), "PrintConfig", StringComparison.Ordinal));
        if (printConfigItem is not null)
        {
            NavigationList.SelectedItem = printConfigItem;
        }
        else
        {
            SetPage("PrintConfig");
        }

        PrintTemplatesList.SelectedItem = template;
        PrintTemplatesList.ScrollIntoView(template);
        ShowPrintTemplateEditor(template);
    }

    private void OnChangeProductPrintTemplateClick(object sender, RoutedEventArgs e)
    {
        if (_availableSkuPrintTemplates.Count == 0)
        {
            MessageBox.Show("没有可用的标签模板。请在打印配置中为标签模板配置一台在线打印机。", "产品管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ProductPrintTemplateComboBox.Visibility = Visibility.Visible;
        ProductPrintTemplateText.Visibility = Visibility.Collapsed;
        ProductPrintTemplateComboBox.IsDropDownOpen = true;
    }

    private void OnProductPrintTemplateSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProductPrintTemplateComboBox.SelectedItem is not PrintTemplateProfile template)
        {
            return;
        }

        ProductPrintTemplateText.Text = $"标签模板：{template.DisplayName}";
        ProductPrintTemplateComboBox.Visibility = Visibility.Collapsed;
        ProductPrintTemplateText.Visibility = Visibility.Visible;
    }

    private void OnLabelTemplateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingLabelTemplate)
        {
            ApplyLabelTemplate(LabelTemplateComboBox.SelectedItem as PrintTemplateProfile);
        }
    }

    private void ApplyLabelTemplate(PrintTemplateProfile? template)
    {
        foreach (PrintTemplateProfile item in _labelTemplates)
        {
            item.IsSelectedForLabelPrint = template is not null &&
                string.Equals(item.Id, template.Id, StringComparison.OrdinalIgnoreCase);
        }
        LabelTemplateTiles.Items.Refresh();

        if (template is null)
        {
            LabelTemplateSummaryText.Text = "未选择模板";
            LabelModeText.Text = "请在打印配置中新增标签模板";
            LabelPrinterText.Text = "默认打印机：-";
            LabelPaperText.Text = "-";
            LabelOrientationText.Text = "-";
            LabelPrefixTextBox.Text = string.Empty;
            LabelPrefixTextBox.IsEnabled = true;
            LabelPrintButton.IsEnabled = false;
            LabelPrintButton.ToolTip = "请先选择打印模板";
            return;
        }

        NormalizeTemplateCompatibility(template);
        var settings = _runtime.LoadEdgeSettings();
        var printer = string.IsNullOrWhiteSpace(template.Printer) ? ResolveEffectivePrinter(settings) : template.Printer;
        var printerStatus = FindPrinter(printer);
        LabelTemplateSummaryText.Text = template.DisplayName;
        string layoutText = GetLayoutStyleText(template.LayoutStyle);
        LabelModeText.Text = $"{template.WidthMillimeters:0.##} × {template.HeightMillimeters:0.##} mm · {GetOrientationText(template.Orientation)} · {layoutText}";
        LabelPrinterText.Text = string.IsNullOrWhiteSpace(printer)
            ? "默认打印机：未配置"
            : $"当前打印机：{printer} · {printerStatus?.StatusText ?? "状态未知"}";
        LabelPaperText.Text = $"{template.WidthMillimeters:0.##} × {template.HeightMillimeters:0.##} mm";
        LabelOrientationText.Text = GetOrientationText(template.Orientation);
        LabelPrefixTextBox.Text = template.LabelQrPrefix;
        bool locationLayout = template.LayoutStyle == PrintTemplatePolicy.LocationCodeLayoutStyle;
        LabelPrefixTextBox.IsEnabled = !locationLayout;
        if (locationLayout)
        {
            LabelPrefixTextBox.Text = string.Empty;
        }
        LabelPrintButton.IsEnabled = printerStatus is { IsAvailable: true };
        LabelPrintButton.ToolTip = printerStatus is { IsAvailable: true }
            ? "打印当前标签"
            : $"打印机当前不可打印：{printerStatus?.StatusText ?? "状态未知"}";
        if (printerStatus is not { IsAvailable: true })
        {
            LabelStatusText.Text = string.IsNullOrWhiteSpace(printer)
                ? "尚未配置打印机。"
                : $"打印机 {printer} 当前不可打印：{printerStatus?.StatusText ?? "状态未知"}。";
        }
    }

    private static string GetOrientationText(string orientation)
    {
        return string.Equals(orientation, "landscape", StringComparison.OrdinalIgnoreCase) ? "横向" : "纵向";
    }

    private static void NormalizeTemplateCompatibility(PrintTemplateProfile template)
    {
        template.Type = NormalizeTemplateType(template);
        template.Copies = Math.Clamp(template.Copies, 1, PrintJobDispatchPolicy.MaxCopiesPerContent);
        template.Mode = "fit";
        template.OffsetXMillimeters = Math.Clamp(template.OffsetXMillimeters, -5, 5);
        template.OffsetYMillimeters = Math.Clamp(template.OffsetYMillimeters, -5, 5);
        template.SafetyInsetMillimeters = template.SafetyInsetMillimeters > 0
            ? Math.Clamp(template.SafetyInsetMillimeters, 0.5, 5)
            : PrintPageContextFactory.DefaultSafetyInsetMillimeters;
        template.LayoutStyle = PrintTemplatePolicy.NormalizeLayoutStyle(template.LayoutStyle);
        template.MaxDisplayLength = PrintTemplatePolicy.GetMaxDisplayLength(template);
        template.Orientation = PrintTemplatePolicy.GetAutomaticOrientation(template.LayoutStyle);
        template.TextFontSizePoints = PrintTemplatePolicy.GetTextFontSizePoints(template);
    }

    private static string GetLayoutStyleText(string layoutStyle)
    {
        return PrintTemplatePolicy.NormalizeLayoutStyle(layoutStyle) switch
        {
            PrintTemplatePolicy.HorizontalLayoutStyle => "左右排版",
            PrintTemplatePolicy.LocationCodeLayoutStyle => "库位码四码排版",
            PrintTemplatePolicy.BoxMarkQuadLayoutStyle => "箱唛横向四码排版",
            _ => "上下排版",
        };
    }

    private PrintTemplateProfile? SelectLabelTemplateFromTile(object sender)
    {
        if (sender is not FrameworkElement { DataContext: PrintTemplateProfile template })
        {
            return null;
        }

        _loadingLabelTemplate = true;
        LabelTemplateComboBox.SelectedItem = template;
        _loadingLabelTemplate = false;
        ApplyLabelTemplate(template);
        return template;
    }

    private void OnSelectLabelTemplateTileClick(object sender, MouseButtonEventArgs e)
    {
        SelectLabelTemplateFromTile(sender);
    }

    private void OnPrintLabelTemplateTileClick(object sender, RoutedEventArgs e)
    {
        if (SelectLabelTemplateFromTile(sender) is not null)
        {
            OnPrintLabelClick(sender, e);
        }
    }

    private void OnPreviewLabelTemplateTileClick(object sender, RoutedEventArgs e)
    {
        PrintTemplateProfile? template = SelectLabelTemplateFromTile(sender);
        if (template is not null)
        {
            ShowLabelTemplatePreview(template, LabelTextBox.Text.Trim());
        }
    }

    private void OnEditLabelTemplateTileClick(object sender, RoutedEventArgs e)
    {
        PrintTemplateProfile? template = SelectLabelTemplateFromTile(sender);
        if (template is null)
        {
            return;
        }

        ListBoxItem? printConfigItem = NavigationList.Items
            .OfType<ListBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), "PrintConfig", StringComparison.Ordinal));
        if (printConfigItem is not null)
        {
            NavigationList.SelectedItem = printConfigItem;
        }
        else
        {
            SetPage("PrintConfig");
        }

        PrintTemplatesList.SelectedItem = template;
        PrintTemplatesList.ScrollIntoView(template);
        ShowPrintTemplateEditor(template);
    }

    private static void ApplyTemplateToSettings(EdgeSettings settings, PrintTemplateProfile template)
    {
        settings.PrintTemplate = template.Id;
        settings.PrintWidthMillimeters = template.WidthMillimeters;
        settings.PrintHeightMillimeters = template.HeightMillimeters;
        settings.PrintOrientation = template.Orientation;
        settings.PrintMode = template.Mode;
        settings.PrintCopies = template.Copies;
        settings.PrintOffsetXMillimeters = template.OffsetXMillimeters;
        settings.PrintOffsetYMillimeters = template.OffsetYMillimeters;
        settings.PrintSafetyInsetMillimeters = template.SafetyInsetMillimeters;
        settings.SkuQrPrefix = template.SkuQrPrefix;
    }

    private static string NormalizeTemplateType(PrintTemplateProfile template)
    {
        // Earlier builds persisted the built-in profiles as "custom". Migrate
        // those stable IDs once, without inferring a purpose from paper size.
        if (string.Equals(template.Id, "label_60x40mm", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(template.Id, "label_60x40mm_horizontal", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(template.Id, "location_100x150mm_landscape", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(template.Id, "label_100x150mm", StringComparison.OrdinalIgnoreCase))
        {
            return "label";
        }

        if (string.Equals(template.Id, "shipping_100x150mm", StringComparison.OrdinalIgnoreCase))
        {
            return "shipping";
        }

        if (string.Equals(template.Id, "manufacturer_box_mark_100x150mm", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(template.Id, "manufacturer_box_mark_quad_100x150mm", StringComparison.OrdinalIgnoreCase))
        {
            return "manufacturer_box_mark";
        }

        var type = template.Type.Trim().ToLowerInvariant();
        if (type is "label" or "shipping" or "manufacturer_box_mark" or "custom")
        {
            return type;
        }

        return "custom";
    }

    private void OnPrintTemplateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPrintTemplate || PrintTemplatesList.SelectedItem is not PrintTemplateProfile template)
        {
            return;
        }

        PrintConfigStatusText.Text = $"已选择模板：{template.DisplayName}。点击操作列中的“编辑”进入配置页面。";
    }

    private void ApplyPrintTemplateToEditor(PrintTemplateProfile template)
    {
        NormalizeTemplateCompatibility(template);
        PrinterComboBox.SelectedItem = (PrinterComboBox.ItemsSource as IEnumerable<LocalPrinter>)?.FirstOrDefault(
            printer => string.Equals(printer.Name, template.Printer, StringComparison.OrdinalIgnoreCase));
        PrintWidthTextBox.Text = FormatMillimeters(template.WidthMillimeters);
        PrintHeightTextBox.Text = FormatMillimeters(template.HeightMillimeters);
        PrintTemplateTypeComboBox.SelectedValue = template.Type;
        SkuQrPrefixTextBox.Text = string.IsNullOrWhiteSpace(template.SkuQrPrefix) ? "T" : template.SkuQrPrefix;
        PrintOffsetXTextBox.Text = template.OffsetXMillimeters.ToString("0.##", CultureInfo.InvariantCulture);
        PrintOffsetYTextBox.Text = template.OffsetYMillimeters.ToString("0.##", CultureInfo.InvariantCulture);
        PrintSafetyInsetTextBox.Text = template.SafetyInsetMillimeters.ToString("0.##", CultureInfo.InvariantCulture);
        PrintCopiesTextBox.Text = template.Copies.ToString(CultureInfo.InvariantCulture);
        PrintLayoutStyleComboBox.SelectedValue = template.LayoutStyle;
    }

    private PrintTemplateProfile BuildEditedPrintTemplate(EdgeSettings settings)
    {
        PrintTemplateProfile? existing = !string.IsNullOrWhiteSpace(_editingPrintTemplateId)
            ? _printTemplates.FirstOrDefault(template => string.Equals(template.Id, _editingPrintTemplateId, StringComparison.OrdinalIgnoreCase))
            : null;
        string layoutStyle = GetSelectedLayoutStyle();
        return new PrintTemplateProfile
        {
            Id = existing?.Id ?? settings.PrintTemplate,
            Name = string.IsNullOrWhiteSpace(PrintTemplateNameTextBox.Text) ? existing?.Name ?? "标签预览" : PrintTemplateNameTextBox.Text.Trim(),
            SortOrder = existing?.SortOrder ?? (_printTemplates.Count + 1),
            Type = GetSelectedTemplateType(existing?.Type),
            Printer = settings.DefaultPrinter,
            WidthMillimeters = settings.PrintWidthMillimeters,
            HeightMillimeters = settings.PrintHeightMillimeters,
            Orientation = PrintTemplatePolicy.GetAutomaticOrientation(layoutStyle),
            Mode = "fit",
            Copies = settings.PrintCopies,
            OffsetXMillimeters = settings.PrintOffsetXMillimeters,
            OffsetYMillimeters = settings.PrintOffsetYMillimeters,
            SafetyInsetMillimeters = settings.PrintSafetyInsetMillimeters,
            SkuQrPrefix = settings.SkuQrPrefix,
            LabelQrPrefix = layoutStyle == PrintTemplatePolicy.LocationCodeLayoutStyle ? string.Empty : existing?.LabelQrPrefix ?? string.Empty,
            LayoutStyle = layoutStyle,
            TextFontSizePoints = ResolveTemplateFontSize(settings, existing, layoutStyle),
            MaxDisplayLength = layoutStyle is PrintTemplatePolicy.HorizontalLayoutStyle or PrintTemplatePolicy.LocationCodeLayoutStyle
                ? PrintTemplatePolicy.RestrictedDisplayTextLength
                : existing is { MaxDisplayLength: > 0 } ? existing.MaxDisplayLength : 16,
        };
    }

    private string GetSelectedLayoutStyle()
    {
        return PrintTemplatePolicy.NormalizeLayoutStyle(PrintLayoutStyleComboBox.SelectedValue as string);
    }

    private static double ResolveTemplateFontSize(EdgeSettings settings, PrintTemplateProfile? existing, string layoutStyle)
    {
        if (layoutStyle == PrintTemplatePolicy.LocationCodeLayoutStyle)
        {
            return PrintTemplatePolicy.LocationCodeFontSizePoints;
        }
        bool is60x40 = Math.Abs(settings.PrintWidthMillimeters - 60) <= 0.1 &&
            Math.Abs(settings.PrintHeightMillimeters - 40) <= 0.1;
        if (is60x40)
        {
            return layoutStyle == PrintTemplatePolicy.HorizontalLayoutStyle
                ? PrintTemplatePolicy.Horizontal60x40FontSizePoints
                : PrintTemplatePolicy.Stacked60x40FontSizePoints;
        }

        return existing is { TextFontSizePoints: > 0 } ? existing.TextFontSizePoints : 10;
    }

    private void OnSavePrintTemplateClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadPrintSettings(out var settings))
        {
            return;
        }

        _runtime.SaveEdgeSettings(settings);

        var name = PrintTemplateNameTextBox.Text.Trim();
        if (name.Length is < 1 or > 50)
        {
            ShowPrintValidation("模板名称长度必须为 1 到 50 个字符。");
            return;
        }

        var existing = !string.IsNullOrWhiteSpace(_editingPrintTemplateId)
            ? _printTemplates.FirstOrDefault(template => string.Equals(template.Id, _editingPrintTemplateId, StringComparison.OrdinalIgnoreCase))
            : null;
        PrintTemplateProfile profile = BuildEditedPrintTemplate(settings);
        profile.Id = existing?.Id ?? $"template_{Guid.NewGuid():N}";
        profile.TemplateNumber = existing?.TemplateNumber ?? PrintTemplatePolicy.NextTemplateNumber(_printTemplates);
        profile.Name = name;

        var templates = NormalizeTemplateSortOrders(_printTemplates
            .Where(template => !string.Equals(template.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
            .Append(profile)
            .ToList());
        _runtime.SavePrintTemplates(templates);
        _printTemplates = templates;
        UpdateTemplateSortState();
        PrintTemplatesList.ItemsSource = _printTemplates;
        PrintTemplatesList.SelectedItem = profile;
        LoadLabelTemplates();
        ShowPrintTemplateList(profile.Id);
        PrintConfigStatusText.Text = $"模板“{profile.DisplayName}”已保存，可供手机终端选择。";
    }

    private void OnNewPrintTemplateClick(object sender, RoutedEventArgs e)
    {
        ShowPrintTemplateEditor(null);
    }

    private void OnCancelPrintTemplateEditClick(object sender, RoutedEventArgs e)
    {
        ShowPrintTemplateList(_editingPrintTemplateId);
        PrintConfigStatusText.Text = "已返回模板列表，未保存的修改已放弃。";
    }

    private void ShowPrintTemplateEditor(PrintTemplateProfile? template)
    {
        PrintTemplateListPanel.Visibility = Visibility.Collapsed;
        PrintTemplateEditorCard.Visibility = Visibility.Visible;
        _loadingPrintTemplate = true;
        try
        {
            if (template is null)
            {
                _editingPrintTemplateId = null;
                PrintTemplatesList.SelectedItem = null;
                PrintTemplateEditorTitleText.Text = "添加打印模板";
                PrintTemplateEditorSubtitleText.Text = "填写模板名称、打印机、尺寸、方向和二维码参数。";
                PrintTemplateNameTextBox.Text = "新建打印模板";
                PrintTemplateNumberTextBox.Text = PrintTemplatePolicy.NextTemplateNumber(_printTemplates);
                PrintTemplateIdTextBox.Text = "保存后自动生成";
                PrintTemplateTypeComboBox.SelectedValue = "label";
                PrintWidthTextBox.Text = "60";
                PrintHeightTextBox.Text = "40";
                PrintCopiesTextBox.Text = "1";
                PrintOffsetXTextBox.Text = "0";
                PrintOffsetYTextBox.Text = "0";
                PrintSafetyInsetTextBox.Text = PrintPageContextFactory.DefaultSafetyInsetMillimeters.ToString("0.##", CultureInfo.InvariantCulture);
                SkuQrPrefixTextBox.Text = "T";
                PrintLayoutStyleComboBox.SelectedValue = PrintTemplatePolicy.StackedLayoutStyle;
            }
            else
            {
                NormalizeTemplateCompatibility(template);
                _editingPrintTemplateId = template.Id;
                PrintTemplatesList.SelectedItem = template;
                PrintTemplateEditorTitleText.Text = "编辑打印模板";
                PrintTemplateEditorSubtitleText.Text = $"模板编号：{template.TemplateNumber} · 模板 ID：{template.Id}";
                PrintTemplateNameTextBox.Text = template.Name;
                PrintTemplateNumberTextBox.Text = template.TemplateNumber;
                PrintTemplateIdTextBox.Text = template.Id;
                ApplyPrintTemplateToEditor(template);
            }
        }
        finally
        {
            _loadingPrintTemplate = false;
        }

        PrintConfigStatusText.Text = template is null
            ? "已准备新模板，填写参数后点击保存模板。"
            : $"正在编辑模板：{template.DisplayName}。保存后保持原编号和 ID。";
    }

    private void ShowPrintTemplateList(string? selectedTemplateId = null)
    {
        _editingPrintTemplateId = null;
        PrintTemplateEditorCard.Visibility = Visibility.Collapsed;
        PrintTemplateListPanel.Visibility = Visibility.Visible;

        if (string.IsNullOrWhiteSpace(selectedTemplateId))
        {
            return;
        }

        PrintTemplateProfile? selected = _printTemplates.FirstOrDefault(template =>
            string.Equals(template.Id, selectedTemplateId, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            return;
        }

        _loadingPrintTemplate = true;
        try
        {
            PrintTemplatesList.SelectedItem = selected;
            PrintTemplatesList.ScrollIntoView(selected);
        }
        finally
        {
            _loadingPrintTemplate = false;
        }
    }

    private string GetSelectedTemplateType(string? fallback)
    {
        var selected = PrintTemplateTypeComboBox.SelectedValue as string;
        if (selected is "label" or "shipping" or "manufacturer_box_mark" or "custom")
        {
            return selected;
        }

        return fallback is "label" or "shipping" or "manufacturer_box_mark" or "custom"
            ? fallback
            : "label";
    }

    private void SelectTemplateFromAction(object sender)
    {
        if (sender is FrameworkElement { DataContext: PrintTemplateProfile template })
        {
            PrintTemplatesList.SelectedItem = template;
        }
    }

    private void OnEditPrintTemplateClick(object sender, RoutedEventArgs e)
    {
        SelectTemplateFromAction(sender);
        if (PrintTemplatesList.SelectedItem is not PrintTemplateProfile template)
        {
            PrintConfigStatusText.Text = "请先选择一个模板。";
            return;
        }

        ShowPrintTemplateEditor(template);
    }

    private void OnCopyPrintTemplateClick(object sender, RoutedEventArgs e)
    {
        SelectTemplateFromAction(sender);
        if (PrintTemplatesList.SelectedItem is not PrintTemplateProfile source)
        {
            PrintConfigStatusText.Text = "请先选择一个模板。";
            return;
        }

        var copy = new PrintTemplateProfile
        {
            Id = $"template_{Guid.NewGuid():N}",
            TemplateNumber = PrintTemplatePolicy.NextTemplateNumber(_printTemplates),
            Name = $"{source.Name} - 副本",
            SortOrder = _printTemplates.Count + 1,
            Type = source.Type,
            Printer = source.Printer,
            WidthMillimeters = source.WidthMillimeters,
            HeightMillimeters = source.HeightMillimeters,
            Orientation = source.Orientation,
            Mode = source.Mode,
            Copies = source.Copies,
            OffsetXMillimeters = source.OffsetXMillimeters,
            OffsetYMillimeters = source.OffsetYMillimeters,
            SafetyInsetMillimeters = source.SafetyInsetMillimeters,
            SkuQrPrefix = source.SkuQrPrefix,
            LabelQrPrefix = source.LabelQrPrefix,
            LayoutStyle = source.LayoutStyle,
            TextFontSizePoints = source.TextFontSizePoints,
            MaxDisplayLength = source.MaxDisplayLength,
        };
        _printTemplates = NormalizeTemplateSortOrders(_printTemplates.Append(copy));
        _runtime.SavePrintTemplates(_printTemplates);
        UpdateTemplateSortState();
        PrintTemplatesList.ItemsSource = _printTemplates;
        PrintTemplatesList.SelectedItem = copy;
        LoadLabelTemplates();
        ShowPrintTemplateEditor(copy);
        PrintConfigStatusText.Text = $"模板已复制，新模板编号：{copy.TemplateNumber}。";
    }

    private void OnDeletePrintTemplateClick(object sender, RoutedEventArgs e)
    {
        SelectTemplateFromAction(sender);
        if (PrintTemplatesList.SelectedItem is not PrintTemplateProfile template)
        {
            PrintConfigStatusText.Text = "请先选择一个模板。";
            return;
        }

        if (_printTemplates.Count <= 1)
        {
            PrintConfigStatusText.Text = "至少保留一个打印模板。";
            return;
        }

        if (MessageBox.Show($"确定删除模板“{template.DisplayName}”吗？", "删除打印模板", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var settings = _runtime.LoadEdgeSettings();
        _printTemplates = NormalizeTemplateSortOrders(
            _printTemplates.Where(item => !string.Equals(item.Id, template.Id, StringComparison.OrdinalIgnoreCase)));
        if (string.Equals(settings.PrintTemplate, template.Id, StringComparison.OrdinalIgnoreCase))
        {
            settings.PrintTemplate = _printTemplates[0].Id;
            _runtime.SaveEdgeSettings(settings);
        }

        _runtime.SavePrintTemplates(_printTemplates);
        UpdateTemplateSortState();
        PrintTemplatesList.ItemsSource = _printTemplates;
        PrintTemplatesList.SelectedIndex = 0;
        if (string.Equals(_editingPrintTemplateId, template.Id, StringComparison.OrdinalIgnoreCase))
        {
            ShowPrintTemplateList();
        }

        LoadLabelTemplates();
        PrintConfigStatusText.Text = "模板已删除。";
    }

    private void OnMovePrintTemplateUpClick(object sender, RoutedEventArgs e)
    {
        MovePrintTemplate(sender, -1);
    }

    private void OnMovePrintTemplateDownClick(object sender, RoutedEventArgs e)
    {
        MovePrintTemplate(sender, 1);
    }

    private void MovePrintTemplate(object sender, int offset)
    {
        if ((sender as FrameworkElement)?.DataContext is not PrintTemplateProfile template)
        {
            return;
        }

        List<PrintTemplateProfile> ordered = PrintTemplatePolicy.OrderTemplates(_printTemplates).ToList();
        int currentIndex = ordered.FindIndex(item => string.Equals(item.Id, template.Id, StringComparison.OrdinalIgnoreCase));
        int targetIndex = currentIndex + offset;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= ordered.Count)
        {
            return;
        }

        (ordered[currentIndex], ordered[targetIndex]) = (ordered[targetIndex], ordered[currentIndex]);
        _printTemplates = NormalizeTemplateSortOrders(ordered);
        _runtime.SavePrintTemplates(_printTemplates);
        UpdateTemplateSortState();
        PrintTemplatesList.ItemsSource = null;
        PrintTemplatesList.ItemsSource = _printTemplates;
        PrintTemplatesList.SelectedItem = template;
        PrintTemplatesList.ScrollIntoView(template);
        LoadLabelTemplates();
        PrintConfigStatusText.Text = $"模板“{template.DisplayName}”已{(offset < 0 ? "上移" : "下移")}，终端模板顺序已更新。";
    }

    private static IReadOnlyList<PrintTemplateProfile> NormalizeTemplateSortOrders(IEnumerable<PrintTemplateProfile> templates)
    {
        List<PrintTemplateProfile> ordered = PrintTemplatePolicy.OrderTemplates(templates).ToList();
        for (int index = 0; index < ordered.Count; index++)
        {
            ordered[index].SortOrder = index + 1;
        }
        return ordered;
    }

    private void UpdateTemplateSortState()
    {
        IReadOnlyList<PrintTemplateProfile> ordered = PrintTemplatePolicy.OrderTemplates(_printTemplates);
        for (int index = 0; index < ordered.Count; index++)
        {
            ordered[index].CanMoveUp = index > 0;
            ordered[index].CanMoveDown = index < ordered.Count - 1;
        }
        PrintTemplatesList.Items.Refresh();
    }

    private void OnSetDefaultPrintTemplateClick(object sender, RoutedEventArgs e)
    {
        SelectTemplateFromAction(sender);
        if (PrintTemplatesList.SelectedItem is not PrintTemplateProfile template)
        {
            PrintConfigStatusText.Text = "请先选择一个模板。";
            return;
        }

        var settings = _runtime.LoadEdgeSettings();
        settings.PrintTemplate = template.Id;
        _runtime.SaveEdgeSettings(settings);
        LoadLabelTemplates();
        PrintConfigStatusText.Text = $"已将“{template.DisplayName}”设为默认模板。";
    }

    private async void OnSearchProductsClick(object sender, RoutedEventArgs e)
    {
        await QueryProductsAsync(ProductKeywordTextBox.Text);
    }

    private async void OnProductSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ProductsGrid.SelectedItem is not ProductSummary product)
        {
            return;
        }

        _selectedProductId = product.Id;
        _selectedProductCode = product.Code;
        ProductQueryTextBox.Text = string.Empty;
        await QuerySkusAsync(string.Empty, 1, product.Id);
    }

    private async void OnSearchSkusClick(object sender, RoutedEventArgs e)
    {
        await QuerySkusAsync(SkuKeywordTextBox.Text);
    }

    private async void OnSkuModeClick(object sender, RoutedEventArgs e)
    {
        _selectedProductId = null;
        _selectedProductCode = string.Empty;
        await QuerySkusAsync(ProductQueryTextBox.Text, 1);
    }

    private void OnSkuGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsGridInteractiveTarget(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var row = FindVisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not SkuSummary sku)
        {
            return;
        }

        sku.IsSelected = !sku.IsSelected;
        row.IsSelected = sku.IsSelected;
        row.Focus();
        e.Handled = true;
    }

    private void OnCopySkuCodePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1 && sender is FrameworkElement { DataContext: SkuSummary sku })
        {
            CopyCodeToClipboard(sku.Code, "SKU 编码", ProductStatusText);
        }

        e.Handled = true;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static bool IsGridInteractiveTarget(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is CheckBox or Button || source is FrameworkElement { Tag: CopyCodeTargetTag })
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static void CopyCodeToClipboard(string? value, string description, TextBlock statusText)
    {
        var code = value?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            statusText.Text = $"{description}为空，无法复制。";
            return;
        }

        try
        {
            Clipboard.SetText(code);
            statusText.Text = $"已复制{description}：{code}";
        }
        catch (Exception)
        {
            statusText.Text = $"复制{description}失败，请稍后重试。";
        }
    }

    private async void OnProductModeClick(object sender, RoutedEventArgs e)
    {
        _selectedProductId = null;
        _selectedProductCode = string.Empty;
        await QueryProductsAsync(ProductQueryTextBox.Text, 1);
    }

    private async void OnQueryCurrentProductModeClick(object sender, RoutedEventArgs e)
    {
        if (_productQueryMode == "product")
        {
            await QueryProductsAsync(ProductQueryTextBox.Text, 1);
        }
        else
        {
            await QuerySkusAsync(ProductQueryTextBox.Text, 1, _selectedProductId);
        }
    }

    private void SetProductQueryMode(string mode)
    {
        _productQueryMode = mode;
        var isProduct = mode == "product";
        ProductsGrid.Visibility = isProduct ? Visibility.Visible : Visibility.Collapsed;
        SkusGrid.Visibility = isProduct ? Visibility.Collapsed : Visibility.Visible;
        ProductPrintActionsPanel.Visibility = isProduct ? Visibility.Collapsed : Visibility.Visible;
        ProductQueryButton.Content = isProduct ? "查询产品" : "查询 SKU";
        SkuModeButton.FontWeight = isProduct ? FontWeights.Normal : FontWeights.SemiBold;
        ProductModeButton.FontWeight = isProduct ? FontWeights.SemiBold : FontWeights.Normal;
        ProductQueryTextBox.ToolTip = isProduct ? "输入产品编码或名称" : "输入 SKU 编码、名称或规格";
        ProductStatusText.Text = isProduct ? "产品模式：查询产品后可查看其 SKU" : "SKU 模式：选择 SKU 后可直接创建二维码打印任务";
    }

    private async Task QueryProductsAsync(string keyword, int page = 1)
    {
        SetProductQueryMode("product");
        ProductQueryTextBox.Text = keyword;
        _selectedProductId = null;
        _selectedProductCode = string.Empty;
        ProductStatusText.Text = "正在查询产品...";
        SetProductQueryEnabled(false);
        var result = await _runtime.GetProductsPageAsync(keyword, page, ProductPageSize);
        SetProductQueryEnabled(true);
        if (result.Items.Count == 0 && page > 1)
        {
            ProductStatusText.Text = string.IsNullOrWhiteSpace(result.Message) ? "没有更多产品。" : result.Message;
            UpdateProductPagination(page - 1, result.Total, 0);
            return;
        }
        ProductsGrid.ItemsSource = result.Items;
        UpdateProductPagination(page, result.Total, result.Items.Count);
        ProductStatusText.Text = result.Items.Count > 0
            ? BuildProductResultMessage(result, "产品查询完成。选择产品可进一步查看 SKU。")
            : (string.IsNullOrWhiteSpace(result.Message) ? "没有查询到产品。" : result.Message);
        await RefreshCatalogStatusAsync();
    }

    private async Task QuerySkusAsync(string keyword, int page = 1, uint? productId = null)
    {
        SetProductQueryMode("sku");
        _selectedProductId = productId;
        if (productId is null)
        {
            _selectedProductCode = string.Empty;
        }
        ProductQueryTextBox.Text = keyword;
        ProductStatusText.Text = "正在查询 SKU...";
        SetProductQueryEnabled(false);
        var result = await _runtime.GetSkusPageAsync(keyword, page, ProductPageSize, productId);
        SetProductQueryEnabled(true);
        if (result.Items.Count == 0 && page > 1)
        {
            ProductStatusText.Text = string.IsNullOrWhiteSpace(result.Message) ? "没有更多 SKU。" : result.Message;
            UpdateProductPagination(page - 1, result.Total, 0);
            return;
        }
        _productSkus = result.Items;
        SkusGrid.ItemsSource = _productSkus;
        UpdateProductPagination(page, result.Total, result.Items.Count);
        var scope = productId is null ? string.Empty : $"产品 {_selectedProductCode} 下";
        ProductStatusText.Text = _productSkus.Count > 0
            ? BuildProductResultMessage(result, $"已加载{scope} {_productSkus.Count} 个 SKU，可勾选后打印二维码。")
            : (string.IsNullOrWhiteSpace(result.Message) ? $"没有查询到{scope} SKU。" : result.Message);
        await RefreshCatalogStatusAsync();
    }

    private static string BuildProductResultMessage<T>(CatalogSearchResult<T> result, string localMessage)
    {
        return string.Equals(result.Source, "center", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(result.Message)
            ? result.Message
            : localMessage;
    }

    private void SetProductQueryEnabled(bool enabled)
    {
        ProductQueryButton.IsEnabled = enabled;
        ProductPreviousPageButton.IsEnabled = enabled && _productPage > 1;
        ProductNextPageButton.IsEnabled = enabled && (_productPage * ProductPageSize < _productTotal || _productCurrentCount == ProductPageSize);
    }

    private void UpdateProductPagination(int page, long total, int currentCount)
    {
        _productPage = Math.Max(1, page);
        _productTotal = Math.Max(0, total);
        _productCurrentCount = Math.Max(0, currentCount);
        var pages = Math.Max(1L, (_productTotal + ProductPageSize - 1) / ProductPageSize);
        ProductPageText.Text = _productTotal > 0 ? $"第 {_productPage} / {pages} 页（{_productTotal} 条）" : $"第 {_productPage} 页";
        ProductPreviousPageButton.IsEnabled = _productPage > 1;
        ProductNextPageButton.IsEnabled = _productPage * ProductPageSize < _productTotal || _productCurrentCount == ProductPageSize;
    }

    private async void OnPreviousProductPageClick(object sender, RoutedEventArgs e)
    {
        await LoadProductModePageAsync(Math.Max(1, _productPage - 1));
    }

    private async void OnNextProductPageClick(object sender, RoutedEventArgs e)
    {
        await LoadProductModePageAsync(_productPage + 1);
    }

    private Task LoadProductModePageAsync(int page)
    {
        return _productQueryMode == "product"
            ? QueryProductsAsync(ProductQueryTextBox.Text, page)
            : QuerySkusAsync(ProductQueryTextBox.Text, page, _selectedProductId);
    }

    private async void OnRefreshCatalogClick(object sender, RoutedEventArgs e)
    {
        var started = await _runtime.RefreshCatalogAsync();
        CatalogStatusText.Text = started ? "目录正在刷新，当前查询继续使用已同步数据。" : "目录刷新未启动，请确认边缘服务已运行。";
    }

    private async Task RefreshCatalogStatusAsync()
    {
        var status = await _runtime.GetCatalogStatusAsync();
        if (status is null)
        {
            CatalogStatusText.Text = "本地目录不可用，查询将回退中心。";
            return;
        }

        if (!status.Ready)
        {
            CatalogStatusText.Text = status.State == "syncing" ? "本地目录正在首次同步。" : "本地目录等待首次同步。";
            return;
        }

        CatalogStatusText.Text = status.LastFullSyncAt is null
            ? $"本地目录版本 {status.Revision}。"
            : $"本地目录版本 {status.Revision}，全量同步 {status.LastFullSyncAt.Value.LocalDateTime:g}。";
        if (BoxLabelCatalogStatusText is not null && BoxLabelsGrid.ItemsSource is null)
        {
            BoxLabelCatalogStatusText.Text = status.BoxLabelsReady
                ? $"已离线同步 {status.BoxLabelCount} 个箱唛"
                : "箱唛历史数据正在首次同步";
        }
    }

    private void OnSaveQrPrefixClick(object sender, RoutedEventArgs e)
    {
        var prefix = SkuQrPrefixTextBox.Text.Trim();
        if (prefix.Length > 20)
        {
            MessageBox.Show("二维码前缀不能超过 20 个字符。", "产品管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = _runtime.LoadEdgeSettings();
        settings.SkuQrPrefix = prefix;
        _runtime.SaveEdgeSettings(settings);
        ProductStatusText.Text = $"二维码前缀已保存：{(string.IsNullOrWhiteSpace(prefix) ? "无前缀" : prefix)}";
    }

    private async void OnPrintSelectedSkusClick(object sender, RoutedEventArgs e)
    {
        var selected = SkusGrid.Items.OfType<SkuSummary>().Where(sku => sku.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先选择要打印的 SKU。", "产品管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ProductPrintTemplateComboBox.SelectedItem is not PrintTemplateProfile template || !_availableSkuPrintTemplates.Any(item => string.Equals(item.Id, template.Id, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("没有可用的标签模板。请在打印配置中检查打印机是否在线。", "产品管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string skuPrefix = string.IsNullOrWhiteSpace(template.SkuQrPrefix) ? "T" : template.SkuQrPrefix;
        SkuSummary? invalidSku = selected.FirstOrDefault(sku =>
            PrintTemplatePolicy.GetDisplayTextValidationError(template, skuPrefix + sku.Code) is not null);
        if (invalidSku is not null)
        {
            string message = PrintTemplatePolicy.GetDisplayTextValidationError(template, skuPrefix + invalidSku.Code)!;
            MessageBox.Show($"{message}\n异常内容：{skuPrefix}{invalidSku.Code}", "产品管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            ProductStatusText.Text = $"SKU {invalidSku.Code} 的标签文字超过 12 个字符，未创建打印任务。";
            return;
        }

        var printerStatus = _printerService.GetPrinter(template.Printer);
        if (printerStatus is not { IsAvailable: true })
        {
            MessageBox.Show(
                printerStatus is null
                    ? $"打印机 {template.Printer} 不存在或无法读取状态。"
                    : $"打印机 {template.Printer} 当前不可打印：{printerStatus.StatusText}。",
                "产品管理",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            RefreshPrinters();
            return;
        }

        var accepted = await _runtime.SubmitSkuPrintJobAsync(template.Id, selected);
        if (accepted)
        {
            ProductStatusText.Text = $"已创建 {selected.Count} 个 SKU 的打印任务，正在打印...";
            await RefreshPrintJobsAsync();
            ProductStatusText.Text = "打印任务已发送并开始处理，请在打印任务页面查看状态。";
        }
        else
        {
            ProductStatusText.Text = "打印任务创建失败，请检查连接、权限和打印模板配置。";
        }
    }

    private bool ApplyCacheInputs(EdgeSettings settings)
    {
        if (!int.TryParse(CacheMemoryTextBox.Text, out var memory) || memory is < 16 or > 4096 ||
            !int.TryParse(CacheObjectTextBox.Text, out var objectSize) || objectSize is < 1 or > 100 ||
            !int.TryParse(CacheStaleHoursTextBox.Text, out var staleHours) || staleHours is < 1 or > 168)
        {
            MessageBox.Show("缓存内存需为 16-4096 MB，单项上限需为 1-100 MB，过期兜底需为 1-168 小时。", "缓存配置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        settings.CacheMode = CacheModeComboBox.SelectedValue?.ToString() ?? "standard";
        settings.CacheMaxMemoryMegabytes = memory;
        settings.CacheMaxObjectMegabytes = objectSize;
        settings.CacheMaxStaleHours = staleHours;
        return true;
    }

    private bool ApplyNamespaceInput(EdgeSettings settings)
    {
        if (!uint.TryParse(NamespaceIdTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var namespaceId))
        {
            MessageBox.Show("工作空间 ID 必须是非负整数。", "远端配置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        settings.NamespaceId = namespaceId;
        return true;
    }

    private void RefreshPrinters(string? selectedPrinter = null)
    {
        ApplyPrinterSnapshot(_printerService.GetPrinters(), selectedPrinter);
    }

    private void ApplyPrinterSnapshot(IReadOnlyList<LocalPrinter> printers, string? selectedPrinter = null)
    {
        var currentPrinter = selectedPrinter ?? (PrinterComboBox.SelectedItem as LocalPrinter)?.Name;
        _localPrinters = printers;
        PrinterComboBox.ItemsSource = printers;

        var configuredPrinter = currentPrinter ?? ResolveEffectivePrinter(_runtime.LoadEdgeSettings());
        PrinterComboBox.SelectedItem = printers.FirstOrDefault(printer => string.Equals(printer.Name, configuredPrinter, StringComparison.OrdinalIgnoreCase))
            ?? printers.FirstOrDefault(printer => printer.IsDefault);

        PrintConfigStatusText.Text = printers.Count == 0
            ? "未发现 Windows 打印机。请检查打印机安装和当前用户权限。"
            : $"已识别 {printers.Count} 台打印机，可打印 {printers.Count(printer => printer.IsAvailable)} 台，不可打印 {printers.Count(printer => !printer.IsAvailable)} 台。";
        if (LabelPrinterText is not null)
        {
            var configuredStatus = FindPrinter(configuredPrinter);
            LabelPrinterText.Text = string.IsNullOrWhiteSpace(configuredPrinter)
                ? "默认打印机：未配置"
                : $"默认打印机：{configuredPrinter} · {configuredStatus?.StatusText ?? "状态未知"}";
        }

        UpdateTemplatePrinterStatuses();
        PrintTemplatesList.Items.Refresh();
        RefreshProductPrintTemplates();
        ApplyLabelTemplate(LabelTemplateComboBox.SelectedItem as PrintTemplateProfile);
    }

    private IReadOnlySet<string> AvailablePrinterNames()
    {
        return LocalPrinterService.AvailablePrinterNames(_localPrinters);
    }

    private LocalPrinter? FindPrinter(string? printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
        {
            return null;
        }

        return _localPrinters.FirstOrDefault(printer =>
            string.Equals(printer.Name, printerName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateTemplatePrinterStatuses()
    {
        var settings = _runtime.LoadEdgeSettings();
        foreach (var template in _printTemplates)
        {
            UpdateTemplatePrinterStatus(template, settings);
        }
        LabelTemplateTiles.Items.Refresh();
    }

    private void UpdateTemplatePrinterStatus(PrintTemplateProfile template, EdgeSettings settings)
    {
        bool usesDefault = string.IsNullOrWhiteSpace(template.Printer);
        string printerName = usesDefault ? ResolveEffectivePrinter(settings) : template.Printer.Trim();
        LocalPrinter? printer = FindPrinter(printerName);
        template.IsPrinterAvailable = printer is { IsAvailable: true };
        template.PrinterStatusText = string.IsNullOrWhiteSpace(printerName)
            ? "未配置"
            : $"{(usesDefault ? "默认 · " : string.Empty)}{printer?.StatusText ?? "状态未知"}";
    }

    private void LoadPrintSettingsIntoForm(EdgeSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DefaultPrinter))
        {
            settings.DefaultPrinter = ResolveEffectivePrinter(settings);
        }

        var selectedPrinter = PrinterComboBox.SelectedItem as LocalPrinter;
        if (selectedPrinter is null && !string.IsNullOrWhiteSpace(settings.DefaultPrinter))
        {
            selectedPrinter = (PrinterComboBox.ItemsSource as IEnumerable<LocalPrinter>)?.FirstOrDefault(
                printer => string.Equals(printer.Name, settings.DefaultPrinter, StringComparison.OrdinalIgnoreCase));
            PrinterComboBox.SelectedItem = selectedPrinter;
        }

        PrintSizePresetComboBox.ItemsSource = PrintSizePresets;
        PrintSizePresetComboBox.SelectedItem = PrintSizePresets.FirstOrDefault(preset =>
                string.Equals(preset.Id, settings.PrintTemplate, StringComparison.OrdinalIgnoreCase))
            ?? PrintSizePresets.First(preset => preset.Id == "custom");
        PrintWidthTextBox.Text = FormatMillimeters(settings.PrintWidthMillimeters);
        PrintHeightTextBox.Text = FormatMillimeters(settings.PrintHeightMillimeters);
        PrintOffsetXTextBox.Text = settings.PrintOffsetXMillimeters.ToString("0.##", CultureInfo.InvariantCulture);
        PrintOffsetYTextBox.Text = settings.PrintOffsetYMillimeters.ToString("0.##", CultureInfo.InvariantCulture);
        PrintSafetyInsetTextBox.Text = settings.PrintSafetyInsetMillimeters.ToString("0.##", CultureInfo.InvariantCulture);
        PrintCopiesTextBox.Text = settings.PrintCopies.ToString(CultureInfo.InvariantCulture);
    }

    private string ResolveEffectivePrinter(EdgeSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.DefaultPrinter))
        {
            return settings.DefaultPrinter.Trim();
        }

        var template = _runtime.LoadPrintTemplates()
            .FirstOrDefault(item => string.Equals(item.Id, settings.PrintTemplate, StringComparison.OrdinalIgnoreCase));
        return template?.Printer?.Trim() ?? string.Empty;
    }

    private bool TryReadPrintSettings(out EdgeSettings settings)
    {
        settings = _runtime.LoadEdgeSettings();
        var printer = PrinterComboBox.SelectedItem as LocalPrinter;
        if (printer is null)
        {
            ShowPrintValidation("请选择一台可用的本地打印机。");
            return false;
        }

        if (!TryParseMillimeters(PrintWidthTextBox.Text, out var width) || width is <= 0 or > 1000 ||
            !TryParseMillimeters(PrintHeightTextBox.Text, out var height) || height is <= 0 or > 1000)
        {
            ShowPrintValidation("宽度和高度必须是 0 到 1000 之间的毫米数。");
            return false;
        }

        if (!int.TryParse(PrintCopiesTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var copies) || !PrintJobDispatchPolicy.IsCopiesAllowed(copies))
        {
            ShowPrintValidation($"打印份数必须是 1 到 {PrintJobDispatchPolicy.MaxCopiesPerContent} 之间的整数。");
            return false;
        }

        if (!double.TryParse(PrintOffsetXTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var offsetX) || offsetX is < -5 or > 5)
        {
            ShowPrintValidation("水平校准必须是 -5 到 5 之间的毫米数。正数向右移动。");
            return false;
        }

        if (!double.TryParse(PrintOffsetYTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var offsetY) || offsetY is < -5 or > 5)
        {
            ShowPrintValidation("纵向校准必须是 -5 到 5 之间的毫米数。正数向下移动。");
            return false;
        }

        if (!double.TryParse(PrintSafetyInsetTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var safetyInset) || safetyInset is < 0.5 or > 5)
        {
            ShowPrintValidation("安全边距必须是 0.5 到 5 之间的毫米数。");
            return false;
        }

        settings.DefaultPrinter = printer.Name;
        settings.PrintTemplate = (PrintSizePresetComboBox.SelectedItem as PrintSizePreset)?.Id ?? "custom";
        settings.PrintWidthMillimeters = width;
        settings.PrintHeightMillimeters = height;
        settings.PrintOrientation = PrintTemplatePolicy.GetAutomaticOrientation(GetSelectedLayoutStyle());
        settings.PrintOffsetXMillimeters = offsetX;
        settings.PrintOffsetYMillimeters = offsetY;
        settings.PrintSafetyInsetMillimeters = safetyInset;
        settings.PrintMode = "fit";
        settings.PrintCopies = copies;
        return true;
    }

    private static bool TryParseMillimeters(string input, out double value)
    {
        return double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
            double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string FormatMillimeters(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static void ShowPrintValidation(string message)
    {
        MessageBox.Show(message, "打印配置", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ApplyApiTokenInput(EdgeSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(ApiTokenPasswordBox.Password))
        {
            settings.ApiToken = ApiTokenPasswordBox.Password.Trim();
        }
    }

    private void OnCopyLabelPrintUrlClick(object sender, RoutedEventArgs e)
    {
        var url = GetLabelPrintUrl();
        Clipboard.SetText(url);
        LocalStatusMessageText.Text = "Web 标签打印地址已复制到剪贴板。";
    }

    private async void OnSearchBoxLabelProductsClick(object sender, RoutedEventArgs e)
    {
        var products = await _runtime.GetProductsAsync(BoxLabelProductComboBox.Text);
        BoxLabelProductComboBox.ItemsSource = products;
        BoxLabelProductComboBox.IsDropDownOpen = products.Count > 0;
        BoxLabelCatalogStatusText.Text = products.Count > 0 ? $"找到 {products.Count} 个产品" : "没有找到产品";
    }

    private async void OnBoxLabelProductChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BoxLabelProductComboBox.SelectedItem is not ProductSummary product)
        {
            return;
        }

        var skus = await _runtime.GetSkusAsync(string.Empty, product.Id);
        BoxLabelSkuComboBox.ItemsSource = skus;
        BoxLabelSkuComboBox.SelectedItem = null;
        BoxLabelSkuComboBox.Text = string.Empty;
    }

    private async void OnSearchBoxLabelSkusClick(object sender, RoutedEventArgs e)
    {
        var productId = SelectedBoxLabelProductId();
        var skus = await _runtime.GetSkusAsync(BoxLabelSkuComboBox.Text, productId);
        BoxLabelSkuComboBox.ItemsSource = skus;
        BoxLabelSkuComboBox.IsDropDownOpen = skus.Count > 0;
        BoxLabelCatalogStatusText.Text = skus.Count > 0 ? $"找到 {skus.Count} 个 SKU" : "没有找到 SKU";
    }

    private async void OnSearchConsolidationOrdersClick(object sender, RoutedEventArgs e)
    {
        var keyword = BoxLabelConsolidationComboBox.Text.Trim();
        if (keyword.Length < 2)
        {
            BoxLabelCatalogStatusText.Text = "至少输入 2 个字符再在线查询集运订单。";
            return;
        }

        var orders = await _runtime.GetConsolidationOrdersAsync(keyword);
        BoxLabelConsolidationComboBox.ItemsSource = orders;
        BoxLabelConsolidationComboBox.IsDropDownOpen = orders.Count > 0;
        BoxLabelCatalogStatusText.Text = orders.Count > 0 ? $"在线找到 {orders.Count} 个集运订单" : "中心没有返回匹配的集运订单，可按输入编码查询本地数据";
    }

    private async void OnQueryBoxLabelsClick(object sender, RoutedEventArgs e)
    {
        await LoadBoxLabelsAsync(1);
    }

    private async void OnResetBoxLabelsClick(object sender, RoutedEventArgs e)
    {
        BoxLabelKeywordTextBox.Clear();
        BoxLabelProductComboBox.ItemsSource = null;
        BoxLabelProductComboBox.Text = string.Empty;
        BoxLabelSkuComboBox.ItemsSource = null;
        BoxLabelSkuComboBox.Text = string.Empty;
        BoxLabelConsolidationComboBox.ItemsSource = null;
        BoxLabelConsolidationComboBox.Text = string.Empty;
        BoxLabelStatusComboBox.SelectedIndex = 0;
        BoxLabelReceivingComboBox.SelectedIndex = 0;
        await LoadBoxLabelsAsync(1);
    }

    private async Task LoadBoxLabelsAsync(int page)
    {
        _boxLabelPage = Math.Max(1, page);
        BoxLabelCatalogStatusText.Text = "正在查询本地箱唛...";
        var selectedOrder = BoxLabelConsolidationComboBox.SelectedItem as ConsolidationOrderSummary;
        var result = await _runtime.GetBoxLabelsAsync(new BoxLabelQuery
        {
            Keyword = BoxLabelKeywordTextBox.Text,
            ProductId = SelectedBoxLabelProductId(),
            SkuId = SelectedBoxLabelSkuId(),
            ConsolidationOrderId = selectedOrder?.Id,
            ConsolidationOrderCode = selectedOrder?.Code ?? BoxLabelConsolidationComboBox.Text,
            StatusGroup = BoxLabelStatusComboBox.SelectedValue?.ToString() ?? string.Empty,
            ReceivingStatus = BoxLabelReceivingComboBox.SelectedValue?.ToString() ?? string.Empty,
            Page = _boxLabelPage,
            PageSize = 20,
        });
        _boxLabels = result.Items;
        _boxLabelTotal = result.Total;
        BoxLabelsGrid.ItemsSource = _boxLabels;
        BoxLabelResultText.Text = $"共 {_boxLabelTotal} 个箱唛，本页 {_boxLabels.Count} 个";
        var totalPages = Math.Max(1, (int)Math.Ceiling(_boxLabelTotal / 20d));
        BoxLabelPageText.Text = $"第 {_boxLabelPage} / {totalPages} 页";
        PreviousBoxLabelPageButton.IsEnabled = _boxLabelPage > 1;
        NextBoxLabelPageButton.IsEnabled = _boxLabelPage < totalPages;
        BoxLabelCatalogStatusText.Text = !string.IsNullOrWhiteSpace(result.Message)
            ? result.Message
            : result.Source == "center"
                ? "数据来源：中心后端"
                : _boxLabels.Count > 0
                    ? "数据来源：本地离线目录"
                    : "没有匹配的箱唛";
        BoxLabelDetailPanel.DataContext = null;
        await RefreshCatalogStatusAsync();
    }

    private uint? SelectedBoxLabelProductId()
    {
        return BoxLabelProductComboBox.SelectedItem is ProductSummary product &&
            string.Equals(BoxLabelProductComboBox.Text.Trim(), product.Code, StringComparison.OrdinalIgnoreCase)
            ? product.Id
            : null;
    }

    private uint? SelectedBoxLabelSkuId()
    {
        return BoxLabelSkuComboBox.SelectedItem is SkuSummary sku &&
            string.Equals(BoxLabelSkuComboBox.Text.Trim(), sku.Code, StringComparison.OrdinalIgnoreCase)
            ? sku.Id
            : null;
    }

    private async void OnPreviousBoxLabelPageClick(object sender, RoutedEventArgs e)
    {
        if (_boxLabelPage > 1)
        {
            await LoadBoxLabelsAsync(_boxLabelPage - 1);
        }
    }

    private async void OnNextBoxLabelPageClick(object sender, RoutedEventArgs e)
    {
        if (_boxLabelPage * 20 < _boxLabelTotal)
        {
            await LoadBoxLabelsAsync(_boxLabelPage + 1);
        }
    }

    private void OnBoxLabelGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsGridInteractiveTarget(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var row = FindVisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not BoxLabelSummary boxLabel)
        {
            return;
        }

        boxLabel.IsSelected = !boxLabel.IsSelected;
        row.IsSelected = boxLabel.IsSelected;
        row.Focus();
        e.Handled = true;
    }

    private void OnCopyBoxLabelCodePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1 && sender is FrameworkElement { DataContext: BoxLabelSummary boxLabel })
        {
            CopyCodeToClipboard(boxLabel.LabelCode, "箱唛码", BoxLabelCatalogStatusText);
        }

        e.Handled = true;
    }

    private async void OnBoxLabelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (BoxLabelsGrid.SelectedItem is not BoxLabelSummary selected)
        {
            return;
        }

        BoxLabelDetailPanel.DataContext = await _runtime.GetBoxLabelAsync(selected.Id) ?? selected;
    }

    private void OnPreviewSelectedBoxLabelsClick(object sender, RoutedEventArgs e)
    {
        List<BoxLabelSummary> selected = _boxLabels.Where(label => label.IsSelected).ToList();
        if (selected.Count == 0 && BoxLabelsGrid.SelectedItem is BoxLabelSummary current)
        {
            selected.Add(current);
        }

        if (selected.Count == 0)
        {
            MessageBox.Show("请先勾选或高亮要预览的箱唛。", "箱唛管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (selected.Count > 100 || selected.Any(label => label.PrintSnapshot is null))
        {
            MessageBox.Show("单次最多预览 100 个箱唛，且箱唛必须包含完整打印快照。", "箱唛管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (BoxLabelPrintTemplateComboBox.SelectedItem is not PrintTemplateProfile template)
        {
            MessageBox.Show("没有 100 × 150 mm 厂家箱唛模板，请先在打印配置中创建。", "箱唛管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            EdgeSettings settings = _runtime.LoadEdgeSettings();
            ApplyTemplateToSettings(settings, template);
            settings.DefaultPrinter = template.Printer;
            List<ManufacturerBoxMark> marks = ManufacturerBoxMarkPrintService.PrepareMarksForTemplate(template, selected).ToList();
            if (marks.Count > 100)
            {
                throw new InvalidOperationException("展开后的箱唛标签超过 100 页，请减少本次选择数量。");
            }
            FixedDocument document = new ManufacturerBoxMarkPrintService().CreatePreviewDocument(settings, template, marks, out string diagnostic);
            bool quadLayout = PrintTemplatePolicy.NormalizeLayoutStyle(template.LayoutStyle) == PrintTemplatePolicy.BoxMarkQuadLayoutStyle;
            PrintPreviewWindow preview = new(
                document,
                "厂家箱唛真实数据预览",
                $"{template.DisplayName} · {selected.Count} 个箱唛 / {marks.Count} 张标签 · 100 x 150 mm · 每页 {(quadLayout ? "2 BOX + 2 SKU" : "1 BOX + 2 SKU")} 二维码",
                diagnostic,
                showConfirmButton: false)
            {
                Owner = this,
            };
            preview.ShowDialog();
            BoxLabelCatalogStatusText.Text = $"已预览 {selected.Count} 个箱唛，共 {marks.Count} 张标签，未提交打印任务。";
        }
        catch (Exception ex)
        {
            BoxLabelCatalogStatusText.Text = $"箱唛预览失败：{ex.Message}";
            MessageBox.Show(ex.Message, "箱唛预览失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnPrintSelectedBoxLabelsClick(object sender, RoutedEventArgs e)
    {
        var selected = _boxLabels.Where(label => label.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先勾选要打印的箱唛。", "箱唛管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (selected.Count > 100 || selected.Any(label => !label.Printable || label.PrintSnapshot is null))
        {
            MessageBox.Show("单次最多打印 100 个箱唛，且作废、短装、已收货、破损或异常箱唛不能打印。", "箱唛管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (BoxLabelPrintTemplateComboBox.SelectedItem is not PrintTemplateProfile template)
        {
            MessageBox.Show("没有 100 × 150 mm 厂家箱唛模板，请先在打印配置中创建。", "箱唛管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(template.Printer))
        {
            MessageBox.Show("当前厂家箱唛模板未配置打印机，请点击模板旁的编辑按钮完成配置。", "箱唛管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var printerStatus = _printerService.GetPrinter(template.Printer);
        if (printerStatus is not { IsAvailable: true })
        {
            MessageBox.Show(
                printerStatus is null
                    ? $"打印机 {template.Printer} 不存在或无法读取状态。"
                    : $"打印机 {template.Printer} 当前不可打印：{printerStatus.StatusText}。",
                "箱唛管理",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            RefreshPrinters();
            return;
        }

        if (!int.TryParse(BoxLabelCopiesTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var copies) || !PrintJobDispatchPolicy.IsCopiesAllowed(copies))
        {
            MessageBox.Show($"打印份数必须是 1 到 {PrintJobDispatchPolicy.MaxCopiesPerContent} 的整数。", "箱唛管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int pageCount;
        try
        {
            pageCount = ManufacturerBoxMarkPrintService.PrepareMarksForTemplate(template, selected).Count;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "箱唛打印", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (pageCount > 100)
        {
            MessageBox.Show("展开后的箱唛标签超过 100 页，请减少本次选择数量。", "箱唛打印", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var accepted = await _runtime.SubmitBoxMarkPrintJobAsync(template, selected, copies);
        BoxLabelCatalogStatusText.Text = accepted ? $"已创建 {selected.Count} 个箱唛、{pageCount} 张标签的本地打印任务" : "打印任务创建失败，请检查模板、箱唛内容和打印机状态";
        if (accepted)
        {
            await RefreshPrintJobsAsync();
        }
    }

    private string GetLabelPrintUrl()
    {
        return _runtime.ResolveLanBaseUrl().TrimEnd('/') + "/edge/label-print";
    }

    private void RefreshLabelPrintUrl()
    {
        var url = GetLabelPrintUrl();
        HomeWebLabelUrlText.Text = url;

        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using PngByteQRCode renderer = new(data);
        using MemoryStream stream = new(renderer.GetGraphic(8));
        BitmapImage image = new();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        HomeWebLabelQrImage.Source = image;
    }

    private void LoadManifest()
    {
        var manifest = _runtime.LoadManifest();
        var version = string.IsNullOrWhiteSpace(manifest?.EdgeVersion) ? "-" : manifest.EdgeVersion;
        var commit = string.IsNullOrWhiteSpace(manifest?.EdgeCommit) ? "-" : manifest.EdgeCommit;
        var api = string.IsNullOrWhiteSpace(manifest?.EdgeApiVersion) ? "-" : manifest.EdgeApiVersion;

        HomeVersionText.Text = $"{version} / {api}";
        AdvancedVersionText.Text = $"版本：{version}";
        AdvancedCommitText.Text = $"Commit：{commit}";
        AdvancedApiText.Text = $"Edge API：{api}";
    }

    private void ApplyRuntimeStatus(EdgeRuntimeStatus status)
    {
        var badge = status.IsHealthy ? BadgeKind.Success : status.IsRunning ? BadgeKind.Warning : BadgeKind.Error;
        var headerText = status.IsHealthy ? "运行正常" : status.IsRunning ? "服务启动中" : "服务不可用";
        var localText = status.IsHealthy ? "健康运行" : status.IsRunning ? "启动中" : "不可用";
        SetBadge(HeaderStatusPill, HeaderStatusText, headerText, badge);
        HeaderStatusDot.Fill = HeaderStatusText.Foreground;
        HeaderStatusDetailText.Foreground = HeaderStatusText.Foreground;
        HeaderStatusDetailText.Text = status.IsHealthy
            ? $"健康检测通过 · {DateTimeOffset.Now:HH:mm:ss}"
            : status.IsRunning
                ? $"等待健康响应 · 端口 {status.Port}"
                : $"健康检测失败 · {DateTimeOffset.Now:HH:mm:ss}";
        SetBadge(LocalStatusBadge, LocalStatusText, localText, badge);
        LocalStatusMessageText.Text = status.IsHealthy
            ? $"本地服务已通过健康检查，端口 {status.Port} 正常响应。"
            : status.Message;

        AdvancedPortText.Text = status.Port.ToString();
        AdvancedProcessText.Text = status.ProcessId?.ToString() ?? "-";
        AdvancedBinaryText.Text = status.BinaryExists ? _runtime.BinaryPath : "缺少 fscm-edge.exe";
        AdvancedConfigText.Text = status.ConfigExists ? _runtime.ConfigPath : "缺少 edge.config.yaml";
        AdvancedRuntimePathText.Text = _runtime.RuntimeDirectory;
        AdvancedLogPathText.Text = _runtime.LogDirectory;
    }

    private void ApplyRemoteStatus(RemoteCenterStatus? status)
    {
        if (status is null || !status.IsConfigured)
        {
            SetBadge(RemoteStatusBadge, RemoteStatusText, "未配置", BadgeKind.Neutral);
            RemoteStatusMessageText.Text = status?.Message ?? "Remote center URL is not configured.";
            RemoteDetailText.Text = RemoteStatusMessageText.Text;
            RemoteCheckedText.Text = "最后检测：-";
            return;
        }

        SetBadge(
            RemoteStatusBadge,
            RemoteStatusText,
            status.IsReachable ? "已连接" : "连接失败",
            status.IsReachable ? BadgeKind.Success : BadgeKind.Error);
        RemoteStatusMessageText.Text = status.Message;
        RemoteDetailText.Text = status.Message;
        RemoteCheckedText.Text = $"最后检测：{status.CheckedAt:yyyy-MM-dd HH:mm:ss}";
    }

    private void ApplyRegistrationStatus(EdgeCenterRegistrationResult? registration)
    {
        if (registration is null || !registration.Attempted)
        {
            SetBadge(RegistrationStatusBadge, RegistrationStatusText, "未注册", BadgeKind.Neutral);
            RegistrationMessageText.Text = registration?.Message ?? "等待远端服务配置。";
            return;
        }

        SetBadge(
            RegistrationStatusBadge,
            RegistrationStatusText,
            registration.Succeeded ? "已注册" : "注册失败",
            registration.Succeeded ? BadgeKind.Success : BadgeKind.Error);
        RegistrationMessageText.Text = registration.Message;
    }

    private void ApplyPrintFilter()
    {
        if (PrintJobsGrid is null)
        {
            return;
        }

        var selected = (PrintStatusFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        PrintJobsGrid.ItemsSource = selected == "all"
            ? _allPrintJobs.ToList()
            : _allPrintJobs.Where(job => string.Equals(job.Status, selected, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void SetPage(string tag)
    {
        HomePage.Visibility = tag == "Home" ? Visibility.Visible : Visibility.Collapsed;
        RemotePage.Visibility = tag == "Advanced" ? Visibility.Visible : Visibility.Collapsed;
        TerminalsPage.Visibility = tag == "Terminals" ? Visibility.Visible : Visibility.Collapsed;
        PrintPage.Visibility = tag is "Print" or "PrintConfig" ? Visibility.Visible : Visibility.Collapsed;
        LabelPage.Visibility = tag == "LabelPrint" ? Visibility.Visible : Visibility.Collapsed;
        BatchPrintPage.Visibility = tag == "BatchPrint" ? Visibility.Visible : Visibility.Collapsed;
        ProductsPage.Visibility = tag == "Products" ? Visibility.Visible : Visibility.Collapsed;
        BoxLabelsPage.Visibility = tag == "BoxLabels" ? Visibility.Visible : Visibility.Collapsed;
        PrintJobsCard.Visibility = tag == "Print" ? Visibility.Visible : Visibility.Collapsed;
        PrintConfigCard.Visibility = tag == "PrintConfig" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPage.Visibility = tag == "Advanced" ? Visibility.Visible : Visibility.Collapsed;

        (PageTitleText.Text, PageSubtitleText.Text) = tag switch
        {
            "Terminals" => ("在线终端", "查看局域网内已探测到的手机或业务终端。"),
            "LabelPrint" => ("标签打印", "输入任意字符串，使用当前选择的通用标签模板打印二维码标签。"),
            "BatchPrint" => ("批量打印", "生成并预览不同标签内容，提交中心批次后由当前边缘节点依次打印。"),
            "Print" => ("打印服务", "优先查看本地打印任务，打印机和规格在打印配置中管理。"),
            "PrintConfig" => ("打印配置", "管理打印模板、打印机、尺寸和二维码打印参数。"),
            "Products" => ("产品管理", "本地优先分页查询产品和 SKU，缺失数据自动从中心同步。"),
            "BoxLabels" => ("箱唛管理", "离线查询箱唛关联单据，支持产品、SKU 和集运订单联合筛选及本地打印。"),
            "Advanced" => ("高级设置", "配置远端服务并管理边缘后端进程、运行文件和日志。"),
            _ => ("总览", "查看边缘节点最关键的运行状态。"),
        };

        if (tag == "BatchPrint")
        {
            _ = LoadBatchPrintPageAsync();
        }

        if (tag == "BoxLabels" && BoxLabelsGrid.ItemsSource is null)
        {
            _ = LoadBoxLabelsAsync(1);
        }
        if (tag == "Products" && SkusGrid.ItemsSource is null)
        {
            _selectedProductId = null;
            _selectedProductCode = string.Empty;
            _ = QuerySkusAsync(string.Empty, 1);
        }
    }

    private static void SetBadge(Border badge, TextBlock textBlock, string text, BadgeKind kind)
    {
        textBlock.Text = text;
        (var background, var foreground, var border) = kind switch
        {
            BadgeKind.Success => ("#DCFCE7", "#14532D", "#22C55E"),
            BadgeKind.Warning => ("#FEF3C7", "#78350F", "#F59E0B"),
            BadgeKind.Error => ("#FEE2E2", "#7F1D1D", "#EF4444"),
            _ => ("#E2E8F0", "#475569", "#94A3B8"),
        };
        badge.Background = BrushFrom(background);
        badge.BorderBrush = BrushFrom(border);
        badge.BorderThickness = new Thickness(1);
        textBlock.Foreground = BrushFrom(foreground);
    }

    private static System.Windows.Media.Brush BrushFrom(string color)
    {
        return (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(color)!;
    }

    private static void OpenLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, string.Empty);
        }

        OpenPath(path);
    }

    private static void OpenPath(string path)
    {
        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    private enum BadgeKind
    {
        Neutral,
        Success,
        Warning,
        Error,
    }
}
