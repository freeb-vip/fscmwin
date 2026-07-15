// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fscm.Edge.Win.Models;
using QRCoder;

namespace Fscm.Edge.Win.Services;

public sealed class QrPrintService
{
    private const double UnitsPerMillimeter = 96d / 25.4d;

    public void Print(EdgeSettings settings, EdgePrintJob job)
    {
        if (string.IsNullOrWhiteSpace(settings.DefaultPrinter) || job.Items.Count == 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(settings.DefaultPrinter)
                ? "打印机为空。"
                : "SKU 打印内容为空。");
        }

        double width = settings.PrintWidthMillimeters * UnitsPerMillimeter;
        double height = settings.PrintHeightMillimeters * UnitsPerMillimeter;
        bool landscape = string.Equals(settings.PrintOrientation, "landscape", StringComparison.OrdinalIgnoreCase);
        (double pageWidth, double pageHeight) = landscape ? (height, width) : (width, height);

        using LocalPrintServer server = new();
        using PrintQueue queue = server.GetPrintQueue(settings.DefaultPrinter);
        PrintDialog dialog = new()
        {
            PrintQueue = queue,
            PrintTicket = queue.DefaultPrintTicket.Clone(),
        };
        dialog.PrintTicket.PageMediaSize = new PageMediaSize(PageMediaSizeName.Unknown, pageWidth, pageHeight);
        dialog.PrintTicket.PageOrientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
        dialog.PrintTicket.CopyCount = settings.PrintCopies;
        PageImageableArea? imageableArea = GetImageableArea(queue, dialog.PrintTicket, pageWidth, pageHeight);

        FixedDocument document = new();
        foreach (var item in job.Items)
        {
            int quantity = Math.Clamp(item.Quantity, 1, 99);
            for (int index = 0; index < quantity; index++)
            {
                document.Pages.Add(new PageContent { Child = CreatePage(settings, job, item, pageWidth, pageHeight, imageableArea) });
            }
        }

        document.DocumentPaginator.PageSize = new Size(pageWidth, pageHeight);
        dialog.PrintDocument(document.DocumentPaginator, $"FSCM SKU 二维码 - {job.Id}");
    }

    public void PrintLabel(EdgeSettings settings, string qrPayload, string displayText, int maxDisplayLength)
    {
        if (string.IsNullOrWhiteSpace(settings.DefaultPrinter))
        {
            throw new InvalidOperationException("请先选择本地打印机。");
        }

        if (settings.PrintWidthMillimeters <= 0 || settings.PrintHeightMillimeters <= 0)
        {
            throw new InvalidOperationException("打印模板纸张尺寸无效。");
        }

        double width = settings.PrintWidthMillimeters * UnitsPerMillimeter;
        double height = settings.PrintHeightMillimeters * UnitsPerMillimeter;
        bool landscape = string.Equals(settings.PrintOrientation, "landscape", StringComparison.OrdinalIgnoreCase);
        (double pageWidth, double pageHeight) = landscape ? (height, width) : (width, height);
        using LocalPrintServer server = new();
        using PrintQueue queue = server.GetPrintQueue(settings.DefaultPrinter);
        PrintDialog dialog = new()
        {
            PrintQueue = queue,
            PrintTicket = queue.DefaultPrintTicket.Clone(),
        };
        dialog.PrintTicket.PageMediaSize = new PageMediaSize(PageMediaSizeName.Unknown, pageWidth, pageHeight);
        dialog.PrintTicket.PageOrientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
        dialog.PrintTicket.CopyCount = Math.Clamp(settings.PrintCopies, 1, 99);
        PageImageableArea? imageableArea = GetImageableArea(queue, dialog.PrintTicket, pageWidth, pageHeight);

        string text = displayText;
        FixedDocument document = new();
        document.Pages.Add(new PageContent { Child = CreateLabelPage(qrPayload, displayText, pageWidth, pageHeight, settings.PrintOffsetXMillimeters, maxDisplayLength, settings.PrintMode, imageableArea) });
        document.DocumentPaginator.PageSize = new Size(pageWidth, pageHeight);
        dialog.PrintDocument(document.DocumentPaginator, $"FSCM 标签 - {text}");
    }

    private static FixedPage CreateLabelPage(string qrPayload, string displayText, double width, double height, double offsetXMillimeters, int maxDisplayLength, string mode, PageImageableArea? imageableArea)
    {
        const double labelMargin = 2;
        double margin = labelMargin * UnitsPerMillimeter;
        var bounds = CalculateContentBounds(width, height, margin, offsetXMillimeters, imageableArea);
        double contentWidth = bounds.Width;
        double contentHeight = bounds.Height;
        double qrWidthRatio = mode switch
        {
            "actual_size" => 0.65,
            "fill" => 0.86,
            _ => 0.78,
        };
        double textReserve = Math.Max(26, Math.Min(contentHeight * 0.25, 38));
        double qrSize = Math.Min(contentWidth * qrWidthRatio, Math.Max(contentHeight - textReserve, 24));
        int displayTextLimit = maxDisplayLength > 0 ? maxDisplayLength : 16;
        string renderedText = displayText.Length > displayTextLimit
            ? displayText[..Math.Min(displayTextLimit, displayText.Length)] + "..."
            : displayText;
        Border content = new()
        {
            Width = contentWidth,
            Height = contentHeight,
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new Image { Source = CreateQrImage(qrPayload), Width = qrSize, Height = qrSize, Stretch = Stretch.Uniform },
                    new TextBlock { Text = renderedText, FontSize = 13, FontWeight = FontWeights.Bold, MaxWidth = contentWidth - 4, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) },
                },
            },
        };
        FixedPage.SetLeft(content, bounds.Left);
        FixedPage.SetTop(content, bounds.Top);
        FixedPage page = new() { Width = width, Height = height, Background = Brushes.White };
        page.Children.Add(content);
        return page;
    }

    private static BitmapImage CreateQrImage(string payload)
    {
        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using PngByteQRCode renderer = new(data);
        byte[] bytes = renderer.GetGraphic(8);
        BitmapImage image = new();
        using MemoryStream stream = new(bytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static FixedPage CreatePage(EdgeSettings settings, EdgePrintJob job, EdgePrintJobItem item, double width, double height, PageImageableArea? imageableArea)
    {
        FixedPage page = new() { Width = width, Height = height, Background = Brushes.White };
        double margin = Math.Min(Math.Min(width, height) / 12d, 24d);
        var bounds = CalculateContentBounds(width, height, margin, settings.PrintOffsetXMillimeters, imageableArea);
        double contentWidth = bounds.Width;
        double contentHeight = bounds.Height;
        double textReserve = Math.Max(44, Math.Min(contentHeight * 0.32, 64));
        double qrSize = Math.Max(Math.Min(contentWidth * 0.82, contentHeight - textReserve), 24);
        var payload = string.IsNullOrWhiteSpace(item.QrCodeContent) ? settings.SkuQrPrefix + item.SkuCode : item.QrCodeContent;
        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using PngByteQRCode renderer = new(data);
        byte[] bytes = renderer.GetGraphic(8);
        BitmapImage image = new();
        using (var stream = new MemoryStream(bytes))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
        }

        Border content = new()
        {
            Width = Math.Max(contentWidth, 1),
            Height = Math.Max(contentHeight, 1),
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new Image { Source = image, Width = qrSize, Height = qrSize, Stretch = Stretch.Uniform },
                    new TextBlock { Text = payload, FontSize = 13, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 5, 0, 0) },
                },
            },
        };
        FixedPage.SetLeft(content, bounds.Left);
        FixedPage.SetTop(content, bounds.Top);
        page.Children.Add(content);
        return page;
    }

    private static (double Left, double Top, double Width, double Height) CalculateContentBounds(double width, double height, double margin, double offsetXMillimeters, PageImageableArea? imageableArea)
    {
        // The print driver applies the imageable-area origin when it maps the
        // FixedPage to the printer. Use only its extent here; adding OriginWidth
        // again causes a systematic horizontal shift on label printers.
        double printableWidth = Math.Clamp(imageableArea?.ExtentWidth ?? width, 1, width);
        double printableHeight = Math.Clamp(imageableArea?.ExtentHeight ?? height, 1, height);
        double widthLimit = Math.Max(printableWidth - (margin * 2), 1);
        double heightLimit = Math.Max(printableHeight - (margin * 2), 1);
        double contentWidth = Math.Min(Math.Max(width - (margin * 2), 1), widthLimit);
        double contentHeight = Math.Min(Math.Max(height - (margin * 2), 1), heightLimit);
        double left = Math.Max((width - contentWidth) / 2d, 0) + (offsetXMillimeters * UnitsPerMillimeter);
        double top = Math.Max((height - contentHeight) / 2d, 0);
        left = Math.Clamp(left, 0, Math.Max(width - contentWidth, 0));
        top = Math.Clamp(top, 0, Math.Max(height - contentHeight, 0));
        return (left, top, contentWidth, contentHeight);
    }

    private static PageImageableArea? GetImageableArea(PrintQueue queue, PrintTicket ticket, double width, double height)
    {
        try
        {
            var area = queue.GetPrintCapabilities(ticket).PageImageableArea;
            if (area is null || area.OriginWidth < 0 || area.OriginHeight < 0 || area.ExtentWidth <= 10 || area.ExtentHeight <= 10 ||
                area.OriginWidth >= width || area.OriginHeight >= height || area.OriginWidth + area.ExtentWidth > width * 1.25 || area.OriginHeight + area.ExtentHeight > height * 1.25)
            {
                return null;
            }

            return area;
        }
        catch (PrintQueueException)
        {
            return null;
        }
    }
}
