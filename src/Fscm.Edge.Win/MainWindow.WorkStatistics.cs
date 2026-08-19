using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Fscm.Edge.Win.Models;

namespace Fscm.Edge.Win;

public partial class MainWindow
{
    private static readonly TimeSpan WorkStatisticsRefreshInterval = TimeSpan.FromSeconds(15);
    private DispatcherTimer? _workStatisticsTimer;
    private CancellationTokenSource? _workStatisticsCancellation;
    private bool _refreshingWorkStatistics;

    private async void OnRefreshWorkStatisticsClick(object sender, RoutedEventArgs e) => await RefreshWorkStatisticsAsync();

    private void StartWorkStatisticsAutoRefresh()
    {
        _workStatisticsTimer ??= CreateWorkStatisticsTimer();
        _workStatisticsTimer.Start();
    }

    private void StopWorkStatisticsAutoRefresh()
    {
        _workStatisticsTimer?.Stop();
        _workStatisticsCancellation?.Cancel();
    }

    private DispatcherTimer CreateWorkStatisticsTimer()
    {
        var timer = new DispatcherTimer { Interval = WorkStatisticsRefreshInterval };
        timer.Tick += async (_, _) =>
        {
            if (WorkStatisticsPage.IsVisible)
            {
                await RefreshWorkStatisticsAsync();
            }
            else
            {
                timer.Stop();
            }
        };
        return timer;
    }

    private void OnWorkOperationClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: OrderScanJob job })
        {
            return;
        }

        try
        {
            new WorkJobQrDialog(job) { Owner = this }.ShowDialog();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, "无法生成作业二维码", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RefreshWorkStatisticsAsync()
    {
        if (!WorkStatisticsPage.IsVisible)
        {
            return;
        }

        StartWorkStatisticsAutoRefresh();
        if (_refreshingWorkStatistics)
        {
            return;
        }

        _workStatisticsCancellation?.Cancel();
        _workStatisticsCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _workStatisticsCancellation = cancellation;
        _refreshingWorkStatistics = true;
        WorkStatisticsStatusText.Text = "正在加载中心作业数据...";
        try
        {
            WorkStatisticsResult result = await _runtime.GetWorkStatisticsAsync(cancellation.Token);
            ActiveWorkSessionsGrid.ItemsSource = result.Sessions;
            ActiveWorkJobsGrid.ItemsSource = result.ActiveJobs;
            WorkBindingGrid.ItemsSource = result.Bindings;
            FinishedWorkJobsGrid.ItemsSource = result.FinishedJobs;
            ActiveWorkSessionsEmptyText.Visibility = result.Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ActiveWorkJobsEmptyText.Visibility = result.ActiveJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FinishedWorkJobsEmptyText.Visibility = result.FinishedJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            WorkBindingsEmptyText.Visibility = result.Bindings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PendingWorkJobCountText.Text = CountJobs(result.ActiveJobs, "pending").ToString("N0");
            OpenWorkJobCountText.Text = CountJobs(result.ActiveJobs, "open").ToString("N0");
            PausedWorkJobCountText.Text = CountJobs(result.ActiveJobs, "paused").ToString("N0");
            FinishedWorkJobCountText.Text = result.FinishedJobs.Count.ToString("N0");
            WorkStatisticsStatusText.Text = $"更新于 {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}，当前会话 {result.Sessions.Count} 个，未结束作业 {result.ActiveJobs.Count} 个，最近完成 {result.FinishedJobs.Count} 个。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ActiveWorkSessionsGrid.ItemsSource = null;
            ActiveWorkJobsGrid.ItemsSource = null;
            WorkBindingGrid.ItemsSource = null;
            FinishedWorkJobsGrid.ItemsSource = null;
            ActiveWorkSessionsEmptyText.Visibility = Visibility.Visible;
            ActiveWorkJobsEmptyText.Visibility = Visibility.Visible;
            FinishedWorkJobsEmptyText.Visibility = Visibility.Visible;
            WorkBindingsEmptyText.Visibility = Visibility.Visible;
            PendingWorkJobCountText.Text = "-";
            OpenWorkJobCountText.Text = "-";
            PausedWorkJobCountText.Text = "-";
            FinishedWorkJobCountText.Text = "-";
            WorkStatisticsStatusText.Text = "无法获取中心作业数据：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_workStatisticsCancellation, cancellation))
            {
                _workStatisticsCancellation.Dispose();
                _workStatisticsCancellation = null;
                _refreshingWorkStatistics = false;
            }
        }
    }

    private static int CountJobs(IEnumerable<OrderScanJob> jobs, string status)
    {
        return jobs.Count(job => string.Equals(job.Status, status, StringComparison.OrdinalIgnoreCase));
    }
}

