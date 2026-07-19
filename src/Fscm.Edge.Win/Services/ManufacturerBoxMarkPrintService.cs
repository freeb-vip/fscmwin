// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.IO;
using System.Printing;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Fscm.Edge.Win.Models;
using QRCoder;

namespace Fscm.Edge.Win.Services;

public sealed class ManufacturerBoxMarkPrintService
{
    private const double UnitsPerMillimeter = 96d / 25.4d;
    private const double KeyColumnMillimeters = 24;
    private const double BaseRowHeightMillimeters = 10.6;
    private const double RowPaddingMillimeters = 2.3;
    private const double BorderMillimeters = 0.2;
    private const double QrSizeMillimeters = 16 * 1.32;
    private const double QrLabelHeightMillimeters = 3.2;
    private const double NoticeHeightRows = 1.35;
    private const double FontSize = 11;
    private const double KeyChineseFontSize = 9.5;
    private const double KeyEnglishFontSize = 6.5;
    private static readonly Brush LabelYellow = CreateFrozenBrush(Color.FromRgb(0xFF, 0xF7, 0x99));

    public void Print(EdgeSettings settings, EdgePrintJob job)
    {
        if (string.IsNullOrWhiteSpace(settings.DefaultPrinter) || job.BoxMarks.Count == 0)
        {
            throw new InvalidOperationException("厂家箱唛缺少打印机或箱唛内容。");
        }

        if ((Math.Abs(settings.PrintWidthMillimeters - 100) > 0.1) ||
            (Math.Abs(settings.PrintHeightMillimeters - 150) > 0.1))
        {
            throw new InvalidOperationException("厂家箱唛仅支持 100 x 150 mm 模板。");
        }

        using LocalPrintServer server = new();
        using PrintQueue queue = server.GetPrintQueue(settings.DefaultPrinter);
        LocalPrinterService.EnsureQueueAvailable(queue);
        PreparedPrintTarget target = PrintTargetService.Prepare(queue, settings);
        FixedDocument document = CreateDocument(job.BoxMarks, target.Context);
        PrintTargetService.Print(queue, target, document, $"FSCM 厂家箱唛 - {job.Id}");
    }

    public FixedDocument CreateDocument(EdgeSettings settings, IEnumerable<ManufacturerBoxMark> marks)
    {
        if ((Math.Abs(settings.PrintWidthMillimeters - 100) > 0.1) ||
            (Math.Abs(settings.PrintHeightMillimeters - 150) > 0.1))
        {
            throw new InvalidOperationException("厂家箱唛仅支持 100 x 150 mm 模板。");
        }

        List<ManufacturerBoxMark> pages = marks.ToList();
        if (pages.Count == 0)
        {
            throw new InvalidOperationException("厂家箱唛预览或打印内容为空。");
        }

        return CreateDocument(pages, PrintPageContextFactory.CreateNominal(settings));
    }

    public FixedDocument CreatePreviewDocument(EdgeSettings settings, IEnumerable<ManufacturerBoxMark> marks, out string diagnostic)
    {
        using LocalPrintServer server = new();
        using PrintQueue queue = server.GetPrintQueue(settings.DefaultPrinter);
        LocalPrinterService.EnsureQueueAvailable(queue);
        PreparedPrintTarget target = PrintTargetService.Prepare(queue, settings);
        diagnostic = target.Diagnostic;
        return CreateDocument(marks, target.Context);
    }

    private static FixedDocument CreateDocument(
        IEnumerable<ManufacturerBoxMark> marks,
        PrintPageContext context)
    {
        List<ManufacturerBoxMark> pages = marks.ToList();
        if (pages.Count == 0)
        {
            throw new InvalidOperationException("厂家箱唛预览或打印内容为空。");
        }

        FixedDocument document = new();
        foreach (ManufacturerBoxMark mark in pages)
        {
            document.Pages.Add(new PageContent
            {
                Child = CreatePage(mark, context),
            });
        }

        document.DocumentPaginator.PageSize = new Size(context.PageWidth, context.PageHeight);
        return document;
    }

    public static ManufacturerBoxMark CreatePreviewSample()
    {
        return new ManufacturerBoxMark
        {
            SeaMark = "WH-US-01",
            Shop = "BX-20260718-001",
            StyleCode = "TB15",
            SkuLines = ["TB15-BLACK-XL x 400"],
            Spec = "黑色 / XL",
            Pcs = "400 / 60x35x60cm",
            SkuBoxs = "1/44",
            Batch = "20260718 (1/44)",
            SkuQrPayload = "TB15-BLACK-XL",
            BoxQrPayload = "BX-20260718-001",
            BoxUid = "BOX-TRACK-20260718-001",
            InboundCode = "IN-20260718-001",
        };
    }

    internal static FixedPage CreatePage(ManufacturerBoxMark mark, double width, double height, double offsetMillimeters)
    {
        EdgeSettings settings = new()
        {
            PrintWidthMillimeters = width / UnitsPerMillimeter,
            PrintHeightMillimeters = height / UnitsPerMillimeter,
            PrintOrientation = "portrait",
            PrintOffsetXMillimeters = Math.Clamp(offsetMillimeters, -5, 5),
            PrintSafetyInsetMillimeters = 0.5,
        };
        return CreatePage(mark, PrintPageContextFactory.CreateNominal(settings));
    }

    private static FixedPage CreatePage(ManufacturerBoxMark mark, PrintPageContext context)
    {
        return context.Place(CreateLayout(mark, context.DesignWidth, context.DesignHeight));
    }

    private static Canvas CreateLayout(ManufacturerBoxMark mark, double width, double height)
    {
        double keyWidth = KeyColumnMillimeters * UnitsPerMillimeter;
        double baseRowHeight = BaseRowHeightMillimeters * UnitsPerMillimeter;
        double noticeHeight = baseRowHeight * NoticeHeightRows;
        double rowPadding = RowPaddingMillimeters * UnitsPerMillimeter;
        double borderWidth = BorderMillimeters * UnitsPerMillimeter;
        double qrSize = QrSizeMillimeters * UnitsPerMillimeter;
        double qrLabelHeight = QrLabelHeightMillimeters * UnitsPerMillimeter;
        string boxMarkCode = ResolveBoxMarkCode(mark);
        List<string> skuLines = ResolveSkuLines(mark);
        string boxQrPayload = ResolveBoxQrPayload(mark);
        string skuQrPayload = ResolveSkuQrPayload(mark);
        ValidateRequiredContent(boxMarkCode, skuLines, boxQrPayload, skuQrPayload);
        double valueRightPadding = (QrSizeMillimeters + 4) * UnitsPerMillimeter;

        double skuRowHeight = CalculateSkuRowHeight(skuLines.Count, baseRowHeight, rowPadding);
        List<BoxMarkRow> rows =
        [
            new("海运唛头", "SEA MARK", mark.SeaMark, false, baseRowHeight),
            new("箱唛码", "BOX MARK", boxMarkCode, false, baseRowHeight),
            new("产品编码", "STYLE CODE", mark.StyleCode, false, baseRowHeight),
            new("SKU", string.Empty, string.Join(Environment.NewLine, skuLines), true, skuRowHeight),
            new("规格", "SPEC", mark.Spec, true, baseRowHeight),
            new("数量/箱规", "PCS/CARTON", mark.Pcs, false, baseRowHeight),
            new("箱序", "BOX INDEX", mark.SkuBoxs, false, baseRowHeight),
            new("日期/批次", "DATE/BATCH", mark.Batch, false, baseRowHeight),
        ];
        double rowsHeight = rows.Sum(row => row.Height);
        double contentTop = Math.Max((height - rowsHeight - noticeHeight) / 2, 0);
        Canvas layout = new()
        {
            Width = width,
            Height = height,
            Background = Brushes.White,
        };
        layout.Children.Add(new Border
        {
            Width = width,
            Height = height,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(borderWidth),
        });

        Dictionary<string, (double Top, double Bottom)> rowBounds = [];
        double cursor = contentTop;
        foreach (BoxMarkRow row in rows)
        {
            rowBounds[row.ChineseKey] = (cursor, cursor + row.Height);
            AddKeyCell(layout, 0, cursor, keyWidth, row.Height, row.ChineseKey, row.EnglishKey, rowPadding, borderWidth);
            AddCell(layout, keyWidth, cursor, width - keyWidth, row.Height, row.Value, Brushes.White, row.Bold ? FontWeights.Bold : FontWeights.Normal, rowPadding, borderWidth, 3 * UnitsPerMillimeter, valueRightPadding, TextAlignment.Left);
            cursor += row.Height;
        }

        const string Notice = "注意：1、每箱贴3张，每张贴不同箱面；2、二维码必须清晰可见；\n3、建议使用10cm*15cm标准热敏纸打印；4、热敏纸上禁止覆盖胶布；";
        AddCell(layout, 0, cursor, width, noticeHeight, Notice, LabelYellow, FontWeights.Bold, rowPadding, borderWidth, 2 * UnitsPerMillimeter, 2 * UnitsPerMillimeter, TextAlignment.Center, 10);

        double rowAreaTop = contentTop + (1 * UnitsPerMillimeter);
        double rowAreaBottom = cursor - (0.8 * UnitsPerMillimeter);
        double qrBlockHeight = qrSize + qrLabelHeight;
        double qrX = width - qrSize - (1.5 * UnitsPerMillimeter);
        AddQrBlock(layout, boxQrPayload, "BOX", qrX, rowAreaTop, qrSize, qrLabelHeight);
        (double skuTop, _) = rowBounds["SKU"];
        (_, double specBottom) = rowBounds["规格"];
        double middleY = Math.Clamp(((skuTop + specBottom) / 2) - (qrBlockHeight / 2), rowAreaTop, rowAreaBottom - qrBlockHeight);
        double bottomY = Math.Max(rowAreaTop, rowAreaBottom - qrBlockHeight);
        AddQrBlock(layout, skuQrPayload, "SKU", qrX, middleY, qrSize, qrLabelHeight);
        AddQrBlock(layout, skuQrPayload, "SKU", qrX, bottomY, qrSize, qrLabelHeight);

        return layout;
    }

    internal static string ResolveBoxMarkCode(ManufacturerBoxMark mark)
    {
        return FirstNonEmpty(mark.Shop, mark.BoxUid, mark.InboundCode);
    }

    internal static string ResolveBoxQrPayload(ManufacturerBoxMark mark)
    {
        return FirstNonEmpty(mark.BoxQrPayload, mark.BoxUid, mark.InboundCode, ResolveBoxMarkCode(mark));
    }

    internal static string ResolveSkuQrPayload(ManufacturerBoxMark mark)
    {
        if (!string.IsNullOrWhiteSpace(mark.SkuQrPayload))
        {
            return mark.SkuQrPayload.Trim();
        }

        string firstLine = ResolveSkuLines(mark).FirstOrDefault() ?? string.Empty;
        string withoutQuantity = Regex.Replace(firstLine, @"\s+x\s+\d+.*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        return withoutQuantity.Split(['+', ':'], 2, StringSplitOptions.TrimEntries)[0];
    }

    private static List<string> ResolveSkuLines(ManufacturerBoxMark mark)
    {
        return mark.SkuLines.Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
    }

    private static void ValidateRequiredContent(string boxMarkCode, IReadOnlyCollection<string> skuLines, string boxQrPayload, string skuQrPayload)
    {
        if (string.IsNullOrWhiteSpace(boxMarkCode))
        {
            throw new InvalidOperationException("厂家箱唛缺少可见箱唛码。");
        }
        if (skuLines.Count == 0)
        {
            throw new InvalidOperationException("厂家箱唛缺少 SKU 和数量信息。");
        }
        if (string.IsNullOrWhiteSpace(boxQrPayload))
        {
            throw new InvalidOperationException("厂家箱唛缺少 BOX 二维码内容。");
        }
        if (string.IsNullOrWhiteSpace(skuQrPayload))
        {
            throw new InvalidOperationException("厂家箱唛缺少 SKU 二维码内容。");
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static double CalculateSkuRowHeight(int lineCount, double baseRowHeight, double rowPadding)
    {
        double contentHeight = (Math.Max(1, lineCount) * FontSize * 1.25) + (rowPadding * 2) + (0.6 * UnitsPerMillimeter);
        double minimumHeight = Math.Max(5.2 * UnitsPerMillimeter, FontSize + (rowPadding * 2));
        return Math.Clamp(contentHeight, minimumHeight, baseRowHeight * 2.2);
    }

    private static void AddKeyCell(
        Canvas canvas,
        double left,
        double top,
        double width,
        double height,
        string chinese,
        string english,
        double verticalPadding,
        double borderWidth)
    {
        StackPanel labels = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(1 * UnitsPerMillimeter, verticalPadding / 2, 1 * UnitsPerMillimeter, verticalPadding / 2),
        };
        labels.Children.Add(new TextBlock
        {
            Text = chinese,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = KeyChineseFontSize,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
        });
        if (!string.IsNullOrWhiteSpace(english))
        {
            labels.Children.Add(new TextBlock
            {
                Text = english,
                FontFamily = new FontFamily("Arial"),
                FontSize = KeyEnglishFontSize,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
            });
        }

        Border cell = new()
        {
            Width = width,
            Height = height,
            Background = LabelYellow,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(borderWidth),
            Child = labels,
        };
        Canvas.SetLeft(cell, left);
        Canvas.SetTop(cell, top);
        canvas.Children.Add(cell);
    }

    private static void AddCell(
        Canvas canvas,
        double left,
        double top,
        double width,
        double height,
        string value,
        Brush background,
        FontWeight weight,
        double verticalPadding,
        double borderWidth,
        double leftPadding,
        double rightPadding,
        TextAlignment alignment,
        double fontSize = FontSize)
    {
        TextBlock text = new()
        {
            Text = string.IsNullOrWhiteSpace(value) ? "-" : value,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = fontSize,
            FontWeight = weight,
            TextAlignment = alignment,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(leftPadding, verticalPadding, rightPadding, verticalPadding),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = fontSize * 1.25,
        };
        Border cell = new()
        {
            Width = width,
            Height = height,
            Background = background,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(borderWidth),
            Child = text,
        };
        Canvas.SetLeft(cell, left);
        Canvas.SetTop(cell, top);
        canvas.Children.Add(cell);
    }

    private static void AddQrBlock(Canvas canvas, string value, string title, double left, double top, double size, double labelHeight)
    {
        Image image = new()
        {
            Source = CreateQrImage(value),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(image, left);
        Canvas.SetTop(image, top);
        canvas.Children.Add(image);

        TextBlock label = new()
        {
            Width = size,
            Height = labelHeight,
            Background = Brushes.White,
            FontFamily = new FontFamily("Arial"),
            FontSize = 8,
            FontWeight = FontWeights.Bold,
            Text = title,
            TextAlignment = TextAlignment.Center,
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top + size + (0.4 * UnitsPerMillimeter));
        canvas.Children.Add(label);
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }

    private static BitmapImage CreateQrImage(string value)
    {
        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(
            string.IsNullOrWhiteSpace(value) ? "-" : value,
            QRCodeGenerator.ECCLevel.M);
        using PngByteQRCode qr = new(data);
        using MemoryStream stream = new(qr.GetGraphic(8, drawQuietZones: true));

        BitmapImage image = new();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private sealed record BoxMarkRow(string ChineseKey, string EnglishKey, string Value, bool Bold, double Height);
}
