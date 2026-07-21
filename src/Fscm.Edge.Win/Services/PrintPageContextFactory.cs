// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

#pragma warning disable SA1402, SA1649

using System.Diagnostics;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Fscm.Edge.Win.Models;

namespace Fscm.Edge.Win.Services;

internal sealed record PrintPageContext(
    double PageWidth,
    double PageHeight,
    Rect ImageableArea,
    Rect SafeArea,
    double Scale,
    double ContentLeft,
    double ContentTop,
    double DesignWidth,
    double DesignHeight,
    double RequestedWidthMillimeters,
    double RequestedHeightMillimeters,
    double AcceptedWidthMillimeters,
    double AcceptedHeightMillimeters,
    string Orientation,
    string Diagnostic,
    bool HasWarning)
{
    public FixedPage Place(FrameworkElement design)
    {
        design.Width = DesignWidth;
        design.Height = DesignHeight;
        design.RenderTransformOrigin = new Point(0, 0);
        design.RenderTransform = new ScaleTransform(Scale, Scale);
        RenderOptions.SetBitmapScalingMode(design, BitmapScalingMode.NearestNeighbor);
        FixedPage.SetLeft(design, ContentLeft);
        FixedPage.SetTop(design, ContentTop);

        FixedPage page = new()
        {
            Width = PageWidth,
            Height = PageHeight,
            Background = Brushes.White,
            ClipToBounds = true,
        };
        page.Children.Add(design);
        return page;
    }
}

internal sealed record PreparedPrintPage(PrintTicket PrintTicket, PrintPageContext Context);

internal static class PrintPageContextFactory
{
    internal const double UnitsPerMillimeter = 96d / 25.4d;
    internal const double DefaultSafetyInsetMillimeters = 1.5;
    internal const double MinimumScale = 0.90;
    internal const double WarningScale = 0.95;
    private const double MediaToleranceMillimeters = 1;

    public static PreparedPrintPage Prepare(PrintQueue queue, EdgeSettings settings)
    {
        ValidateSettings(settings);

        bool landscape = IsLandscape(settings.PrintOrientation);
        double requestedWidth = settings.PrintWidthMillimeters * UnitsPerMillimeter;
        double requestedHeight = settings.PrintHeightMillimeters * UnitsPerMillimeter;
        PrintTicket baseTicket = queue.DefaultPrintTicket.Clone();
        PrintCapabilities initialCapabilities = queue.GetPrintCapabilities(baseTicket);
        PageMediaSize requestedMedia = FindSupportedMedia(initialCapabilities, requestedWidth, requestedHeight)
            ?? new PageMediaSize(PageMediaSizeName.Unknown, requestedWidth, requestedHeight);

        PrintTicket delta = new()
        {
            PageMediaSize = requestedMedia,
            PageOrientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait,
            CopyCount = settings.PrintCopies,
            PageScalingFactor = 100,
        };
        System.Printing.ValidationResult validation = queue.MergeAndValidatePrintTicket(baseTicket, delta);
        PrintTicket acceptedTicket = validation.ValidatedPrintTicket;
        PageMediaSize? acceptedMedia = acceptedTicket.PageMediaSize;
        if (acceptedMedia?.Width is not double acceptedWidth || acceptedMedia.Height is not double acceptedHeight)
        {
            throw new InvalidOperationException("打印机驱动没有返回有效纸张尺寸，无法保证内容不会越界。");
        }

        ValidateAcceptedMedia(settings, acceptedWidth, acceptedHeight, landscape);
        PageOrientation expectedOrientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
        if (acceptedTicket.PageOrientation != expectedOrientation)
        {
            throw new InvalidOperationException($"打印机驱动将纸张方向改为 {acceptedTicket.PageOrientation?.ToString() ?? "unknown"}，请关闭驱动中的自动旋转后重试。");
        }

        if (acceptedTicket.PageScalingFactor is int scalingFactor && scalingFactor != 100)
        {
            throw new InvalidOperationException($"打印机驱动将输出缩放改为 {scalingFactor}%，请关闭驱动中的自动适应纸张后重试。");
        }

        PrintCapabilities acceptedCapabilities = queue.GetPrintCapabilities(acceptedTicket);
        PageImageableArea? imageable = acceptedCapabilities.PageImageableArea;
        double logicalWidth = landscape ? requestedHeight : requestedWidth;
        double logicalHeight = landscape ? requestedWidth : requestedHeight;
        Rect normalizedImageable = NormalizeImageableArea(
            imageable,
            logicalWidth,
            logicalHeight,
            requestedWidth,
            requestedHeight,
            landscape);
        PrintPageContext context = Calculate(
            logicalWidth,
            logicalHeight,
            normalizedImageable,
            settings,
            acceptedWidth / UnitsPerMillimeter,
            acceptedHeight / UnitsPerMillimeter);

        Trace.WriteLine($"[print-layout] printer={queue.Name}; conflict={validation.ConflictStatus}; {context.Diagnostic}");
        return new PreparedPrintPage(acceptedTicket, context);
    }

    public static PrintPageContext CreateNominal(EdgeSettings settings)
    {
        ValidateSettings(settings);
        bool landscape = IsLandscape(settings.PrintOrientation);
        double physicalWidth = settings.PrintWidthMillimeters * UnitsPerMillimeter;
        double physicalHeight = settings.PrintHeightMillimeters * UnitsPerMillimeter;
        double logicalWidth = landscape ? physicalHeight : physicalWidth;
        double logicalHeight = landscape ? physicalWidth : physicalHeight;
        return Calculate(
            logicalWidth,
            logicalHeight,
            new Rect(0, 0, logicalWidth, logicalHeight),
            settings,
            settings.PrintWidthMillimeters,
            settings.PrintHeightMillimeters);
    }

    internal static PrintPageContext Calculate(
        double logicalWidth,
        double logicalHeight,
        Rect imageableArea,
        EdgeSettings settings,
        double acceptedWidthMillimeters,
        double acceptedHeightMillimeters)
    {
        if (!IsUsableRect(imageableArea, logicalWidth, logicalHeight))
        {
            throw new InvalidOperationException("打印机驱动返回的可打印区域无效，已阻止打印以避免裁切。");
        }

        double insetMillimeters = settings.PrintSafetyInsetMillimeters > 0
            ? settings.PrintSafetyInsetMillimeters
            : DefaultSafetyInsetMillimeters;
        double inset = Math.Clamp(insetMillimeters, 0.5, 5) * UnitsPerMillimeter;
        Rect safeArea = Deflate(imageableArea, inset, inset);
        double offsetX = Math.Clamp(settings.PrintOffsetXMillimeters, -5, 5) * UnitsPerMillimeter;
        double offsetY = Math.Clamp(settings.PrintOffsetYMillimeters, -5, 5) * UnitsPerMillimeter;
        Rect scaleArea = Deflate(safeArea, Math.Abs(offsetX), Math.Abs(offsetY));
        double scale = Math.Min(1, Math.Min(scaleArea.Width / logicalWidth, scaleArea.Height / logicalHeight));
        if (!double.IsFinite(scale) || scale < MinimumScale)
        {
            throw new InvalidOperationException(
                $"打印机可打印区域只能容纳模板的 {Math.Max(scale, 0):P1}，低于安全下限 {MinimumScale:P0}。请检查驱动纸张尺寸和边距。");
        }

        double scaledWidth = logicalWidth * scale;
        double scaledHeight = logicalHeight * scale;
        double left = safeArea.Left + ((safeArea.Width - scaledWidth) / 2) + offsetX;
        double top = safeArea.Top + ((safeArea.Height - scaledHeight) / 2) + offsetY;
        left = Math.Clamp(left, safeArea.Left, Math.Max(safeArea.Left, safeArea.Right - scaledWidth));
        top = Math.Clamp(top, safeArea.Top, Math.Max(safeArea.Top, safeArea.Bottom - scaledHeight));
        bool warning = scale < WarningScale;
        string diagnostic =
            $"status={(warning ? "warning_scale_below_95" : "ok")}; " +
            $"requested={settings.PrintWidthMillimeters:0.##}x{settings.PrintHeightMillimeters:0.##}mm; " +
            $"accepted={acceptedWidthMillimeters:0.##}x{acceptedHeightMillimeters:0.##}mm; " +
            $"orientation={(IsLandscape(settings.PrintOrientation) ? "landscape" : "portrait")}; " +
            $"imageable={FormatRectMillimeters(imageableArea)}; safe={FormatRectMillimeters(safeArea)}; " +
            $"scale={scale:P1}; offset={settings.PrintOffsetXMillimeters:0.##},{settings.PrintOffsetYMillimeters:0.##}mm";

        return new PrintPageContext(
            logicalWidth,
            logicalHeight,
            imageableArea,
            safeArea,
            scale,
            left,
            top,
            logicalWidth,
            logicalHeight,
            settings.PrintWidthMillimeters,
            settings.PrintHeightMillimeters,
            acceptedWidthMillimeters,
            acceptedHeightMillimeters,
            IsLandscape(settings.PrintOrientation) ? "landscape" : "portrait",
            diagnostic,
            warning);
    }

    internal static Rect NormalizeImageableArea(
        PageImageableArea? area,
        double logicalWidth,
        double logicalHeight,
        double physicalWidth,
        double physicalHeight,
        bool landscape)
    {
        if (area is null || !AllFinite(area.OriginWidth, area.OriginHeight, area.ExtentWidth, area.ExtentHeight))
        {
            throw new InvalidOperationException("打印机驱动没有提供可打印区域，请先在 Windows 中配置正确的标签纸型。");
        }

        Rect raw = new(area.OriginWidth, area.OriginHeight, area.ExtentWidth, area.ExtentHeight);
        return NormalizeImageableArea(raw, logicalWidth, logicalHeight, physicalWidth, physicalHeight, landscape);
    }

    internal static Rect NormalizeImageableArea(
        Rect raw,
        double logicalWidth,
        double logicalHeight,
        double physicalWidth,
        double physicalHeight,
        bool landscape)
    {
        if (IsUsableRect(raw, logicalWidth, logicalHeight))
        {
            return raw;
        }

        if (landscape && IsUsableRect(raw, physicalWidth, physicalHeight))
        {
            double left = raw.Left;
            double right = Math.Max(physicalWidth - raw.Right, 0);
            double top = raw.Top;
            double bottom = Math.Max(physicalHeight - raw.Bottom, 0);
            double horizontalMargin = Math.Max(top, bottom);
            double verticalMargin = Math.Max(left, right);
            return new Rect(
                horizontalMargin,
                verticalMargin,
                logicalWidth - (horizontalMargin * 2),
                logicalHeight - (verticalMargin * 2));
        }

        throw new InvalidOperationException("打印机驱动返回的可打印区域与请求纸张不一致，请检查纸张尺寸和自动旋转设置。");
    }

    private static PageMediaSize? FindSupportedMedia(PrintCapabilities capabilities, double requestedWidth, double requestedHeight)
    {
        return capabilities.PageMediaSizeCapability?
            .Where(size => size.Width.HasValue && size.Height.HasValue)
            .OrderBy(size => Math.Abs(size.Width!.Value - requestedWidth) + Math.Abs(size.Height!.Value - requestedHeight))
            .FirstOrDefault(size => IsCloseMillimeters(size.Width!.Value, requestedWidth) && IsCloseMillimeters(size.Height!.Value, requestedHeight));
    }

    internal static void ValidateAcceptedMedia(EdgeSettings settings, double width, double height, bool landscape)
    {
        double requestedWidth = settings.PrintWidthMillimeters * UnitsPerMillimeter;
        double requestedHeight = settings.PrintHeightMillimeters * UnitsPerMillimeter;
        bool direct = IsCloseMillimeters(width, requestedWidth) && IsCloseMillimeters(height, requestedHeight);
        bool oriented = landscape && IsCloseMillimeters(width, requestedHeight) && IsCloseMillimeters(height, requestedWidth);
        if (!direct && !oriented)
        {
            throw new InvalidOperationException(
                $"打印机驱动未接受 {settings.PrintWidthMillimeters:0.##} x {settings.PrintHeightMillimeters:0.##} mm 纸张，" +
                $"实际返回 {width / UnitsPerMillimeter:0.##} x {height / UnitsPerMillimeter:0.##} mm。请在驱动中创建正确纸型。");
        }
    }

    private static void ValidateSettings(EdgeSettings settings)
    {
        if (settings.PrintWidthMillimeters <= 0 || settings.PrintHeightMillimeters <= 0)
        {
            throw new InvalidOperationException("打印纸张尺寸必须大于 0。");
        }
    }

    private static bool IsCloseMillimeters(double left, double right)
    {
        return Math.Abs(left - right) / UnitsPerMillimeter <= MediaToleranceMillimeters;
    }

    private static bool IsUsableRect(Rect rect, double width, double height)
    {
        const double tolerance = 1;
        return AllFinite(rect.X, rect.Y, rect.Width, rect.Height) &&
            rect.X >= 0 && rect.Y >= 0 && rect.Width > 10 && rect.Height > 10 &&
            rect.Right <= width + tolerance && rect.Bottom <= height + tolerance;
    }

    private static Rect Deflate(Rect rect, double horizontal, double vertical)
    {
        double width = rect.Width - (horizontal * 2);
        double height = rect.Height - (vertical * 2);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("打印机可打印区域小于安全边距，无法打印。");
        }

        return new Rect(rect.X + horizontal, rect.Y + vertical, width, height);
    }

    private static string FormatRectMillimeters(Rect rect)
    {
        return $"{rect.X / UnitsPerMillimeter:0.##},{rect.Y / UnitsPerMillimeter:0.##}," +
            $"{rect.Width / UnitsPerMillimeter:0.##}x{rect.Height / UnitsPerMillimeter:0.##}mm";
    }

    private static bool AllFinite(params double[] values) => values.All(double.IsFinite);

    private static bool IsLandscape(string orientation) =>
        string.Equals(orientation, "landscape", StringComparison.OrdinalIgnoreCase);
}
