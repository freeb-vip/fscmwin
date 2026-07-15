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
    private const double UnitsPerMillimeter = 96d / 25.4d;

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
                try
                {
                    queue.Refresh();
                    if (IsUnavailable(queue.QueueStatus))
                    {
                        continue;
                    }

                    printers.Add(new LocalPrinter
                    {
                        Name = queue.Name,
                        IsDefault = string.Equals(queue.Name, defaultName, StringComparison.OrdinalIgnoreCase),
                    });
                }
                catch (PrintQueueException)
                {
                    // A single disconnected queue must not hide other usable printers.
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

    private static bool IsUnavailable(PrintQueueStatus status)
    {
        const PrintQueueStatus unavailable = PrintQueueStatus.Offline | PrintQueueStatus.NotAvailable | PrintQueueStatus.Error;
        return (status & unavailable) != 0;
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
        PrintDialog dialog = new()
        {
            PrintQueue = queue,
            PrintTicket = queue.DefaultPrintTicket.Clone(),
        };

        double width = settings.PrintWidthMillimeters * UnitsPerMillimeter;
        double height = settings.PrintHeightMillimeters * UnitsPerMillimeter;
        bool landscape = string.Equals(settings.PrintOrientation, "landscape", StringComparison.OrdinalIgnoreCase);
        (double pageWidth, double pageHeight) = OrientPageSize(width, height, landscape);
        dialog.PrintTicket.PageMediaSize = new PageMediaSize(PageMediaSizeName.Unknown, pageWidth, pageHeight);
        dialog.PrintTicket.PageOrientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
        dialog.PrintTicket.CopyCount = settings.PrintCopies;

        PageImageableArea? imageableArea = null;
        try
        {
            imageableArea = queue.GetPrintCapabilities(dialog.PrintTicket).PageImageableArea;
            if (!IsUsableImageableArea(imageableArea, pageWidth, pageHeight))
            {
                imageableArea = null;
            }
        }
        catch (PrintQueueException)
        {
            // Some label drivers do not expose imageable-area information.
        }

        FixedDocument document = CreateTestDocument(settings, pageWidth, pageHeight, imageableArea);
        dialog.PrintDocument(document.DocumentPaginator, "FSCM Edge 打印配置测试");
    }

    private static (double Width, double Height) OrientPageSize(double width, double height, bool landscape)
    {
        return landscape ? (height, width) : (width, height);
    }

    private static FixedDocument CreateTestDocument(EdgeSettings settings, double width, double height, PageImageableArea? imageableArea)
    {
        FixedPage page = new() { Width = width, Height = height, Background = Brushes.White };
        double margin = Math.Min(Math.Min(width, height) / 12d, 36d);
        double printableLeft = Math.Clamp(imageableArea?.OriginWidth ?? 0, 0, width);
        double printableTop = Math.Clamp(imageableArea?.OriginHeight ?? 0, 0, height);
        double printableWidth = Math.Clamp(imageableArea?.ExtentWidth ?? width, 1, width - printableLeft);
        double printableHeight = Math.Clamp(imageableArea?.ExtentHeight ?? height, 1, height - printableTop);
        double usableWidth = Math.Max(width - (margin * 2), 1);
        double usableHeight = Math.Max(height - (margin * 2), 1);
        double contentWidth = Math.Min(usableWidth, Math.Max(printableWidth - (margin * 2), 1));
        double contentHeight = Math.Min(usableHeight, Math.Max(printableHeight - (margin * 2), 1));
        double contentLeft = printableLeft + Math.Max((printableWidth - contentWidth) / 2d, 0) +
            (settings.PrintOffsetXMillimeters * UnitsPerMillimeter);
        double contentTop = printableTop + Math.Max((printableHeight - contentHeight) / 2d, 0);

        // Keep the horizontal calibration outside the page clamp so positive values
        // can compensate for a printer's physical left-origin offset.
        contentLeft = Math.Max(contentLeft, -contentWidth + 1);
        if (contentLeft >= 0)
        {
            contentWidth = Math.Min(contentWidth, Math.Max(width - contentLeft - 1, 1));
        }

        contentTop = Math.Clamp(contentTop, 0, Math.Max(height - contentHeight, 0));
        Border border = new()
        {
            Width = contentWidth,
            Height = contentHeight,
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
                },
            },
        };

        FixedPage.SetLeft(border, contentLeft);
        FixedPage.SetTop(border, contentTop);
        page.Children.Add(border);

        PageContent content = new() { Child = page };
        FixedDocument document = new();
        document.Pages.Add(content);
        document.DocumentPaginator.PageSize = new Size(width, height);
        return document;
    }

    private static bool IsUsableImageableArea(PageImageableArea? area, double width, double height)
    {
        if (area is null || !double.IsFinite(area.OriginWidth) || !double.IsFinite(area.OriginHeight) ||
            !double.IsFinite(area.ExtentWidth) || !double.IsFinite(area.ExtentHeight))
        {
            return false;
        }

        return area.OriginWidth >= 0 && area.OriginHeight >= 0 && area.ExtentWidth > 10 && area.ExtentHeight > 10 &&
            area.OriginWidth < width && area.OriginHeight < height &&
            area.OriginWidth + area.ExtentWidth <= width * 1.25 &&
            area.OriginHeight + area.ExtentHeight <= height * 1.25;
    }
}
