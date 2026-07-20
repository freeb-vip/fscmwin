// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Globalization;
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
    internal const double UnitsPerMillimeter = 96d / 25.4d;
    private const double PointsToDeviceIndependentPixels = 96d / 72d;
    private static readonly Typeface LabelTypeface = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface LocationLabelTypeface = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);

    public void Print(EdgeSettings settings, EdgePrintJob job, PrintTemplateProfile template)
    {
        if (string.IsNullOrWhiteSpace(settings.DefaultPrinter) || job.Items.Count == 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(settings.DefaultPrinter)
                ? "打印机为空。"
                : "SKU 打印内容为空。");
        }

        using LocalPrintServer server = new();
        using PrintQueue queue = server.GetPrintQueue(settings.DefaultPrinter);
        LocalPrinterService.EnsureQueueAvailable(queue);
        PreparedPrintTarget target = PrintTargetService.Prepare(queue, settings);

        FixedDocument document = new();
        foreach (EdgePrintJobItem item in job.Items)
        {
            int quantity = Math.Clamp(item.Quantity, 1, 99);
            string payload = string.IsNullOrWhiteSpace(item.QrCodeContent)
                ? settings.SkuQrPrefix + item.SkuCode
                : item.QrCodeContent;
            ValidateDisplayText(template, payload);
            for (int index = 0; index < quantity; index++)
            {
                document.Pages.Add(new PageContent
                {
                    Child = CreateLabelPage(payload, payload, template, target.Context),
                });
            }
        }

        document.DocumentPaginator.PageSize = new Size(target.Context.PageWidth, target.Context.PageHeight);
        PrintTargetService.Print(queue, target, document, $"FSCM SKU 二维码 - {job.Id}");
    }

    public void Print(EdgeSettings settings, EdgePrintJob job, LabelSheetLayout layout = LabelSheetLayout.Single)
    {
        Print(settings, job, CreateCompatibilityTemplate(settings, layout));
    }

    public void PrintLabel(
        EdgeSettings settings,
        string qrPayload,
        string displayText,
        PrintTemplateProfile template)
    {
        ValidateDisplayText(template, displayText);
        if (string.IsNullOrWhiteSpace(settings.DefaultPrinter))
        {
            throw new InvalidOperationException("请先选择本地打印机。");
        }

        if (settings.PrintWidthMillimeters <= 0 || settings.PrintHeightMillimeters <= 0)
        {
            throw new InvalidOperationException("打印模板纸张尺寸无效。");
        }

        using LocalPrintServer server = new();
        using PrintQueue queue = server.GetPrintQueue(settings.DefaultPrinter);
        LocalPrinterService.EnsureQueueAvailable(queue);
        PreparedPrintTarget target = PrintTargetService.Prepare(queue, settings);
        FixedDocument document = CreateLabelDocument(template, qrPayload, displayText, target.Context);
        PrintTargetService.Print(queue, target, document, $"FSCM 标签 - {displayText}");
    }

    public void PrintLabel(
        EdgeSettings settings,
        string qrPayload,
        string displayText,
        int maxDisplayLength,
        LabelSheetLayout layout = LabelSheetLayout.Single)
    {
        PrintTemplateProfile template = CreateCompatibilityTemplate(settings, layout);
        template.MaxDisplayLength = maxDisplayLength;
        PrintLabel(settings, qrPayload, displayText, template);
    }

    public FixedDocument CreatePreviewDocument(
        EdgeSettings settings,
        PrintTemplateProfile template,
        string qrPayload,
        string displayText)
    {
        return CreatePreviewDocument(settings, template, qrPayload, displayText, out _);
    }

    public FixedDocument CreatePreviewDocument(
        EdgeSettings settings,
        PrintTemplateProfile template,
        string qrPayload,
        string displayText,
        out string diagnostic)
    {
        ValidateDisplayText(template, displayText);
        PrintPageContext context;
        if (string.IsNullOrWhiteSpace(settings.DefaultPrinter))
        {
            context = PrintPageContextFactory.CreateNominal(settings);
        }
        else
        {
            using LocalPrintServer server = new();
            using PrintQueue queue = server.GetPrintQueue(settings.DefaultPrinter);
            LocalPrinterService.EnsureQueueAvailable(queue);
            PreparedPrintTarget target = PrintTargetService.Prepare(queue, settings);
            context = target.Context;
        }

        diagnostic = context.Diagnostic;
        return CreateLabelDocument(template, qrPayload, displayText, context);
    }

    private static FixedDocument CreateLabelDocument(
        PrintTemplateProfile template,
        string qrPayload,
        string displayText,
        PrintPageContext context)
    {
        FixedDocument document = new();
        document.Pages.Add(new PageContent
        {
            Child = CreateLabelPage(qrPayload, displayText, template, context),
        });
        document.DocumentPaginator.PageSize = new Size(context.PageWidth, context.PageHeight);
        return document;
    }

    internal static FixedPage CreateLabelPage(
        string qrPayload,
        string displayText,
        double width,
        double height,
        double offsetXMillimeters,
        PrintTemplateProfile template,
        PageImageableArea? imageableArea = null)
    {
        EdgeSettings settings = new()
        {
            PrintWidthMillimeters = width / UnitsPerMillimeter,
            PrintHeightMillimeters = height / UnitsPerMillimeter,
            PrintOrientation = "portrait",
            PrintOffsetXMillimeters = Math.Clamp(offsetXMillimeters, -5, 5),
            PrintSafetyInsetMillimeters = PrintPageContextFactory.DefaultSafetyInsetMillimeters,
        };
        Rect printable = imageableArea is null
            ? new Rect(0, 0, width, height)
            : new Rect(imageableArea.OriginWidth, imageableArea.OriginHeight, imageableArea.ExtentWidth, imageableArea.ExtentHeight);
        PrintPageContext context = PrintPageContextFactory.Calculate(
            width,
            height,
            printable,
            settings,
            settings.PrintWidthMillimeters,
            settings.PrintHeightMillimeters);
        return CreateLabelPage(qrPayload, displayText, template, context);
    }

    private static FixedPage CreateLabelPage(
        string qrPayload,
        string displayText,
        PrintTemplateProfile template,
        PrintPageContext context)
    {
        const double labelMarginMillimeters = 2;
        double margin = labelMarginMillimeters * UnitsPerMillimeter;
        var bounds = (
            Left: margin,
            Top: margin,
            Width: Math.Max(context.DesignWidth - (margin * 2), 1),
            Height: Math.Max(context.DesignHeight - (margin * 2), 1));
        Canvas design = new()
        {
            Width = context.DesignWidth,
            Height = context.DesignHeight,
            Background = Brushes.White,
        };
        BitmapImage image = CreateQrImage(ResolveQrPayload(qrPayload, displayText, template));

        if (PrintTemplatePolicy.GetLabelSheetLayout(template) == LabelSheetLayout.FourUpRepeated)
        {
            foreach (var cell in CalculateFourUpCellBounds(bounds))
            {
                Border content = CreateLabelContent(image, displayText, cell.Width, cell.Height, template);
                FixedPage.SetLeft(content, cell.Left);
                FixedPage.SetTop(content, cell.Top);
                design.Children.Add(content);
            }
            return context.Place(design);
        }

        Border singleContent = CreateLabelContent(image, displayText, bounds.Width, bounds.Height, template);
        FixedPage.SetLeft(singleContent, bounds.Left);
        FixedPage.SetTop(singleContent, bounds.Top);
        design.Children.Add(singleContent);
        return context.Place(design);
    }

    internal static Border CreateLabelContent(
        BitmapImage image,
        string displayText,
        double width,
        double height,
        PrintTemplateProfile template)
    {
        string layoutStyle = PrintTemplatePolicy.NormalizeLayoutStyle(template.LayoutStyle);
        double fontSize = PrintTemplatePolicy.GetTextFontSizePoints(template) * PointsToDeviceIndependentPixels;
        const double qrScale = 0.95;

        return layoutStyle switch
        {
            PrintTemplatePolicy.LocationCodeLayoutStyle => CreateLocationCodeLabelContent(image, displayText, width, height, qrScale),
            PrintTemplatePolicy.HorizontalLayoutStyle => CreateHorizontalLabelContent(image, displayText, width, height, fontSize, qrScale),
            _ => CreateStackedLabelContent(image, displayText, width, height, template.MaxDisplayLength, fontSize, qrScale),
        };
    }

    internal static string ResolveQrPayload(string qrPayload, string displayText, PrintTemplateProfile template)
    {
        return PrintTemplatePolicy.NormalizeLayoutStyle(template.LayoutStyle) == PrintTemplatePolicy.LocationCodeLayoutStyle
            ? displayText
            : qrPayload;
    }

    internal static void ValidateDisplayText(PrintTemplateProfile template, string? displayText)
    {
        string? error = PrintTemplatePolicy.GetDisplayTextValidationError(template, displayText);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }
    }

    private static Border CreateLocationCodeLabelContent(
        BitmapImage image,
        string displayText,
        double width,
        double height,
        double qrScale)
    {
        var layout = CalculateLocationCodeLayout(width, height);
        string text = displayText;
        double textMaxWidth = Math.Max(layout.CenterWidth - 4, 1);
        double textMaxHeight = Math.Max(height - 4, 1);
        double fontSizePoints = FitLocationCodeFontSizePoints(
            text,
            textMaxWidth,
            textMaxHeight);
        double fontSize = fontSizePoints * PointsToDeviceIndependentPixels;
        double qrSize = Math.Max(Math.Min(layout.SideWidth, layout.QrRowHeight) * qrScale, 1);

        Grid grid = new() { Width = width, Height = height };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.SideWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.ColumnGap) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.CenterWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.ColumnGap) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.SideWidth) });

        Grid leftCodes = CreateLocationQrColumn(image, qrSize, layout.RowGap);
        Grid rightCodes = CreateLocationQrColumn(image, qrSize, layout.RowGap);
        TextBlock label = CreateTextBlock(text, fontSize, textMaxWidth);
        label.FontWeight = FontWeights.Black;
        label.TextDecorations = TextDecorations.Underline;
        Grid.SetColumn(leftCodes, 0);
        Grid.SetColumn(label, 2);
        Grid.SetColumn(rightCodes, 4);
        grid.Children.Add(leftCodes);
        grid.Children.Add(label);
        grid.Children.Add(rightCodes);
        return new Border { Width = width, Height = height, ClipToBounds = true, Child = grid };
    }

    private static Grid CreateLocationQrColumn(BitmapImage image, double qrSize, double rowGap)
    {
        Grid column = new();
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowGap) });
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int row = 0; row <= 2; row += 2)
        {
            Image qr = new()
            {
                Source = image,
                Width = qrSize,
                Height = qrSize,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
            Grid.SetRow(qr, row);
            column.Children.Add(qr);
        }
        return column;
    }

    internal static (double SideWidth, double CenterWidth, double QrRowHeight, double ColumnGap, double RowGap) CalculateLocationCodeLayout(
        double width,
        double height)
    {
        double sideWidth = Math.Min(33 * UnitsPerMillimeter, Math.Max(width * 0.24, 1));
        double columnGap = 2 * UnitsPerMillimeter;
        double rowGap = 4 * UnitsPerMillimeter;
        double centerWidth = Math.Max(width - (sideWidth * 2) - (columnGap * 2), 1);
        double qrRowHeight = Math.Max((height - rowGap) / 2d, 1);
        return (sideWidth, centerWidth, qrRowHeight, columnGap, rowGap);
    }

    internal static double FitLocationCodeFontSizePoints(string text, double maxWidth, double maxHeight)
    {
        return FitTextFontSize(text, maxWidth, maxHeight, Math.Max(maxHeight, 1), LocationLabelTypeface) /
            PointsToDeviceIndependentPixels;
    }

    private static Border CreateStackedLabelContent(
        BitmapImage image,
        string displayText,
        double width,
        double height,
        int maxDisplayLength,
        double fontSize,
        double qrScale)
    {
        double gap = 1 * UnitsPerMillimeter;
        double textHeight = Math.Clamp(Math.Max(8 * UnitsPerMillimeter, fontSize * 1.35), 1, Math.Max(height * 0.4, 1));
        double qrRegionHeight = Math.Max(height - textHeight - gap, 1);
        double qrSize = Math.Max(Math.Min(width, qrRegionHeight) * qrScale, 1);
        string text = TruncateTextToWidth(displayText, maxDisplayLength, Math.Max(width - 4, 1), fontSize);

        Grid grid = new() { Width = width, Height = height };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(qrRegionHeight) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(gap) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(textHeight) });
        Image qr = new()
        {
            Source = image,
            Width = qrSize,
            Height = qrSize,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
        TextBlock label = CreateTextBlock(text, fontSize, Math.Max(width - 4, 1));
        Grid.SetRow(qr, 0);
        Grid.SetRow(label, 2);
        grid.Children.Add(qr);
        grid.Children.Add(label);
        return new Border { Width = width, Height = height, ClipToBounds = true, Child = grid };
    }

    private static Border CreateHorizontalLabelContent(
        BitmapImage image,
        string displayText,
        double width,
        double height,
        double fontSize,
        double qrScale)
    {
        double gap = 2 * UnitsPerMillimeter;
        double regionWidth = Math.Max((width - gap) / 2d, 1);
        double qrSize = Math.Max(Math.Min(regionWidth, height) * qrScale, 1);
        double textMaxWidth = Math.Max(regionWidth - 4, 1);
        double textMaxHeight = Math.Max(height - 4, 1);
        string text = displayText;
        double fittedFontSize = FitTextFontSize(text, textMaxWidth, textMaxHeight, fontSize);

        Grid grid = new() { Width = width, Height = height };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(regionWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(regionWidth) });
        Image qr = new()
        {
            Source = image,
            Width = qrSize,
            Height = qrSize,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
        TextBlock label = CreateTextBlock(text, fittedFontSize, textMaxWidth);
        Grid.SetColumn(qr, 0);
        Grid.SetColumn(label, 2);
        grid.Children.Add(qr);
        grid.Children.Add(label);
        return new Border { Width = width, Height = height, ClipToBounds = true, Child = grid };
    }

    private static TextBlock CreateTextBlock(string text, double fontSize, double maxWidth)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = fontSize,
            FontWeight = FontWeights.Bold,
            MaxWidth = maxWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None,
        };
    }

    internal static string TruncateTextToWidth(string text, int maxDisplayLength, double maxWidth, double fontSize)
    {
        text ??= string.Empty;
        int limit = maxDisplayLength > 0 ? maxDisplayLength : 16;
        bool lengthTruncated = text.Length > limit;
        string value = lengthTruncated ? text[..limit] : text;
        string suffix = lengthTruncated ? "..." : string.Empty;
        while (value.Length > 0 && MeasureTextWidth(value + suffix, fontSize) > maxWidth)
        {
            value = value[..^1];
            suffix = "...";
        }

        if (value.Length == 0)
        {
            return MeasureTextWidth("...", fontSize) <= maxWidth ? "..." : string.Empty;
        }

        return value + suffix;
    }

    internal static double FitTextFontSize(
        string text,
        double maxWidth,
        double maxHeight,
        double maximumFontSize,
        Typeface? typeface = null)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0 || maxHeight <= 0 || maximumFontSize <= 1)
        {
            return Math.Max(Math.Min(maximumFontSize, 1), 0.1);
        }

        double lower = 0.1;
        double upper = maximumFontSize;
        for (int iteration = 0; iteration < 24; iteration++)
        {
            double candidate = (lower + upper) / 2d;
            (double Width, double Height) size = MeasureText(text, candidate, typeface ?? LabelTypeface);
            if (size.Width <= maxWidth && size.Height <= maxHeight)
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

    private static double MeasureTextWidth(string text, double fontSize)
    {
        return MeasureText(text, fontSize, LabelTypeface).Width;
    }

    private static (double Width, double Height) MeasureText(string text, double fontSize, Typeface typeface)
    {
        FormattedText formatted = new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            1);
        return (formatted.WidthIncludingTrailingWhitespace, formatted.Height);
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

    internal static IReadOnlyList<(double Left, double Top, double Width, double Height)> CalculateFourUpCellBounds(
        (double Left, double Top, double Width, double Height) bounds)
    {
        double gutter = 2 * UnitsPerMillimeter;
        double cellWidth = Math.Max((bounds.Width - gutter) / 2d, 1);
        double cellHeight = Math.Max((bounds.Height - gutter) / 2d, 1);
        return
        [
            (bounds.Left, bounds.Top, cellWidth, cellHeight),
            (bounds.Left + cellWidth + gutter, bounds.Top, cellWidth, cellHeight),
            (bounds.Left, bounds.Top + cellHeight + gutter, cellWidth, cellHeight),
            (bounds.Left + cellWidth + gutter, bounds.Top + cellHeight + gutter, cellWidth, cellHeight),
        ];
    }

    private static PrintTemplateProfile CreateCompatibilityTemplate(EdgeSettings settings, LabelSheetLayout layout)
    {
        return new PrintTemplateProfile
        {
            Id = settings.PrintTemplate,
            Type = "label",
            WidthMillimeters = settings.PrintWidthMillimeters,
            HeightMillimeters = settings.PrintHeightMillimeters,
            Orientation = settings.PrintOrientation,
            Mode = settings.PrintMode,
            LayoutStyle = PrintTemplatePolicy.StackedLayoutStyle,
            TextFontSizePoints = PrintTemplatePolicy.Is60x40Label(new PrintTemplateProfile
            {
                Type = "label",
                WidthMillimeters = settings.PrintWidthMillimeters,
                HeightMillimeters = settings.PrintHeightMillimeters,
            }) ? PrintTemplatePolicy.Stacked60x40FontSizePoints : 10,
            MaxDisplayLength = 16,
        };
    }
}
