// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

#pragma warning disable SA1402, SA1649

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fscm.Edge.Win.Models;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using DrawingPrinting = System.Drawing.Printing;

namespace Fscm.Edge.Win.Services;

internal enum PrintTransportKind
{
    WpfXps,
    Gdi,
}

internal sealed record GdiPaperCandidate(
    string Name,
    int RawKind,
    int WidthHundredthsInch,
    int HeightHundredthsInch)
{
    public double WidthMillimeters => WidthHundredthsInch * 0.254d;

    public double HeightMillimeters => HeightHundredthsInch * 0.254d;
}

internal sealed record PreparedPrintTarget(
    PrintTransportKind TransportKind,
    string PrinterName,
    PrintPageContext Context,
    PrintTicket? PrintTicket,
    GdiPaperCandidate? GdiPaper,
    int DpiX,
    int DpiY,
    int Copies,
    string Diagnostic);

internal static class PrintTargetService
{
    private const double GdiUnitsPerInch = 100d;
    private const double DipsPerGdiUnit = 96d / GdiUnitsPerInch;
    private const double PaperToleranceMillimeters = 1;

    public static PreparedPrintTarget Prepare(PrintQueue queue, EdgeSettings settings)
    {
        PrintJobDispatchPolicy.EnsureCopiesAllowed(settings.PrintCopies);
        Exception wpfFailure;
        try
        {
            PrintCapabilities capabilities = queue.GetPrintCapabilities(queue.DefaultPrintTicket);
            if (capabilities.PageMediaSizeCapability is not { Count: > 0 })
            {
                throw new InvalidOperationException("WPF 驱动未公布任何纸型。");
            }

            PreparedPrintPage prepared = PrintPageContextFactory.Prepare(queue, settings);
            string diagnostic = $"transport=wpf_xps; {prepared.Context.Diagnostic}";
            return new PreparedPrintTarget(
                PrintTransportKind.WpfXps,
                queue.Name,
                prepared.Context with { Diagnostic = diagnostic },
                prepared.PrintTicket,
                null,
                0,
                0,
                settings.PrintCopies,
                diagnostic);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PrintSystemException or Win32Exception)
        {
            wpfFailure = ex;
        }

        try
        {
            PreparedPrintTarget gdi = PrepareGdi(queue.Name, settings, wpfFailure.Message);
            Trace.WriteLine($"[print-target] printer={queue.Name}; {gdi.Diagnostic}");
            return gdi;
        }
        catch (Exception gdiFailure) when (gdiFailure is InvalidOperationException or Win32Exception)
        {
            throw new InvalidOperationException(
                $"WPF/XPS 未接受目标纸张：{wpfFailure.Message} GDI 也未找到可用纸型：{gdiFailure.Message}",
                gdiFailure);
        }
    }

    public static void Print(PrintQueue queue, PreparedPrintTarget target, FixedDocument document, string documentName)
    {
        PrintJobDispatchPolicy.EnsureCopiesAllowed(target.Copies);
        if (target.TransportKind == PrintTransportKind.WpfXps)
        {
            PrintDialog dialog = new()
            {
                PrintQueue = queue,
                PrintTicket = target.PrintTicket ?? throw new InvalidOperationException("WPF 打印票据为空。"),
            };
            dialog.PrintDocument(document.DocumentPaginator, documentName);
            return;
        }

        PrintGdi(target, document, documentName);
    }

    internal static GdiPaperCandidate? FindBestGdiPaper(
        IEnumerable<GdiPaperCandidate> papers,
        double requestedWidthMillimeters,
        double requestedHeightMillimeters)
    {
        return papers
            .Where(paper =>
                Math.Abs(paper.WidthMillimeters - requestedWidthMillimeters) <= PaperToleranceMillimeters &&
                Math.Abs(paper.HeightMillimeters - requestedHeightMillimeters) <= PaperToleranceMillimeters)
            .OrderBy(paper =>
                Math.Abs(paper.WidthMillimeters - requestedWidthMillimeters) +
                Math.Abs(paper.HeightMillimeters - requestedHeightMillimeters))
            .ThenBy(paper => paper.RawKind == (int)DrawingPrinting.PaperKind.Custom ? 1 : 0)
            .ThenBy(paper => paper.RawKind)
            .ThenBy(paper => paper.Name, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    internal static PrintPageContext CreateGdiContext(
        EdgeSettings settings,
        GdiPaperCandidate paper,
        Drawing.RectangleF printableAreaHundredthsInch,
        int dpiX,
        int dpiY,
        string wpfFailure)
    {
        bool landscape = string.Equals(settings.PrintOrientation, "landscape", StringComparison.OrdinalIgnoreCase);
        double physicalWidth = settings.PrintWidthMillimeters * PrintPageContextFactory.UnitsPerMillimeter;
        double physicalHeight = settings.PrintHeightMillimeters * PrintPageContextFactory.UnitsPerMillimeter;
        double logicalWidth = landscape ? physicalHeight : physicalWidth;
        double logicalHeight = landscape ? physicalWidth : physicalHeight;
        Rect imageable = NormalizeGdiPrintableArea(
            printableAreaHundredthsInch,
            physicalWidth,
            physicalHeight,
            landscape);
        PrintPageContext context = PrintPageContextFactory.Calculate(
            logicalWidth,
            logicalHeight,
            imageable,
            settings,
            paper.WidthMillimeters,
            paper.HeightMillimeters);
        string diagnostic =
            $"transport=gdi; paper={paper.Name}; raw_kind={paper.RawKind}; " +
            $"dpi={dpiX}x{dpiY}; driver_orientation=portrait; content_rotation={(landscape ? 90 : 0)}; " +
            $"wpf_fallback={SanitizeDiagnostic(wpfFailure)}; {context.Diagnostic}";
        return context with { Diagnostic = diagnostic };
    }

    internal static (int Width, int Height) CalculateRasterPixelSize(
        double pageWidthDips,
        double pageHeightDips,
        int dpiX,
        int dpiY)
    {
        return (
            Math.Max(1, (int)Math.Round(pageWidthDips * dpiX / 96d)),
            Math.Max(1, (int)Math.Round(pageHeightDips * dpiY / 96d)));
    }

    internal static (float TranslateX, float RotationDegrees, Drawing.RectangleF Destination) CalculateGdiDrawPlan(
        bool landscape,
        Drawing.Rectangle pageBounds)
    {
        return landscape
            ? (
                pageBounds.Width,
                90,
                new Drawing.RectangleF(0, 0, pageBounds.Height, pageBounds.Width))
            : (
                0,
                0,
                new Drawing.RectangleF(0, 0, pageBounds.Width, pageBounds.Height));
    }

    private static PreparedPrintTarget PrepareGdi(string printerName, EdgeSettings settings, string wpfFailure)
    {
        DrawingPrinting.PrinterSettings printer = new() { PrinterName = printerName };
        if (!printer.IsValid)
        {
            throw new InvalidOperationException($"GDI 无法访问打印机 {printerName}。");
        }

        List<GdiPaperCandidate> candidates = printer.PaperSizes
            .Cast<DrawingPrinting.PaperSize>()
            .Select(ToCandidate)
            .ToList();
        GdiPaperCandidate paper = FindBestGdiPaper(
            candidates,
            settings.PrintWidthMillimeters,
            settings.PrintHeightMillimeters)
            ?? throw new InvalidOperationException(
                $"打印机没有 {settings.PrintWidthMillimeters:0.##} x {settings.PrintHeightMillimeters:0.##} mm 的 GDI 纸型。");
        DrawingPrinting.PaperSize selectedPaper = ResolvePaperSize(printer, paper);
        DrawingPrinting.PageSettings page = new(printer)
        {
            PaperSize = selectedPaper,
            Landscape = false,
            Margins = new DrawingPrinting.Margins(0, 0, 0, 0),
        };
        DrawingPrinting.PrinterResolution? resolution = SelectResolution(printer);
        if (resolution is not null)
        {
            page.PrinterResolution = resolution;
        }

        int dpiX = NormalizeDpi(page.PrinterResolution.X, 203);
        int dpiY = NormalizeDpi(page.PrinterResolution.Y, dpiX);
        PrintPageContext context = CreateGdiContext(
            settings,
            paper,
            page.PrintableArea,
            dpiX,
            dpiY,
            wpfFailure);
        return new PreparedPrintTarget(
            PrintTransportKind.Gdi,
            printerName,
            context,
            null,
            paper,
            dpiX,
            dpiY,
            settings.PrintCopies,
            context.Diagnostic);
    }

    private static void PrintGdi(PreparedPrintTarget target, FixedDocument fixedDocument, string documentName)
    {
        GdiPaperCandidate paper = target.GdiPaper
            ?? throw new InvalidOperationException("GDI 打印纸型为空。");
        List<FixedPage> pages = fixedDocument.Pages
            .Select(page => page.Child)
            .Where(page => page is not null)
            .Cast<FixedPage>()
            .ToList();
        if (pages.Count == 0)
        {
            throw new InvalidOperationException("GDI 打印文档没有页面。");
        }

        using DrawingPrinting.PrintDocument printDocument = new();
        printDocument.PrinterSettings.PrinterName = target.PrinterName;
        if (!printDocument.PrinterSettings.IsValid)
        {
            throw new InvalidOperationException($"GDI 无法访问打印机 {target.PrinterName}。");
        }

        printDocument.PrinterSettings.Copies = (short)target.Copies;
        printDocument.PrinterSettings.Collate = true;
        printDocument.DocumentName = documentName;
        printDocument.OriginAtMargins = false;
        printDocument.PrintController = new DrawingPrinting.StandardPrintController();
        printDocument.DefaultPageSettings.PaperSize = ResolvePaperSize(printDocument.PrinterSettings, paper);
        printDocument.DefaultPageSettings.Landscape = false;
        printDocument.DefaultPageSettings.Margins = new DrawingPrinting.Margins(0, 0, 0, 0);
        DrawingPrinting.PrinterResolution? resolution = SelectResolution(printDocument.PrinterSettings, target.DpiX, target.DpiY);
        if (resolution is not null)
        {
            printDocument.DefaultPageSettings.PrinterResolution = resolution;
        }

        int pageIndex = 0;
        printDocument.PrintPage += (_, args) =>
        {
            using Drawing.Bitmap bitmap = RenderPage(pages[pageIndex], target.Context, target.DpiX, target.DpiY);
            Drawing.Graphics graphics = args.Graphics
                ?? throw new InvalidOperationException("GDI 打印机没有提供绘图上下文。");
            Drawing.Drawing2D.GraphicsState state = graphics.Save();
            try
            {
                graphics.PageUnit = Drawing.GraphicsUnit.Display;
                graphics.TranslateTransform(-args.PageSettings.HardMarginX, -args.PageSettings.HardMarginY);
                graphics.CompositingQuality = Drawing2D.CompositingQuality.HighQuality;
                graphics.InterpolationMode = Drawing2D.InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = Drawing2D.PixelOffsetMode.Half;
                var drawPlan = CalculateGdiDrawPlan(
                    target.Context.Orientation == "landscape",
                    args.PageBounds);
                if (drawPlan.RotationDegrees != 0)
                {
                    graphics.TranslateTransform(drawPlan.TranslateX, 0);
                    graphics.RotateTransform(drawPlan.RotationDegrees);
                }

                graphics.DrawImage(bitmap, drawPlan.Destination);
            }
            finally
            {
                graphics.Restore(state);
            }

            pageIndex++;
            args.HasMorePages = pageIndex < pages.Count;
        };
        printDocument.Print();
    }

    internal static Drawing.Bitmap RenderPage(FixedPage page, PrintPageContext context, int dpiX, int dpiY)
    {
        page.Measure(new Size(context.PageWidth, context.PageHeight));
        page.Arrange(new Rect(0, 0, context.PageWidth, context.PageHeight));
        page.UpdateLayout();
        (int pixelWidth, int pixelHeight) = CalculateRasterPixelSize(
            context.PageWidth,
            context.PageHeight,
            dpiX,
            dpiY);
        RenderTargetBitmap target = new(pixelWidth, pixelHeight, dpiX, dpiY, PixelFormats.Pbgra32);
        target.Render(page);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using MemoryStream stream = new();
        encoder.Save(stream);
        stream.Position = 0;
        using Drawing.Bitmap source = new(stream);
        return new Drawing.Bitmap(source);
    }

    internal static Rect NormalizeGdiPrintableArea(
        Drawing.RectangleF printable,
        double physicalWidth,
        double physicalHeight,
        bool landscape)
    {
        double left = Math.Clamp(printable.Left * DipsPerGdiUnit, 0, physicalWidth);
        double top = Math.Clamp(printable.Top * DipsPerGdiUnit, 0, physicalHeight);
        double right = Math.Clamp(printable.Right * DipsPerGdiUnit, left, physicalWidth);
        double bottom = Math.Clamp(printable.Bottom * DipsPerGdiUnit, top, physicalHeight);
        Rect physical = new(left, top, right - left, bottom - top);
        if (!landscape)
        {
            return physical;
        }

        double rightMargin = Math.Max(physicalWidth - physical.Right, 0);
        double bottomMargin = Math.Max(physicalHeight - physical.Bottom, 0);
        return new Rect(
            physical.Top,
            rightMargin,
            Math.Max(physicalHeight - physical.Top - bottomMargin, 0),
            Math.Max(physicalWidth - rightMargin - physical.Left, 0));
    }

    private static GdiPaperCandidate ToCandidate(DrawingPrinting.PaperSize paper)
    {
        return new GdiPaperCandidate(paper.PaperName, paper.RawKind, paper.Width, paper.Height);
    }

    private static DrawingPrinting.PaperSize ResolvePaperSize(
        DrawingPrinting.PrinterSettings printer,
        GdiPaperCandidate candidate)
    {
        return printer.PaperSizes
            .Cast<DrawingPrinting.PaperSize>()
            .Where(paper => paper.RawKind == candidate.RawKind &&
                paper.Width == candidate.WidthHundredthsInch &&
                paper.Height == candidate.HeightHundredthsInch)
            .OrderBy(paper => string.Equals(paper.PaperName, candidate.Name, StringComparison.Ordinal) ? 0 : 1)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"打印机纸型 {candidate.Name} 已不可用，请刷新打印机配置。");
    }

    private static DrawingPrinting.PrinterResolution? SelectResolution(
        DrawingPrinting.PrinterSettings printer,
        int preferredX = 0,
        int preferredY = 0)
    {
        return printer.PrinterResolutions
            .Cast<DrawingPrinting.PrinterResolution>()
            .Where(resolution => resolution.X > 0 && resolution.Y > 0)
            .OrderBy(resolution => preferredX > 0
                ? Math.Abs(resolution.X - preferredX) + Math.Abs(resolution.Y - preferredY)
                : 0)
            .ThenByDescending(resolution => resolution.X * resolution.Y)
            .FirstOrDefault();
    }

    private static int NormalizeDpi(int value, int fallback)
    {
        return value is >= 96 and <= 2400 ? value : fallback;
    }

    private static string SanitizeDiagnostic(string value)
    {
        return value.Replace(';', ',').ReplaceLineEndings(" ").Trim();
    }
}
