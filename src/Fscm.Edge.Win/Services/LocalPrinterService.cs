// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Fscm.Edge.Win.Models;

namespace Fscm.Edge.Win.Services;

public sealed class LocalPrinterService
{
    public IReadOnlyList<LocalPrinter> GetPrinters()
    {
        try
        {
            using LocalPrintServer server = new();
            string? defaultName = server.DefaultPrintQueue?.Name;
            using PrintQueueCollection queues = server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections });
            List<LocalPrinter> printers = [];
            foreach (PrintQueue queue in queues)
            {
                var name = queue.Name;
                try
                {
                    queue.Refresh();
                    printers.Add(CreatePrinter(name, defaultName, EvaluateStatus(queue.QueueStatus)));
                }
                catch (Exception ex) when (ex is PrintQueueException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    printers.Add(CreatePrinter(
                        name,
                        defaultName,
                        new PrinterStatus(false, "unknown", "状态未知")));
                }
                finally
                {
                    queue.Dispose();
                }
            }

            return printers
                .OrderByDescending(printer => printer.IsDefault)
                .ThenBy(printer => printer.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is PrintQueueException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    public LocalPrinter? GetPrinter(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
        {
            return null;
        }

        return GetPrinters().FirstOrDefault(printer =>
            string.Equals(printer.Name, printerName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlySet<string> AvailablePrinterNames(IEnumerable<LocalPrinter> printers)
    {
        return printers
            .Where(printer => printer.IsAvailable)
            .Select(printer => printer.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void EnsurePrinterAvailable(string printerName)
    {
        var printer = GetPrinter(printerName);
        if (printer is null)
        {
            throw new InvalidOperationException($"打印机 {printerName} 不存在或无法读取状态。");
        }

        if (!printer.IsAvailable)
        {
            throw new InvalidOperationException($"打印机 {printer.Name} 当前不可打印：{printer.StatusText}。");
        }
    }

    public int CancelBatchPrintJobs(IEnumerable<EdgePrintJob> jobs)
    {
        int cancelled = 0;
        foreach (IGrouping<string, EdgePrintJob> printerJobs in jobs
                     .Where(job => !string.IsNullOrWhiteSpace(job.Printer) && !string.IsNullOrWhiteSpace(job.Id))
                     .GroupBy(job => job.Printer, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using LocalPrintServer server = new();
                using PrintQueue queue = server.GetPrintQueue(printerJobs.Key);
                HashSet<string> ids = printerJobs.Select(job => job.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                queue.Refresh();
                using PrintJobInfoCollection printJobs = queue.GetPrintJobInfoCollection();
                foreach (PrintSystemJobInfo printJob in printJobs)
                {
                    try
                    {
                        if (ids.Any(id => printJob.Name.Contains(id, StringComparison.OrdinalIgnoreCase)))
                        {
                            printJob.Cancel();
                            cancelled++;
                        }
                    }
                    finally
                    {
                        printJob.Dispose();
                    }
                }
            }
            catch (Exception ex) when (ex is PrintSystemException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                // The local task remains cancelled even when a printer driver
                // has already consumed the spool entry.
            }
        }
        return cancelled;
    }

    internal static void EnsureQueueAvailable(PrintQueue queue)
    {
        queue.Refresh();
        var status = EvaluateStatus(queue.QueueStatus);
        if (!status.IsAvailable)
        {
            throw new InvalidOperationException($"打印机 {queue.Name} 当前不可打印：{status.Text}。");
        }
    }

    internal static PrinterStatus EvaluateStatus(PrintQueueStatus status)
    {
        if (Has(status, PrintQueueStatus.Offline))
        {
            return new(false, "offline", "离线");
        }

        if (Has(status, PrintQueueStatus.Paused))
        {
            return new(false, "paused", "已暂停");
        }

        if (Has(status, PrintQueueStatus.PaperJam))
        {
            return new(false, "paper_jam", "卡纸");
        }

        if (Has(status, PrintQueueStatus.PaperOut))
        {
            return new(false, "paper_out", "缺纸");
        }

        if (Has(status, PrintQueueStatus.NoToner))
        {
            return new(false, "no_toner", "无碳粉");
        }

        if (Has(status, PrintQueueStatus.DoorOpen))
        {
            return new(false, "door_open", "机盖打开");
        }

        if (Has(status, PrintQueueStatus.OutputBinFull))
        {
            return new(false, "output_bin_full", "出纸盘已满");
        }

        if (Has(status, PrintQueueStatus.ManualFeed))
        {
            return new(false, "manual_feed", "等待手动送纸");
        }

        if (Has(status, PrintQueueStatus.UserIntervention))
        {
            return new(false, "user_intervention", "需要人工处理");
        }

        if (Has(status, PrintQueueStatus.PaperProblem))
        {
            return new(false, "paper_problem", "纸张异常");
        }

        if (Has(status, PrintQueueStatus.OutOfMemory))
        {
            return new(false, "out_of_memory", "打印机内存不足");
        }

        if (Has(status, PrintQueueStatus.PendingDeletion))
        {
            return new(false, "pending_deletion", "正在删除队列");
        }

        if (Has(status, PrintQueueStatus.NotAvailable))
        {
            return new(false, "not_available", "不可用");
        }

        if (Has(status, PrintQueueStatus.ServerUnknown))
        {
            return new(false, "server_unknown", "打印服务状态未知");
        }

        if (Has(status, PrintQueueStatus.Error))
        {
            return new(false, "error", "错误");
        }

        if (Has(status, PrintQueueStatus.PagePunt))
        {
            return new(false, "page_error", "页面处理失败");
        }

        if (Has(status, PrintQueueStatus.Printing))
        {
            return new(true, "printing", "正在打印");
        }

        if (Has(status, PrintQueueStatus.Busy))
        {
            return new(true, "busy", "忙碌（可排队）");
        }

        if (Has(status, PrintQueueStatus.Processing))
        {
            return new(true, "processing", "处理中（可排队）");
        }

        if (Has(status, PrintQueueStatus.WarmingUp))
        {
            return new(true, "warming_up", "预热中（可排队）");
        }

        if (Has(status, PrintQueueStatus.Initializing))
        {
            return new(true, "initializing", "初始化中（可排队）");
        }

        if (Has(status, PrintQueueStatus.PowerSave))
        {
            return new(true, "power_save", "节能待机");
        }

        if (Has(status, PrintQueueStatus.TonerLow))
        {
            return new(true, "toner_low", "碳粉不足（可打印）");
        }

        if (Has(status, PrintQueueStatus.Waiting))
        {
            return new(true, "waiting", "等待中（可排队）");
        }

        return new(true, "ready", "在线");
    }

    private static LocalPrinter CreatePrinter(string name, string? defaultName, PrinterStatus status)
    {
        return new LocalPrinter
        {
            Name = name,
            IsDefault = string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase),
            IsAvailable = status.IsAvailable,
            StatusCode = status.Code,
            StatusText = status.Text,
        };
    }

    private static bool Has(PrintQueueStatus status, PrintQueueStatus flag)
    {
        return (status & flag) != 0;
    }

    public void PrintTestPage(EdgeSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DefaultPrinter))
        {
            throw new InvalidOperationException("请先选择本地打印机。");
        }

        if (settings.PrintWidthMillimeters <= 0 || settings.PrintHeightMillimeters <= 0)
        {
            throw new InvalidOperationException("打印尺寸必须大于 0。");
        }

        using var server = new LocalPrintServer();
        using var queue = server.GetPrintQueue(settings.DefaultPrinter);
        EnsureQueueAvailable(queue);
        PreparedPrintTarget target = PrintTargetService.Prepare(queue, settings);
        FixedDocument document = CreateTestDocument(settings, target.Context);
        PrintTargetService.Print(queue, target, document, "FSCM Edge 打印配置测试");
    }

    private static FixedDocument CreateTestDocument(EdgeSettings settings, PrintPageContext context)
    {
        double width = context.DesignWidth;
        double height = context.DesignHeight;
        double margin = Math.Min(Math.Min(width, height) / 12d, 36d);
        Border border = new()
        {
            Width = width,
            Height = height,
            BorderBrush = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(Math.Min(margin, 20)),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "FSCM EDGE",
                        FontWeight = FontWeights.Bold,
                        FontSize = Math.Clamp(Math.Min(width, height) / 8d, 10, 22),
                        Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                    },
                    new TextBlock
                    {
                        Text = "本地打印配置测试",
                        Margin = new Thickness(0, 4, 0, 0),
                        FontSize = Math.Clamp(Math.Min(width, height) / 11d, 8, 16),
                    },
                    new TextBlock
                    {
                        Text = $"{settings.PrintWidthMillimeters:0.##} x {settings.PrintHeightMillimeters:0.##} mm | {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
                        Margin = new Thickness(0, 5, 0, 0),
                        FontSize = Math.Clamp(Math.Min(width, height) / 15d, 7, 13),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = context.Diagnostic,
                        Margin = new Thickness(0, 5, 0, 0),
                        FontSize = 7,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };

        FixedPage page = context.Place(border);

        PageContent content = new() { Child = page };
        FixedDocument document = new();
        document.Pages.Add(content);
        document.DocumentPaginator.PageSize = new Size(context.PageWidth, context.PageHeight);
        return document;
    }
}

internal readonly record struct PrinterStatus(bool IsAvailable, string Code, string Text);
