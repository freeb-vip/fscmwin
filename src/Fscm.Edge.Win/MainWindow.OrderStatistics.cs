using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Fscm.Edge.Win.Models;
using Fscm.Edge.Win.Services;

namespace Fscm.Edge.Win;

public partial class MainWindow
{
    private const int OrderStatisticsPageSize = 20;
    private readonly List<OrderStatisticsOperationType> _orderStatisticsOperationTypes = [];
    private readonly List<OrderStatisticsUserOption> _orderStatisticsUsers = [];
    private CancellationTokenSource? _orderStatisticsCancellation;
    private OrderStatisticsCenterClient? _orderStatisticsClient;
    private ScrollViewer? _orderStatisticsPage;
    private DatePicker? _orderStatisticsFromDate;
    private DatePicker? _orderStatisticsToDate;
    private ListBox? _orderStatisticsUsersList;
    private ListBox? _orderStatisticsOperationsList;
    private TextBox? _orderStatisticsUserSearch;
    private TextBlock? _orderStatisticsStatus;
    private TextBlock? _orderStatisticsAccepted;
    private TextBlock? _orderStatisticsTotalScans;
    private TextBlock? _orderStatisticsAverage;
    private TextBlock? _orderStatisticsMarkedError;
    private TextBlock? _orderStatisticsErrorRate;
    private DataGrid? _orderStatisticsOperationsGrid;
    private DataGrid? _orderStatisticsGroupsGrid;
    private DataGrid? _orderStatisticsRecordsGrid;
    private TextBlock? _orderStatisticsRecordsEmpty;
    private TabControl? _orderStatisticsRecordTabs;
    private Button? _orderStatisticsRefreshButton;
    private Button? _orderStatisticsPreviousButton;
    private Button? _orderStatisticsNextButton;
    private TextBlock? _orderStatisticsPagination;
    private int _orderStatisticsCurrentPage = 1;
    private long _orderStatisticsTotal;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += OnOrderStatisticsWindowLoaded;
        Closing += (_, _) =>
        {
            _orderStatisticsCancellation?.Cancel();
            StopWorkStatisticsAutoRefresh();
        };
    }

    private void OnOrderStatisticsWindowLoaded(object sender, RoutedEventArgs e)
    {
        _orderStatisticsClient = new OrderStatisticsCenterClient(_runtime);
        CreateOrderStatisticsPage();
        NavigationList.SelectionChanged += OnOrderStatisticsNavigationChanged;
    }

    private void OnOrderStatisticsNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_orderStatisticsPage is null)
        {
            return;
        }

        string tag = (NavigationList.SelectedItem as ListBoxItem)?.Tag?.ToString() ?? string.Empty;
        _orderStatisticsPage.Visibility = tag == "OrderStatistics" ? Visibility.Visible : Visibility.Collapsed;
        if (tag != "OrderStatistics")
        {
            return;
        }

        PageTitleText.Text = "订单统计";
        PageSubtitleText.Text = "按时间、作业人员和业务类型查看中心订单扫码统计。";
        _ = OpenOrderStatisticsPageAsync();
    }

    private async Task OpenOrderStatisticsPageAsync()
    {
        if (_orderStatisticsOperationTypes.Count == 0 && _orderStatisticsUsers.Count == 0)
        {
            await LoadOrderStatisticsFiltersAsync();
        }

        await RefreshOrderStatisticsAsync(resetPage: true);
    }

    private void CreateOrderStatisticsPage()
    {
        if (_orderStatisticsPage is not null || Content is not Grid root)
        {
            return;
        }

        Grid? host = root.Children.OfType<Grid>().FirstOrDefault(grid => Grid.GetColumn(grid) == 1);
        if (host is null)
        {
            return;
        }

        NavigationList.Items.Add(new ListBoxItem { Tag = "OrderStatistics", Content = "订单统计" });

        var content = new StackPanel { Margin = new Thickness(28) };
        _orderStatisticsPage = new ScrollViewer
        {
            Visibility = Visibility.Collapsed,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content,
        };
        Grid.SetRow(_orderStatisticsPage, 1);
        host.Children.Add(_orderStatisticsPage);

        content.Children.Add(CreateOrderStatisticsFilters());
        _orderStatisticsStatus = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 14),
            Foreground = Brush("#64748B"),
            Text = "请选择筛选条件后查询。",
            TextWrapping = TextWrapping.Wrap,
        };
        content.Children.Add(_orderStatisticsStatus);

        content.Children.Add(CreateOrderStatisticsMetrics());
        content.Children.Add(CreateOrderStatisticsSummaryGrids());
        content.Children.Add(CreateOrderStatisticsRecords());
    }

    private UIElement CreateOrderStatisticsFilters()
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        DateTime today = DateTime.Today;
        _orderStatisticsFromDate = new DatePicker { SelectedDate = today, Width = 130 };
        _orderStatisticsToDate = new DatePicker { SelectedDate = today, Width = 130 };
        _orderStatisticsUserSearch = new TextBox { Width = 142, MinWidth = 142 };
        _orderStatisticsUsersList = new ListBox { Width = 210, Height = 82, SelectionMode = SelectionMode.Multiple, DisplayMemberPath = "Display" };
        _orderStatisticsOperationsList = new ListBox { Width = 210, Height = 82, SelectionMode = SelectionMode.Multiple, DisplayMemberPath = "Display" };

        panel.Children.Add(FilterBlock("开始日期", _orderStatisticsFromDate));
        panel.Children.Add(FilterBlock("结束日期", _orderStatisticsToDate));
        panel.Children.Add(FilterBlock("人员搜索", CreateUserSearchBlock()));
        panel.Children.Add(FilterBlock("作业人员（不选即全部）", _orderStatisticsUsersList));
        panel.Children.Add(FilterBlock("业务类型（不选即全部）", _orderStatisticsOperationsList));

        var buttons = new StackPanel { Margin = new Thickness(0, 20, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        _orderStatisticsRefreshButton = new Button { Content = "查询", MinWidth = 76, Style = FindResource("PrimaryButtonStyle") as Style };
        _orderStatisticsRefreshButton.Click += async (_, _) => await RefreshOrderStatisticsAsync(resetPage: true);
        buttons.Children.Add(_orderStatisticsRefreshButton);
        var clearButton = new Button { Content = "清空筛选", MinWidth = 76, Margin = new Thickness(0, 8, 0, 0) };
        clearButton.Click += (_, _) => ClearOrderStatisticsFilters();
        buttons.Children.Add(clearButton);
        panel.Children.Add(buttons);
        return panel;
    }

    private UIElement CreateUserSearchBlock()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_orderStatisticsUserSearch!);
        var button = new Button { Content = "查找", Margin = new Thickness(6, 0, 0, 0) };
        button.Click += async (_, _) => await SearchOrderStatisticsUsersAsync();
        panel.Children.Add(button);
        return panel;
    }

    private UIElement CreateOrderStatisticsMetrics()
    {
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 18) };
        _orderStatisticsAccepted = AddMetric(grid, "已完成单量", "-", "#DCFCE7", "#166534");
        _orderStatisticsTotalScans = AddMetric(grid, "总扫码量", "-", "#E0E7FF", "#3730A3");
        _orderStatisticsAverage = AddMetric(grid, "日均单量", "-", "#DBEAFE", "#1D4ED8");
        _orderStatisticsMarkedError = AddMetric(grid, "标错单量", "-", "#FEF3C7", "#B45309");
        _orderStatisticsErrorRate = AddMetric(grid, "出错率", "-", "#FEE2E2", "#B91C1C");
        return grid;
    }

    private UIElement CreateOrderStatisticsSummaryGrids()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });

        _orderStatisticsOperationsGrid = CreateGrid();
        AddColumns(_orderStatisticsOperationsGrid,
            ("业务类型", "OperationDisplay", "*"),
            ("完成量", "AcceptedCount", "80"),
            ("标错", "MarkedErrorCount", "70"),
            ("出错率", "ErrorRateDisplay", "85"));
        Border operations = CreateSection("各业务类型处理量", _orderStatisticsOperationsGrid);
        Grid.SetColumn(operations, 0);
        grid.Children.Add(operations);

        _orderStatisticsGroupsGrid = CreateGrid();
        AddColumns(_orderStatisticsGroupsGrid,
            ("作业人员", "UserDisplay", "130"),
            ("业务类型", "OperationDisplay", "*"),
            ("完成量", "AcceptedCount", "80"),
            ("标错", "MarkedErrorCount", "70"),
            ("出错率", "ErrorRateDisplay", "85"));
        Border groups = CreateSection("人员与业务类型统计", _orderStatisticsGroupsGrid);
        Grid.SetColumn(groups, 2);
        grid.Children.Add(groups);
        return grid;
    }

    private UIElement CreateOrderStatisticsRecords()
    {
        var stack = new StackPanel();
        _orderStatisticsRecordTabs = new TabControl { Margin = new Thickness(0, 0, 0, 10) };
        _orderStatisticsRecordTabs.Items.Add(new TabItem { Header = "扫描明细" });
        _orderStatisticsRecordTabs.Items.Add(new TabItem { Header = "扫描记录" });
        _orderStatisticsRecordTabs.SelectionChanged += async (_, e) =>
        {
            if (e.Source == _orderStatisticsRecordTabs)
            {
                await RefreshOrderStatisticsAsync(resetPage: true);
            }
        };
        stack.Children.Add(_orderStatisticsRecordTabs);

        _orderStatisticsRecordsEmpty = new TextBlock { Margin = new Thickness(0, 0, 0, 8), Foreground = Brush("#64748B"), Text = "暂无符合条件的扫码记录。", Visibility = Visibility.Collapsed };
        stack.Children.Add(_orderStatisticsRecordsEmpty);
        _orderStatisticsRecordsGrid = CreateGrid();
        AddColumns(_orderStatisticsRecordsGrid,
            ("人员", "UserDisplay", "120"),
            ("业务类型", "OperationDisplay", "155"),
            ("扫码时间", "ClientScannedAt", "150"),
            ("识别内容", "RecognizedCodeDisplay", "2*"),
            ("原始内容", "RawCode", "2*"),
            ("状态", "StatusDisplay", "70"),
            ("结果说明", "ResultMessage", "2*"),
            ("设备", "DeviceId", "160"),
            ("质量标记", "QualityDisplay", "180"));
        stack.Children.Add(CreateSection("扫码记录", _orderStatisticsRecordsGrid));

        var pagination = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        _orderStatisticsPreviousButton = new Button { Content = "上一页", MinWidth = 64, IsEnabled = false };
        _orderStatisticsPreviousButton.Click += async (_, _) => await LoadOrderStatisticsPageAsync(_orderStatisticsCurrentPage - 1);
        _orderStatisticsPagination = new TextBlock { MinWidth = 180, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Foreground = Brush("#64748B"), Text = "第 1 页" };
        _orderStatisticsNextButton = new Button { Content = "下一页", MinWidth = 64, IsEnabled = false };
        _orderStatisticsNextButton.Click += async (_, _) => await LoadOrderStatisticsPageAsync(_orderStatisticsCurrentPage + 1);
        pagination.Children.Add(_orderStatisticsPreviousButton);
        pagination.Children.Add(_orderStatisticsPagination);
        pagination.Children.Add(_orderStatisticsNextButton);
        stack.Children.Add(pagination);
        return stack;
    }

    private async Task LoadOrderStatisticsFiltersAsync()
    {
        if (_orderStatisticsClient is null || _orderStatisticsUsersList is null || _orderStatisticsOperationsList is null)
        {
            return;
        }

        SetOrderStatisticsStatus("正在加载人员和业务类型...");
        try
        {
            Task<List<OrderStatisticsUserOption>> usersTask = _orderStatisticsClient.SearchUsersAsync(null);
            Task<List<OrderStatisticsOperationType>> operationsTask = _orderStatisticsClient.GetOperationTypesAsync();
            await Task.WhenAll(usersTask, operationsTask);

            _orderStatisticsUsers.Clear();
            _orderStatisticsUsers.AddRange(usersTask.Result.OrderBy(user => user.Display, StringComparer.CurrentCulture));
            _orderStatisticsOperationTypes.Clear();
            _orderStatisticsOperationTypes.AddRange(operationsTask.Result.Where(operation => operation.IsActive != false));
            _orderStatisticsUsersList.ItemsSource = _orderStatisticsUsers;
            _orderStatisticsOperationsList.ItemsSource = _orderStatisticsOperationTypes;
            SetOrderStatisticsStatus("筛选条件已加载。", false);
        }
        catch (Exception ex)
        {
            SetOrderStatisticsStatus("无法加载筛选条件：" + ex.Message, true);
        }
    }

    private async Task SearchOrderStatisticsUsersAsync()
    {
        if (_orderStatisticsClient is null || _orderStatisticsUsersList is null)
        {
            return;
        }

        HashSet<uint> selected = _orderStatisticsUsersList.SelectedItems.Cast<OrderStatisticsUserOption>().Select(user => user.Id).ToHashSet();
        try
        {
            List<OrderStatisticsUserOption> users = await _orderStatisticsClient.SearchUsersAsync(_orderStatisticsUserSearch?.Text);
            _orderStatisticsUsers.Clear();
            _orderStatisticsUsers.AddRange(users.OrderBy(user => user.Display, StringComparer.CurrentCulture));
            _orderStatisticsUsersList.ItemsSource = null;
            _orderStatisticsUsersList.ItemsSource = _orderStatisticsUsers;
            foreach (OrderStatisticsUserOption user in _orderStatisticsUsers.Where(user => selected.Contains(user.Id)))
            {
                _orderStatisticsUsersList.SelectedItems.Add(user);
            }
            SetOrderStatisticsStatus($"已加载 {_orderStatisticsUsers.Count} 位人员。", false);
        }
        catch (Exception ex)
        {
            SetOrderStatisticsStatus("无法搜索人员：" + ex.Message, true);
        }
    }

    private async Task RefreshOrderStatisticsAsync(bool resetPage)
    {
        if (_orderStatisticsClient is null || _orderStatisticsPage?.IsVisible != true)
        {
            return;
        }

        if (!TryCreateOrderStatisticsQuery(out OrderStatisticsQuery? query, out string validationError))
        {
            SetOrderStatisticsStatus(validationError, true);
            return;
        }

        if (resetPage)
        {
            _orderStatisticsCurrentPage = 1;
        }

        await LoadOrderStatisticsAsync(query!, includeDashboard: true);
    }

    private async Task LoadOrderStatisticsPageAsync(int page)
    {
        if (page < 1)
        {
            return;
        }

        if (!TryCreateOrderStatisticsQuery(out OrderStatisticsQuery? query, out string validationError))
        {
            SetOrderStatisticsStatus(validationError, true);
            return;
        }

        _orderStatisticsCurrentPage = page;
        await LoadOrderStatisticsAsync(query!, includeDashboard: false);
    }

    private async Task LoadOrderStatisticsAsync(OrderStatisticsQuery baseQuery, bool includeDashboard)
    {
        if (_orderStatisticsClient is null)
        {
            return;
        }

        _orderStatisticsCancellation?.Cancel();
        _orderStatisticsCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _orderStatisticsCancellation = cancellation;
        SetOrderStatisticsBusy(true);
        SetOrderStatisticsStatus("正在查询中心订单统计...");

        try
        {
            var recordsQuery = new OrderStatisticsQuery
            {
                From = baseQuery.From,
                To = baseQuery.To,
                UserIds = baseQuery.UserIds,
                OperationTypeCodes = baseQuery.OperationTypeCodes,
                Page = _orderStatisticsCurrentPage,
                PageSize = OrderStatisticsPageSize,
                Status = _orderStatisticsRecordTabs?.SelectedIndex == 0 ? "accepted" : null,
            };
            Task<OrderStatisticsRecordPage> recordsTask = _orderStatisticsClient.GetRecordsAsync(recordsQuery, cancellation.Token);
            Task<OrderStatisticsDashboard>? dashboardTask = includeDashboard
                ? _orderStatisticsClient.GetDashboardAsync(baseQuery, cancellation.Token)
                : null;
            await recordsTask;
            if (dashboardTask is not null)
            {
                await dashboardTask;
            }

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            ApplyOrderStatisticsRecords(recordsTask.Result);
            if (dashboardTask is not null)
            {
                ApplyOrderStatisticsDashboard(dashboardTask.Result);
            }
            SetOrderStatisticsStatus($"更新于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}，共 {_orderStatisticsTotal} 条记录。", false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetOrderStatisticsStatus("无法获取订单统计：" + ex.Message, true);
        }
        finally
        {
            if (ReferenceEquals(_orderStatisticsCancellation, cancellation))
            {
                SetOrderStatisticsBusy(false);
            }
        }
    }

    private bool TryCreateOrderStatisticsQuery(out OrderStatisticsQuery? query, out string error)
    {
        DateTime from = (_orderStatisticsFromDate?.SelectedDate ?? DateTime.Today).Date;
        DateTime to = (_orderStatisticsToDate?.SelectedDate ?? DateTime.Today).Date;
        if (from > to)
        {
            query = null;
            error = "结束日期不能早于开始日期。";
            return false;
        }

        DateTimeOffset fromOffset = ToLocalDateTimeOffset(from);
        DateTimeOffset toOffset = ToLocalDateTimeOffset(to.AddDays(1).AddTicks(-1));
        var selectedUsers = _orderStatisticsUsersList?.SelectedItems.Cast<OrderStatisticsUserOption>().Select(user => user.Id).ToArray() ?? [];
        var selectedOperations = _orderStatisticsOperationsList?.SelectedItems.Cast<OrderStatisticsOperationType>().Select(operation => operation.Code).ToArray() ?? [];
        bool allOperationsSelected = selectedOperations.Length == 0 || selectedOperations.Length == _orderStatisticsOperationTypes.Count;
        query = new OrderStatisticsQuery
        {
            From = fromOffset,
            To = toOffset,
            UserIds = selectedUsers,
            OperationTypeCodes = allOperationsSelected ? [] : selectedOperations,
            Page = _orderStatisticsCurrentPage,
            PageSize = OrderStatisticsPageSize,
        };
        error = string.Empty;
        return true;
    }

    private static DateTimeOffset ToLocalDateTimeOffset(DateTime value)
    {
        DateTime local = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private void ApplyOrderStatisticsDashboard(OrderStatisticsDashboard dashboard)
    {
        _orderStatisticsAccepted!.Text = dashboard.AcceptedCount.ToString("N0");
        _orderStatisticsTotalScans!.Text = dashboard.TotalScanCount.ToString("N0");
        _orderStatisticsAverage!.Text = dashboard.AverageDailyCount.ToString("0.0");
        _orderStatisticsMarkedError!.Text = dashboard.MarkedErrorCount.ToString("N0");
        _orderStatisticsErrorRate!.Text = dashboard.ErrorRateDisplay;
        _orderStatisticsOperationsGrid!.ItemsSource = dashboard.Operations;
        _orderStatisticsGroupsGrid!.ItemsSource = dashboard.Groups;
    }

    private void ApplyOrderStatisticsRecords(OrderStatisticsRecordPage page)
    {
        _orderStatisticsRecordsGrid!.ItemsSource = page.Items;
        _orderStatisticsRecordsEmpty!.Visibility = page.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _orderStatisticsTotal = Math.Max(0, page.Total);
        int totalPages = Math.Max(1, (int)Math.Ceiling((double)_orderStatisticsTotal / OrderStatisticsPageSize));
        _orderStatisticsPagination!.Text = _orderStatisticsTotal > 0
            ? $"第 {_orderStatisticsCurrentPage} / {totalPages} 页（{_orderStatisticsTotal} 条）"
            : $"第 {_orderStatisticsCurrentPage} 页";
        _orderStatisticsPreviousButton!.IsEnabled = _orderStatisticsCurrentPage > 1;
        _orderStatisticsNextButton!.IsEnabled = _orderStatisticsCurrentPage < totalPages;
    }

    private void ClearOrderStatisticsFilters()
    {
        DateTime today = DateTime.Today;
        _orderStatisticsFromDate!.SelectedDate = today;
        _orderStatisticsToDate!.SelectedDate = today;
        _orderStatisticsUserSearch!.Clear();
        _orderStatisticsUsersList!.UnselectAll();
        _orderStatisticsOperationsList!.UnselectAll();
        _ = RefreshOrderStatisticsAsync(resetPage: true);
    }

    private void SetOrderStatisticsBusy(bool busy)
    {
        if (_orderStatisticsRefreshButton is not null) _orderStatisticsRefreshButton.IsEnabled = !busy;
        if (_orderStatisticsPreviousButton is not null) _orderStatisticsPreviousButton.IsEnabled = !busy && _orderStatisticsCurrentPage > 1;
        if (_orderStatisticsNextButton is not null) _orderStatisticsNextButton.IsEnabled = !busy && _orderStatisticsCurrentPage * OrderStatisticsPageSize < _orderStatisticsTotal;
    }

    private void SetOrderStatisticsStatus(string message, bool error = false)
    {
        if (_orderStatisticsStatus is not null)
        {
            _orderStatisticsStatus.Text = message;
            _orderStatisticsStatus.Foreground = Brush(error ? "#B91C1C" : "#64748B");
        }
    }

    private static Border CreateSection(string title, UIElement content)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 10), FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(content);
        return new Border { Margin = new Thickness(0), Padding = new Thickness(14), BorderBrush = Brush("#E2E8F0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Background = Brushes.White, Child = panel };
    }

    private static DataGrid CreateGrid() => new()
    {
        AutoGenerateColumns = false,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        IsReadOnly = true,
        MinHeight = 220,
    };

    private static void AddColumns(DataGrid grid, params (string Header, string Path, string Width)[] columns)
    {
        foreach ((string header, string path, string width) in columns)
        {
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = new DataGridLengthConverter().ConvertFromString(width) is DataGridLength length ? length : DataGridLength.Auto });
        }
    }

    private static FrameworkElement FilterBlock(string title, UIElement content)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 5), Foreground = Brush("#475569") });
        panel.Children.Add(content);
        return panel;
    }

    private static TextBlock AddMetric(Panel panel, string title, string value, string background, string foreground)
    {
        var text = new TextBlock { Text = value, Margin = new Thickness(0, 4, 0, 0), FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brush(foreground) };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, Foreground = Brush("#475569") });
        stack.Children.Add(text);
        panel.Children.Add(new Border { Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(14), Background = Brush(background), BorderBrush = Brush("#CBD5E1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = stack });
        return text;
    }

    private static Brush Brush(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)!);
}

