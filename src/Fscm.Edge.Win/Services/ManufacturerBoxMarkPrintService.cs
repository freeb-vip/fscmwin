// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Globalization;
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
    private const double QuadQrColumnMillimeters = 27;
    private const double QuadColumnGapMillimeters = 2;
    private const double QuadRowGapMillimeters = 6;
    private const double QuadHeaderFontSize = 10;
    private static readonly Brush LabelYellow = CreateFrozenBrush(Color.FromRgb(0xFF, 0xF7, 0x99));
    private static readonly Typeface QuadValueTypeface = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);

    public void Print(EdgeSettings settings, EdgePrintJob job)
    {
        Print(settings, job, new PrintTemplateProfile { LayoutStyle = PrintTemplatePolicy.StackedLayoutStyle });
    }

    public void Print(EdgeSettings settings, EdgePrintJob job, PrintTemplateProfile template)
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
        FixedDocument document = CreateDocument(job.BoxMarks, target.Context, template.LayoutStyle);
        PrintTargetService.Print(queue, target, document, $"FSCM 厂家箱唛 - {job.Id}");
    }

    public FixedDocument CreateDocument(EdgeSettings settings, IEnumerable<ManufacturerBoxMark> marks)
    {
        return CreateDocument(settings, new PrintTemplateProfile { LayoutStyle = PrintTemplatePolicy.StackedLayoutStyle }, marks);
    }

    public FixedDocument CreateDocument(EdgeSettings settings, PrintTemplateProfile template, IEnumerable<ManufacturerBoxMark> marks)
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

        return CreateDocument(pages, PrintPageContextFactory.CreateNominal(settings), template.LayoutStyle);
    }

    public FixedDocument CreatePreviewDocument(EdgeSettings settings, IEnumerable<ManufacturerBoxMark> marks, out string diagnostic)
    {
        return CreatePreviewDocument(
            settings,
            new PrintTemplateProfile { LayoutStyle = PrintTemplatePolicy.StackedLayoutStyle },
            marks,
            out diagnostic);
    }

    public FixedDocument CreatePreviewDocument(
        EdgeSettings settings,
        PrintTemplateProfile template,
        IEnumerable<ManufacturerBoxMark> marks,
        out string diagnostic)
    {
        using LocalPrintServer server = new();
        using PrintQueue queue = server.GetPrintQueue(settings.DefaultPrinter);
        LocalPrinterService.EnsureQueueAvailable(queue);
        PreparedPrintTarget target = PrintTargetService.Prepare(queue, settings);
        diagnostic = target.Diagnostic;
        return CreateDocument(marks, target.Context, template.LayoutStyle);
    }

    private static FixedDocument CreateDocument(
        IEnumerable<ManufacturerBoxMark> marks,
        PrintPageContext context,
        string? layoutStyle)
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
                Child = CreatePage(mark, context, layoutStyle),
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
            SkuCode = "TB15-BLACK-XL",
            SkuName = "轻量防泼水连帽夹克 黑色 XL",
            QuantityPerBox = 400,
            BoxQrPayload = "BX-20260718-001",
            BoxUid = "BOX-TRACK-20260718-001",
            InboundCode = "IN-20260718-001",
        };
    }

    internal static FixedPage CreatePage(ManufacturerBoxMark mark, double width, double height, double offsetMillimeters)
    {
        return CreatePage(mark, width, height, offsetMillimeters, PrintTemplatePolicy.StackedLayoutStyle);
    }

    internal static FixedPage CreatePage(
        ManufacturerBoxMark mark,
        double width,
        double height,
        double offsetMillimeters,
        string layoutStyle)
    {
        EdgeSettings settings = new()
        {
            PrintWidthMillimeters = width / UnitsPerMillimeter,
            PrintHeightMillimeters = height / UnitsPerMillimeter,
            PrintOrientation = "portrait",
            PrintOffsetXMillimeters = Math.Clamp(offsetMillimeters, -5, 5),
            PrintSafetyInsetMillimeters = 0.5,
        };
        return CreatePage(mark, PrintPageContextFactory.CreateNominal(settings), layoutStyle);
    }

    private static FixedPage CreatePage(ManufacturerBoxMark mark, PrintPageContext context, string? layoutStyle)
    {
        bool quadLayout = string.Equals(
            PrintTemplatePolicy.NormalizeLayoutStyle(layoutStyle),
            PrintTemplatePolicy.BoxMarkQuadLayoutStyle,
            StringComparison.Ordinal);
        FrameworkElement layout = quadLayout
            ? CreateRotatedQuadLayout(mark, context.DesignWidth, context.DesignHeight)
            : CreateClassicLayout(mark, context.DesignWidth, context.DesignHeight);
        return context.Place(layout);
    }

    private static Canvas CreateRotatedQuadLayout(ManufacturerBoxMark mark, double portraitWidth, double portraitHeight)
    {
        Grid landscapeLayout = CreateQuadLayout(mark, portraitHeight, portraitWidth);
        landscapeLayout.RenderTransformOrigin = new Point(0, 0);
        landscapeLayout.RenderTransform = new MatrixTransform(
            0,
            1,
            -1,
            0,
            portraitWidth,
            0);

        Canvas portraitPage = new()
        {
            Width = portraitWidth,
            Height = portraitHeight,
            Background = Brushes.White,
            ClipToBounds = true,
        };
        portraitPage.Children.Add(landscapeLayout);
        return portraitPage;
    }

    private static Canvas CreateClassicLayout(ManufacturerBoxMark mark, double width, double height)
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

    public static IReadOnlyList<ManufacturerBoxMark> PrepareMarksForTemplate(
        PrintTemplateProfile template,
        IEnumerable<BoxLabelSummary> labels)
    {
        List<BoxLabelSummary> source = labels.ToList();
        if (!string.Equals(
            PrintTemplatePolicy.NormalizeLayoutStyle(template.LayoutStyle),
            PrintTemplatePolicy.BoxMarkQuadLayoutStyle,
            StringComparison.Ordinal))
        {
            return source
                .Where(label => label.PrintSnapshot is not null)
                .Select(label => label.PrintSnapshot!)
                .ToList();
        }

        List<ManufacturerBoxMark> pages = [];
        foreach (BoxLabelSummary label in source)
        {
            ManufacturerBoxMark snapshot = label.PrintSnapshot
                ?? throw new InvalidOperationException($"箱唛 {label.LabelCode} 缺少打印快照。");
            if (label.SkuItems.Count == 0)
            {
                throw new InvalidOperationException($"箱唛 {label.LabelCode} 缺少 SKU 明细，无法使用横向四码模板。");
            }

            foreach (BoxLabelSkuItem sku in label.SkuItems)
            {
                string skuCode = sku.SkuCode.Trim();
                string skuName = FirstNonEmpty(sku.SkuName, sku.ProductName, skuCode);
                ManufacturerBoxMark page = CloneMark(snapshot);
                page.Shop = FirstNonEmpty(snapshot.Shop, label.LabelCode, label.BoxUid);
                page.BoxUid = FirstNonEmpty(snapshot.BoxUid, label.BoxUid);
                page.SkuCode = skuCode;
                page.SkuName = skuName;
                page.QuantityPerBox = sku.QuantityPerBox;
                page.SkuQrPayload = skuCode;
                page.SkuLines = [$"{skuCode} x {sku.QuantityPerBox}"];
                ValidateQuadRequiredContent(page);
                pages.Add(page);
            }
        }
        return pages;
    }

    private static ManufacturerBoxMark CloneMark(ManufacturerBoxMark source)
    {
        return new ManufacturerBoxMark
        {
            BoxPlanId = source.BoxPlanId,
            SeaMark = source.SeaMark,
            Shop = source.Shop,
            StyleCode = source.StyleCode,
            SkuLines = [.. source.SkuLines],
            Spec = source.Spec,
            Pcs = source.Pcs,
            SkuBoxs = source.SkuBoxs,
            Batch = source.Batch,
            SkuQrPayload = source.SkuQrPayload,
            SkuCode = source.SkuCode,
            SkuName = source.SkuName,
            QuantityPerBox = source.QuantityPerBox,
            BoxQrPayload = source.BoxQrPayload,
            BoxUid = source.BoxUid,
            InboundCode = source.InboundCode,
        };
    }

    private static Grid CreateQuadLayout(ManufacturerBoxMark mark, double width, double height)
    {
        ValidateQuadRequiredContent(mark);
        double outerMargin = 2 * UnitsPerMillimeter;
        double gap = QuadColumnGapMillimeters * UnitsPerMillimeter;
        double rowGap = QuadRowGapMillimeters * UnitsPerMillimeter;
        double availableWidth = Math.Max(width - (outerMargin * 2), 1);
        double availableHeight = Math.Max(height - (outerMargin * 2), 1);
        double sideWidth = Math.Min(QuadQrColumnMillimeters * UnitsPerMillimeter, Math.Max((availableWidth - (gap * 2)) * 0.29, 1));
        double centerWidth = Math.Max(availableWidth - (sideWidth * 2) - (gap * 2), 1);

        Grid outer = new() { Width = width, Height = height, Background = Brushes.White };
        Grid grid = new()
        {
            Width = availableWidth,
            Height = availableHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(sideWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(centerWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(sideWidth) });

        Grid leftCodes = CreateQuadQrColumn(ResolveBoxQrPayload(mark), "BOX ID", sideWidth, availableHeight, rowGap);
        Grid rightCodes = CreateQuadQrColumn(ResolveSkuQrPayload(mark), "SKU", sideWidth, availableHeight, rowGap);
        Grid center = CreateQuadCenter(mark, centerWidth, availableHeight);
        Grid.SetColumn(leftCodes, 0);
        Grid.SetColumn(center, 2);
        Grid.SetColumn(rightCodes, 4);
        grid.Children.Add(leftCodes);
        grid.Children.Add(center);
        grid.Children.Add(rightCodes);
        outer.Children.Add(grid);
        return outer;
    }

    private static Grid CreateQuadQrColumn(string payload, string title, double width, double height, double rowGap)
    {
        Grid column = new() { Width = width, Height = height };
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowGap) });
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        double labelHeight = 5 * UnitsPerMillimeter;
        double qrSize = Math.Max(Math.Min(width * 0.96, ((height - rowGap) / 2d) - labelHeight), 1);
        for (int row = 0; row <= 2; row += 2)
        {
            Grid block = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            block.RowDefinitions.Add(new RowDefinition { Height = new GridLength(qrSize) });
            block.RowDefinitions.Add(new RowDefinition { Height = new GridLength(labelHeight) });
            Image image = new()
            {
                Source = CreateQrImage(payload),
                Width = qrSize,
                Height = qrSize,
                Stretch = Stretch.Uniform,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            TextBlock label = new()
            {
                Text = title,
                FontFamily = new FontFamily("Arial"),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(label, 1);
            block.Children.Add(image);
            block.Children.Add(label);
            Grid.SetRow(block, row);
            column.Children.Add(block);
        }
        return column;
    }

    private static Grid CreateQuadCenter(ManufacturerBoxMark mark, double width, double height)
    {
        double boxHeight = height * 0.24;
        double nameHeight = height * 0.48;
        double quantityHeight = Math.Max(height - boxHeight - nameHeight, 1);
        Grid center = new() { Width = width, Height = height };
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(boxHeight) });
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(nameHeight) });
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(quantityHeight) });

        FrameworkElement boxCode = CreateQuadTextSection("箱唛编码", ResolveBoxMarkCode(mark), width, boxHeight, 1, false);
        FrameworkElement skuName = CreateQuadTextSection("SKU 名称", ResolveSkuName(mark), width, nameHeight, 3, true);
        FrameworkElement quantity = CreateQuadTextSection("箱规数量", $"{mark.QuantityPerBox} PCS", width, quantityHeight, 1, true);
        Grid.SetRow(skuName, 1);
        Grid.SetRow(quantity, 2);
        center.Children.Add(boxCode);
        center.Children.Add(skuName);
        center.Children.Add(quantity);
        return center;
    }

    private static FrameworkElement CreateQuadTextSection(
        string header,
        string value,
        double width,
        double height,
        int maxLines,
        bool underline)
    {
        double horizontalPadding = 1.5 * UnitsPerMillimeter;
        double headerHeight = 6 * UnitsPerMillimeter;
        double bodyWidth = Math.Max(width - (horizontalPadding * 2), 1);
        double bodyHeight = Math.Max(height - headerHeight - (1 * UnitsPerMillimeter), 1);
        double fontSize = maxLines > 1
            ? FitWrappedTextFontSize(value, bodyWidth, bodyHeight, maxLines, Math.Max(bodyHeight, 1), 8)
            : QrPrintService.FitTextFontSize(value, bodyWidth, bodyHeight, Math.Max(bodyHeight, 1), QuadValueTypeface);
        Grid section = new() { Width = width, Height = height };
        section.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerHeight) });
        section.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        TextBlock title = new()
        {
            Text = header,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = QuadHeaderFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.Black,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        TextBlock body = new()
        {
            Text = value,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = fontSize,
            FontWeight = FontWeights.Black,
            MaxWidth = bodyWidth,
            MaxHeight = bodyHeight,
            TextAlignment = TextAlignment.Center,
            TextWrapping = maxLines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = fontSize * 1.12,
        };
        if (underline)
        {
            body.TextDecorations = TextDecorations.Underline;
        }
        Grid.SetRow(body, 1);
        section.Children.Add(title);
        section.Children.Add(body);
        return section;
    }

    internal static double FitWrappedTextFontSize(
        string text,
        double maxWidth,
        double maxHeight,
        int maxLines,
        double maximumFontSize,
        double minimumFontSize)
    {
        double lower = Math.Max(minimumFontSize, 1);
        double upper = Math.Max(maximumFontSize, lower);
        for (int iteration = 0; iteration < 24; iteration++)
        {
            double candidate = (lower + upper) / 2d;
            FormattedText formatted = new(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                QuadValueTypeface,
                candidate,
                Brushes.Black,
                1)
            {
                MaxTextWidth = maxWidth,
                LineHeight = candidate * 1.12,
            };
            double allowedHeight = Math.Min(maxHeight, formatted.LineHeight * Math.Max(maxLines, 1));
            if (formatted.Height <= allowedHeight)
            {
                lower = candidate;
            }
            else
            {
                upper = candidate;
            }
        }
        return lower;
    }

    internal static string ResolveSkuName(ManufacturerBoxMark mark)
    {
        return FirstNonEmpty(mark.SkuName, mark.SkuCode, ResolveSkuQrPayload(mark));
    }

    internal static void ValidateQuadRequiredContent(ManufacturerBoxMark mark)
    {
        if (string.IsNullOrWhiteSpace(ResolveBoxMarkCode(mark)))
        {
            throw new InvalidOperationException("横向四码箱唛缺少箱唛编码。");
        }
        if (string.IsNullOrWhiteSpace(ResolveBoxQrPayload(mark)))
        {
            throw new InvalidOperationException("横向四码箱唛缺少 BOX 二维码内容。");
        }
        if (string.IsNullOrWhiteSpace(mark.SkuCode))
        {
            throw new InvalidOperationException("横向四码箱唛缺少 SKU 编码。");
        }
        if (string.IsNullOrWhiteSpace(ResolveSkuName(mark)))
        {
            throw new InvalidOperationException("横向四码箱唛缺少 SKU 名称。");
        }
        if (string.IsNullOrWhiteSpace(ResolveSkuQrPayload(mark)))
        {
            throw new InvalidOperationException("横向四码箱唛缺少 SKU 二维码内容。");
        }
        if (mark.QuantityPerBox <= 0)
        {
            throw new InvalidOperationException("横向四码箱唛的箱规数量必须大于 0。");
        }
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

        if (!string.IsNullOrWhiteSpace(mark.SkuCode))
        {
            return mark.SkuCode.Trim();
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
