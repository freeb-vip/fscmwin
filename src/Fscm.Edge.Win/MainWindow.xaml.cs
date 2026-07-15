// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fscm.Edge.Win.Models;
using Fscm.Edge.Win.Services;
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
    private readonly EdgeRuntimeManager _runtime = new();
    private readonly AppUpdateService _updates = new();
    private readonly LocalPrinterService _printerService = new();
    private readonly DispatcherTimer _timer;
    private readonly FormsNotifyIcon _notifyIcon;
    private readonly List<EdgePrintJob> _allPrintJobs = [];
    private IReadOnlyList<PrintTemplateProfile> _printTemplates = [];
    private IReadOnlyList<PrintTemplateProfile> _labelTemplates = [];
    private IReadOnlyList<PrintTemplateProfile> _availableSkuPrintTemplates = [];
    private IReadOnlyList<SkuSummary> _productSkus = [];
    private string _productQueryMode = "sku";
    private string? _editingPrintTemplateId;
    private bool _loadingPrintTemplate;
    private bool _loadingLabelTemplate;
    private bool _settingsFormLoaded;
    private int _printingJobs;
    private static readonly IReadOnlyList<PrintSizePreset> PrintSizePresets =
    [
        new() { Id = "label_60x40mm", Name = "标签 60 x 40 mm", WidthMillimeters = 60, HeightMillimeters = 40 },
        new() { Id = "shipping_100x150mm", Name = "面单 100 x 150 mm", WidthMillimeters = 100, HeightMillimeters = 150 },
        new() { Id = "label_150x100mm", Name = "标签 15 x 10 cm", WidthMillimeters = 150, HeightMillimeters = 100 },
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
    private bool _refreshing;
    private bool _checkingForUpdates;
    private bool _updatePromptShown;

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
        await RefreshAllAsync(forceRemoteCheck: true);
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
                : "主动领取失败，请确认边缘服务和中心连接正常。";
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

    private void OnRefreshPrintersClick(object sender, RoutedEventArgs e)
    {
        var selectedPrinter = (PrinterComboBox.SelectedItem as LocalPrinter)?.Name;
        RefreshPrinters(selectedPrinter);
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
        var labelPrefix = LabelPrefixTextBox.Text.Trim();
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
        settings.SkuQrPrefix = template.SkuQrPrefix;
        var effectivePrinter = string.IsNullOrWhiteSpace(template.Printer) ? ResolveEffectivePrinter(settings) : template.Printer.Trim();
        if (string.IsNullOrWhiteSpace(effectivePrinter))
        {
            LabelStatusText.Text = "尚未配置默认打印机，请先到打印配置中选择打印机。";
            return;
        }

        settings.DefaultPrinter = effectivePrinter;
        if (!string.Equals(template.LabelQrPrefix, labelPrefix, StringComparison.Ordinal))
        {
            template.LabelQrPrefix = labelPrefix;
            _runtime.SavePrintTemplates(_printTemplates);
        }

        LabelPrinterText.Text = $"当前打印机：{effectivePrinter}";
        LabelStatusText.Text = $"正在发送到 {effectivePrinter}...";
        try
        {
            await RunOnStaAsync(() => new QrPrintService().PrintLabel(settings, labelPrefix + text, text, template.MaxDisplayLength));
            LabelStatusText.Text = $"标签已发送，模板：{template.Name}。";
        }
        catch (Exception ex)
        {
            LabelStatusText.Text = $"标签打印失败：{ex.Message}";
        }
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

        try
        {
            _runtime.SaveEdgeSettings(settings);

            PrintPreviewWindow preview = new(settings) { Owner = this };
            if (preview.ShowDialog() != true)
            {
                PrintConfigStatusText.Text = "已取消打印，可返回修改纸张尺寸或打印机配置。";
                return;
            }

            _printerService.PrintTestPage(settings);
            PrintConfigStatusText.Text = $"测试任务已发送至 {settings.DefaultPrinter}。请检查打印机队列和出纸效果。";
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
        var status = await _runtime.StartAsync();
        ApplyRuntimeStatus(status);
        if (status.IsHealthy)
        {
            _lastRegistration = await _runtime.GetRuntimeRegistrationAsync();
            ApplyRegistrationStatus(_lastRegistration);
        }
    }

    private async Task RefreshAllAsync(bool forceRemoteCheck = false)
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
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
            var availablePrinters = _printerService.GetPrinters();
            await _runtime.SyncPrintInventoryAsync(availablePrinters.Select(printer => printer.Name));
            RefreshProductPrintTemplates();
            await RefreshTerminalsAsync();
            await RefreshPrintJobsAsync();
            await RefreshCacheStatusAsync();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshTerminalsAsync()
    {
        var terminals = await _runtime.GetTerminalsAsync();
        TerminalsGrid.ItemsSource = terminals;
        HomeTerminalCountText.Text = terminals.Count.ToString(CultureInfo.InvariantCulture);
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
                var started = await _runtime.UpdatePrintJobStatusAsync(job.Id, "printing");
                if (started is null)
                {
                    continue;
                }

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

                    settings.PrintTemplate = template.Id;
                    settings.PrintWidthMillimeters = template.WidthMillimeters;
                    settings.PrintHeightMillimeters = template.HeightMillimeters;
                    settings.PrintOrientation = template.Orientation;
                    settings.PrintMode = template.Mode;
                    settings.PrintCopies = Math.Max(1, template.Copies);
                    settings.PrintOffsetXMillimeters = template.OffsetXMillimeters;
                    settings.SkuQrPrefix = string.IsNullOrWhiteSpace(template.SkuQrPrefix) ? settings.SkuQrPrefix : template.SkuQrPrefix;

                    await RunOnStaAsync(() =>
                    {
                        if (string.Equals(started.Kind, "manufacturer_box_mark", StringComparison.OrdinalIgnoreCase))
                        {
                            new ManufacturerBoxMarkPrintService().Print(settings, started);
                        }
                        else if (string.Equals(started.Kind, "manual_text", StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrWhiteSpace(started.Text))
                            {
                                throw new InvalidOperationException("手工标签内容为空。");
                            }

                            settings.PrintCopies = Math.Max(1, started.Copies);
                            new QrPrintService().PrintLabel(settings, started.Text, started.Text, template.MaxDisplayLength);
                        }
                        else
                        {
                            new QrPrintService().Print(settings, started);
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
        _printTemplates = _runtime.LoadPrintTemplates();
        foreach (var template in _printTemplates)
        {
            NormalizeTemplateCompatibility(template);
        }

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
        _labelTemplates = _printTemplates.Where(template => string.Equals(template.Type, "label", StringComparison.OrdinalIgnoreCase)).ToList();
        if (_labelTemplates.Count == 0)
        {
            _labelTemplates =
            [
                new PrintTemplateProfile
                {
                    Id = "label_60x40mm",
                    Name = "标签 60 × 40 mm",
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

        _loadingLabelTemplate = true;
        LabelTemplateComboBox.ItemsSource = _labelTemplates;
        LabelTemplateComboBox.SelectedItem = _labelTemplates.FirstOrDefault(template => template.Id == "label_60x40mm")
            ?? _labelTemplates.FirstOrDefault();
        _loadingLabelTemplate = false;
        ApplyLabelTemplate(LabelTemplateComboBox.SelectedItem as PrintTemplateProfile);
        RefreshProductPrintTemplates();
    }

    private void RefreshProductPrintTemplates()
    {
        var availablePrinters = _printerService.GetPrinters()
            .Select(printer => printer.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _availableSkuPrintTemplates = _printTemplates
            .Where(template => string.Equals(template.Type, "label", StringComparison.OrdinalIgnoreCase))
            .Where(template => !string.IsNullOrWhiteSpace(template.Printer) && availablePrinters.Contains(template.Printer.Trim()))
            .ToList();

        var currentId = (ProductPrintTemplateComboBox.SelectedItem as PrintTemplateProfile)?.Id;
        var settings = _runtime.LoadEdgeSettings();
        var selected = _availableSkuPrintTemplates.FirstOrDefault(template => string.Equals(template.Id, currentId, StringComparison.OrdinalIgnoreCase))
            ?? _availableSkuPrintTemplates.FirstOrDefault(template => string.Equals(template.Id, "label_60x40mm", StringComparison.OrdinalIgnoreCase))
            ?? _availableSkuPrintTemplates.FirstOrDefault(template => string.Equals(template.Id, settings.PrintTemplate, StringComparison.OrdinalIgnoreCase))
            ?? _availableSkuPrintTemplates.FirstOrDefault();

        ProductPrintTemplateComboBox.ItemsSource = _availableSkuPrintTemplates;
        ProductPrintTemplateComboBox.SelectedItem = selected;
        ProductPrintTemplateText.Text = selected is null
            ? "没有可用标签模板"
            : $"标签模板：{selected.Name}";
        ProductPrintButton.IsEnabled = selected is not null;
        ChangeProductPrintTemplateButton.IsEnabled = _availableSkuPrintTemplates.Count > 1;
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

        ProductPrintTemplateText.Text = $"标签模板：{template.Name}";
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
        if (template is null)
        {
            LabelTemplateSummaryText.Text = "未选择模板";
            LabelModeText.Text = "请在打印配置中新增标签模板";
            LabelPrinterText.Text = "默认打印机：-";
            LabelPaperText.Text = "-";
            LabelOrientationText.Text = "-";
            LabelPrefixTextBox.Text = string.Empty;
            return;
        }

        NormalizeTemplateCompatibility(template);
        var settings = _runtime.LoadEdgeSettings();
        var printer = string.IsNullOrWhiteSpace(template.Printer) ? ResolveEffectivePrinter(settings) : template.Printer;
        LabelTemplateSummaryText.Text = template.Name;
        LabelModeText.Text = $"{template.WidthMillimeters:0.##} × {template.HeightMillimeters:0.##} mm · {GetOrientationText(template.Orientation)}";
        LabelPrinterText.Text = string.IsNullOrWhiteSpace(printer) ? "默认打印机：未配置" : $"当前打印机：{printer}";
        LabelPaperText.Text = $"{template.WidthMillimeters:0.##} × {template.HeightMillimeters:0.##} mm";
        LabelOrientationText.Text = GetOrientationText(template.Orientation);
        LabelPrefixTextBox.Text = template.LabelQrPrefix;
    }

    private static string GetOrientationText(string orientation)
    {
        return string.Equals(orientation, "landscape", StringComparison.OrdinalIgnoreCase) ? "横向" : "纵向";
    }

    private static void NormalizeTemplateCompatibility(PrintTemplateProfile template)
    {
        template.Type = NormalizeTemplateType(template);
        template.MaxDisplayLength = template.MaxDisplayLength > 0 ? template.MaxDisplayLength : 16;
        template.Copies = Math.Clamp(template.Copies, 1, 99);
        template.Mode = template.Mode is "actual_size" or "fill" ? template.Mode : "fit";
        template.Orientation = string.Equals(template.Orientation, "landscape", StringComparison.OrdinalIgnoreCase) ? "landscape" : "portrait";
    }

    private static string NormalizeTemplateType(PrintTemplateProfile template)
    {
        // Earlier builds persisted the built-in profiles as "custom". Migrate
        // those stable IDs once, without inferring a purpose from paper size.
        if (string.Equals(template.Id, "label_60x40mm", StringComparison.OrdinalIgnoreCase))
        {
            return "label";
        }

        if (string.Equals(template.Id, "shipping_100x150mm", StringComparison.OrdinalIgnoreCase))
        {
            return "shipping";
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

        ApplyPrintTemplateToEditor(template);
        PrintConfigStatusText.Text = $"已选择模板：{template.Name}。点击操作列中的编辑才会展开配置。";
    }

    private void ApplyPrintTemplateToEditor(PrintTemplateProfile template)
    {
        NormalizeTemplateCompatibility(template);
        PrinterComboBox.SelectedItem = (PrinterComboBox.ItemsSource as IEnumerable<LocalPrinter>)?.FirstOrDefault(
            printer => string.Equals(printer.Name, template.Printer, StringComparison.OrdinalIgnoreCase));
        PrintWidthTextBox.Text = FormatMillimeters(template.WidthMillimeters);
        PrintHeightTextBox.Text = FormatMillimeters(template.HeightMillimeters);
        PrintOrientationComboBox.SelectedValue = template.Orientation;
        PrintTemplateTypeComboBox.SelectedValue = template.Type;
        SkuQrPrefixTextBox.Text = string.IsNullOrWhiteSpace(template.SkuQrPrefix) ? "T" : template.SkuQrPrefix;
        PrintOffsetXTextBox.Text = template.OffsetXMillimeters.ToString("0.##", CultureInfo.InvariantCulture);
        PrintModeComboBox.SelectedValue = template.Mode;
        PrintCopiesTextBox.Text = template.Copies.ToString(CultureInfo.InvariantCulture);
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
        var profile = new PrintTemplateProfile
        {
            Id = existing?.Id ?? $"template_{Guid.NewGuid():N}",
            Name = name,
            Type = GetSelectedTemplateType(existing?.Type),
            Printer = settings.DefaultPrinter,
            WidthMillimeters = settings.PrintWidthMillimeters,
            HeightMillimeters = settings.PrintHeightMillimeters,
            Orientation = settings.PrintOrientation,
            Mode = settings.PrintMode,
            Copies = settings.PrintCopies,
            OffsetXMillimeters = settings.PrintOffsetXMillimeters,
            SkuQrPrefix = settings.SkuQrPrefix,
            LabelQrPrefix = existing?.LabelQrPrefix ?? string.Empty,
            MaxDisplayLength = existing?.MaxDisplayLength > 0 ? existing.MaxDisplayLength : 16,
        };

        var templates = _printTemplates
            .Where(template => !string.Equals(template.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
            .Append(profile)
            .ToList();
        _runtime.SavePrintTemplates(templates);
        _printTemplates = templates;
        PrintTemplatesList.ItemsSource = _printTemplates;
        PrintTemplatesList.SelectedItem = profile;
        _editingPrintTemplateId = profile.Id;
        PrintTemplateIdTextBox.Text = profile.Id;
        LoadLabelTemplates();
        PrintConfigStatusText.Text = $"模板“{name}”已保存，可供手机终端选择。";
    }

    private void OnNewPrintTemplateClick(object sender, RoutedEventArgs e)
    {
        ShowPrintTemplateEditor(null);
    }

    private void OnCancelPrintTemplateEditClick(object sender, RoutedEventArgs e)
    {
        _editingPrintTemplateId = null;
        PrintTemplateEditorCard.Visibility = Visibility.Collapsed;
        PrintConfigStatusText.Text = "已关闭模板编辑。";
    }

    private void ShowPrintTemplateEditor(PrintTemplateProfile? template)
    {
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
                PrintTemplateIdTextBox.Text = "保存后自动生成";
                PrintTemplateTypeComboBox.SelectedValue = "label";
                PrintWidthTextBox.Text = "60";
                PrintHeightTextBox.Text = "40";
                PrintOrientationComboBox.SelectedValue = "portrait";
                PrintModeComboBox.SelectedValue = "fit";
                PrintCopiesTextBox.Text = "1";
                PrintOffsetXTextBox.Text = "0";
                SkuQrPrefixTextBox.Text = "T";
            }
            else
            {
                NormalizeTemplateCompatibility(template);
                _editingPrintTemplateId = template.Id;
                PrintTemplatesList.SelectedItem = template;
                PrintTemplateEditorTitleText.Text = "编辑打印模板";
                PrintTemplateEditorSubtitleText.Text = $"模板 ID：{template.Id}";
                PrintTemplateNameTextBox.Text = template.Name;
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
            : $"正在编辑模板：{template.Name}。保存后保持原 ID。";
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
            Name = $"{source.Name} - 副本",
            Type = source.Type,
            Printer = source.Printer,
            WidthMillimeters = source.WidthMillimeters,
            HeightMillimeters = source.HeightMillimeters,
            Orientation = source.Orientation,
            Mode = source.Mode,
            Copies = source.Copies,
            OffsetXMillimeters = source.OffsetXMillimeters,
            SkuQrPrefix = source.SkuQrPrefix,
            LabelQrPrefix = source.LabelQrPrefix,
            MaxDisplayLength = source.MaxDisplayLength,
        };
        _printTemplates = _printTemplates.Append(copy).ToList();
        _runtime.SavePrintTemplates(_printTemplates);
        PrintTemplatesList.ItemsSource = _printTemplates;
        PrintTemplatesList.SelectedItem = copy;
        LoadLabelTemplates();
        ShowPrintTemplateEditor(copy);
        PrintConfigStatusText.Text = $"模板已复制，新模板 ID：{copy.Id}";
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

        if (MessageBox.Show($"确定删除模板“{template.Name}”吗？", "删除打印模板", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var settings = _runtime.LoadEdgeSettings();
        _printTemplates = _printTemplates.Where(item => !string.Equals(item.Id, template.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (string.Equals(settings.PrintTemplate, template.Id, StringComparison.OrdinalIgnoreCase))
        {
            settings.PrintTemplate = _printTemplates[0].Id;
            _runtime.SaveEdgeSettings(settings);
        }

        _runtime.SavePrintTemplates(_printTemplates);
        PrintTemplatesList.ItemsSource = _printTemplates;
        PrintTemplatesList.SelectedIndex = 0;
        if (string.Equals(_editingPrintTemplateId, template.Id, StringComparison.OrdinalIgnoreCase))
        {
            _editingPrintTemplateId = null;
            PrintTemplateEditorCard.Visibility = Visibility.Collapsed;
        }

        LoadLabelTemplates();
        PrintConfigStatusText.Text = "模板已删除。";
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
        PrintConfigStatusText.Text = $"已将“{template.Name}”设为默认模板。";
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

        SetProductQueryMode("sku");
        ProductQueryTextBox.Text = product.Code;
        ProductStatusText.Text = "正在查询产品 SKU...";
        _productSkus = await _runtime.GetSkusAsync(string.Empty, product.Id);
        SkusGrid.ItemsSource = _productSkus;
        ProductStatusText.Text = $"已加载 {_productSkus.Count} 个 SKU。";
    }

    private async void OnSearchSkusClick(object sender, RoutedEventArgs e)
    {
        await QuerySkusAsync(SkuKeywordTextBox.Text);
    }

    private void OnSkuModeClick(object sender, RoutedEventArgs e)
    {
        SetProductQueryMode("sku");
    }

    private void OnProductModeClick(object sender, RoutedEventArgs e)
    {
        SetProductQueryMode("product");
    }

    private async void OnQueryCurrentProductModeClick(object sender, RoutedEventArgs e)
    {
        if (_productQueryMode == "product")
        {
            await QueryProductsAsync(ProductQueryTextBox.Text);
        }
        else
        {
            await QuerySkusAsync(ProductQueryTextBox.Text);
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

    private async Task QueryProductsAsync(string keyword)
    {
        SetProductQueryMode("product");
        ProductQueryTextBox.Text = keyword;
        ProductStatusText.Text = "正在查询产品...";
        var products = await _runtime.GetProductsAsync(keyword);
        ProductsGrid.ItemsSource = products;
        ProductStatusText.Text = products.Count > 0
            ? "产品查询完成。选择产品可进一步查看 SKU。"
            : (_runtime.LastCenterQueryMessage.Length > 0 ? _runtime.LastCenterQueryMessage : "没有查询到产品。");
        await RefreshCatalogStatusAsync();
    }

    private async Task QuerySkusAsync(string keyword)
    {
        SetProductQueryMode("sku");
        ProductQueryTextBox.Text = keyword;
        ProductStatusText.Text = "正在查询 SKU...";
        _productSkus = await _runtime.GetSkusAsync(keyword);
        SkusGrid.ItemsSource = _productSkus;
        ProductStatusText.Text = _productSkus.Count > 0
            ? $"已加载 {_productSkus.Count} 个 SKU，可勾选后打印二维码。"
            : (_runtime.LastCenterQueryMessage.Length > 0 ? _runtime.LastCenterQueryMessage : "没有查询到 SKU。");
        await RefreshCatalogStatusAsync();
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
        var printers = _printerService.GetPrinters();
        PrinterComboBox.ItemsSource = printers;

        var configuredPrinter = selectedPrinter ?? ResolveEffectivePrinter(_runtime.LoadEdgeSettings());
        PrinterComboBox.SelectedItem = printers.FirstOrDefault(printer => string.Equals(printer.Name, configuredPrinter, StringComparison.OrdinalIgnoreCase))
            ?? printers.FirstOrDefault(printer => printer.IsDefault);

        PrintConfigStatusText.Text = printers.Count == 0
            ? "未发现可用的 Windows 打印机。请检查打印机安装、连接和当前用户权限。"
            : $"已识别 {printers.Count} 台本地或已连接打印机。";
        if (LabelPrinterText is not null)
        {
            LabelPrinterText.Text = string.IsNullOrWhiteSpace(configuredPrinter)
                ? "默认打印机：未配置"
                : $"默认打印机：{configuredPrinter}";
        }
        RefreshProductPrintTemplates();
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
        PrintOrientationComboBox.SelectedValue = settings.PrintOrientation;
        PrintOffsetXTextBox.Text = settings.PrintOffsetXMillimeters.ToString("0.##", CultureInfo.InvariantCulture);
        PrintModeComboBox.SelectedValue = settings.PrintMode;
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

        if (!int.TryParse(PrintCopiesTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var copies) || copies is < 1 or > 99)
        {
            ShowPrintValidation("打印份数必须是 1 到 99 之间的整数。");
            return false;
        }

        if (!double.TryParse(PrintOffsetXTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var offsetX) || offsetX is < -50 or > 50)
        {
            ShowPrintValidation("水平校准必须是 -50 到 50 之间的毫米数。正数向右移动。");
            return false;
        }

        settings.DefaultPrinter = printer.Name;
        settings.PrintTemplate = (PrintSizePresetComboBox.SelectedItem as PrintSizePreset)?.Id ?? "custom";
        settings.PrintWidthMillimeters = width;
        settings.PrintHeightMillimeters = height;
        settings.PrintOrientation = PrintOrientationComboBox.SelectedValue?.ToString() ?? "portrait";
        settings.PrintOffsetXMillimeters = offsetX;
        settings.PrintMode = PrintModeComboBox.SelectedValue?.ToString() ?? "fit";
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
        var text = status.IsHealthy ? "可用" : status.IsRunning ? "启动中" : "不可用";
        SetBadge(HeaderStatusPill, HeaderStatusText, text, badge);
        SetBadge(LocalStatusBadge, LocalStatusText, text, badge);
        LocalStatusMessageText.Text = status.Message;

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
        ProductsPage.Visibility = tag == "Products" ? Visibility.Visible : Visibility.Collapsed;
        PrintJobsCard.Visibility = tag == "Print" ? Visibility.Visible : Visibility.Collapsed;
        PrintConfigCard.Visibility = tag == "PrintConfig" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPage.Visibility = tag == "Advanced" ? Visibility.Visible : Visibility.Collapsed;

        (PageTitleText.Text, PageSubtitleText.Text) = tag switch
        {
            "Terminals" => ("在线终端", "查看局域网内已探测到的手机或业务终端。"),
            "LabelPrint" => ("标签打印", "输入任意字符串，使用当前选择的通用标签模板打印二维码标签。"),
            "Print" => ("打印服务", "优先查看本地打印任务，打印机和规格在打印配置中管理。"),
            "PrintConfig" => ("打印配置", "管理打印模板、打印机、尺寸和二维码打印参数。"),
            "Products" => ("产品管理", "查询中心产品和 SKU，选择模板后创建二维码打印任务。"),
            "Advanced" => ("高级设置", "配置远端服务并管理边缘后端进程、运行文件和日志。"),
            _ => ("总览", "查看边缘节点最关键的运行状态。"),
        };
    }

    private static void SetBadge(Border badge, TextBlock textBlock, string text, BadgeKind kind)
    {
        textBlock.Text = text;
        (var background, var foreground) = kind switch
        {
            BadgeKind.Success => ("#DCFCE7", "#166534"),
            BadgeKind.Warning => ("#FEF3C7", "#92400E"),
            BadgeKind.Error => ("#FEE2E2", "#991B1B"),
            _ => ("#E2E8F0", "#475569"),
        };
        badge.Background = BrushFrom(background);
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
