// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Printing;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using Fscm.Edge.Win.Models;
using Fscm.Edge.Win.Services;
using Xunit;
using Drawing = System.Drawing;

namespace Fscm.Edge.Win.UnitTests;

public sealed class PrintTargetServiceTests
{
    [Fact]
    public void FindBestGdiPaperSelectsExactVendorPaperDeterministically()
    {
        GdiPaperCandidate[] papers =
        [
            new("Custom 76 x 127", 256, 299, 500),
            new("SF 100 x 150", 270, 394, 591),
            new("Postal 100 x 150", 260, 394, 591),
            new("100 x 152", 284, 394, 598),
        ];

        GdiPaperCandidate? selected = PrintTargetService.FindBestGdiPaper(papers, 100, 150);

        Assert.NotNull(selected);
        Assert.Equal(260, selected.RawKind);
        Assert.Equal("Postal 100 x 150", selected.Name);
    }

    [Fact]
    public void FindBestGdiPaperDoesNotTreatThreeByFiveAs100By150()
    {
        GdiPaperCandidate[] papers =
        [
            new("Custom 76 x 127", 256, 299, 500),
            new("Custom 72 x 130", 256, 283, 512),
        ];

        Assert.Null(PrintTargetService.FindBestGdiPaper(papers, 100, 150));
    }

    [Fact]
    public void CreateGdiContextClampsDriverRoundingAndRetainsSafeFit()
    {
        EdgeSettings settings = Settings(100, 150);
        GdiPaperCandidate paper = new("Postal 100 x 150", 260, 394, 591);
        Drawing.RectangleF printable = new(0.49f, 0.49f, 394.09f, 591.13f);

        PrintPageContext context = PrintTargetService.CreateGdiContext(
            settings,
            paper,
            printable,
            203,
            203,
            "driver returned 76 x 127 mm");

        Assert.Equal(100 * PrintPageContextFactory.UnitsPerMillimeter, context.PageWidth, 3);
        Assert.Equal(150 * PrintPageContextFactory.UnitsPerMillimeter, context.PageHeight, 3);
        Assert.InRange(context.Scale, 0.95, 1);
        Assert.Contains("transport=gdi", context.Diagnostic);
        Assert.Contains("raw_kind=260", context.Diagnostic);
        Assert.Contains("dpi=203x203", context.Diagnostic);
    }

    [Fact]
    public void CreateGdiContextUsesLogicalLandscapePageOnce()
    {
        EdgeSettings settings = Settings(100, 150);
        settings.PrintOrientation = "landscape";
        GdiPaperCandidate paper = new("Postal 100 x 150", 260, 394, 591);

        PrintPageContext context = PrintTargetService.CreateGdiContext(
            settings,
            paper,
            new Drawing.RectangleF(0, 0, 394, 591),
            203,
            203,
            "WPF media unavailable");

        Assert.Equal(150 * PrintPageContextFactory.UnitsPerMillimeter, context.PageWidth, 3);
        Assert.Equal(100 * PrintPageContextFactory.UnitsPerMillimeter, context.PageHeight, 3);
        Assert.Equal("landscape", context.Orientation);
        Assert.Contains("driver_orientation=portrait", context.Diagnostic);
        Assert.Contains("content_rotation=90", context.Diagnostic);
    }

    [Fact]
    public void NormalizeGdiPrintableAreaRotatesAsymmetricPortraitMarginsIntoLogicalLandscape()
    {
        const double UnitsPerMillimeter = PrintPageContextFactory.UnitsPerMillimeter;
        Drawing.RectangleF printable = new(
            (float)(2 / 0.254d),
            (float)(3 / 0.254d),
            (float)(96 / 0.254d),
            (float)(144 / 0.254d));

        Rect logical = PrintTargetService.NormalizeGdiPrintableArea(
            printable,
            100 * UnitsPerMillimeter,
            150 * UnitsPerMillimeter,
            landscape: true);

        Assert.Equal(3 * UnitsPerMillimeter, logical.X, 2);
        Assert.Equal(2 * UnitsPerMillimeter, logical.Y, 2);
        Assert.Equal(144 * UnitsPerMillimeter, logical.Width, 2);
        Assert.Equal(96 * UnitsPerMillimeter, logical.Height, 2);
    }

    [Theory]
    [InlineData(203, 799, 1199)]
    [InlineData(300, 1181, 1772)]
    public void CalculateRasterPixelSizeUsesPhysicalPrinterDpi(int dpi, int expectedWidth, int expectedHeight)
    {
        (int width, int height) = PrintTargetService.CalculateRasterPixelSize(
            100 * PrintPageContextFactory.UnitsPerMillimeter,
            150 * PrintPageContextFactory.UnitsPerMillimeter,
            dpi,
            dpi);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Fact]
    public void CalculateGdiDrawPlanRotatesLogicalLandscapeIntoPortraitPaper()
    {
        (float TranslateX, float RotationDegrees, Drawing.RectangleF Destination) plan = PrintTargetService.CalculateGdiDrawPlan(
            landscape: true,
            new Drawing.Rectangle(0, 0, 394, 591));

        Assert.Equal(394, plan.TranslateX);
        Assert.Equal(90, plan.RotationDegrees);
        Assert.Equal(591, plan.Destination.Width);
        Assert.Equal(394, plan.Destination.Height);
    }

    [Fact]
    public void PrepareUsesGdiForInstalledHprtLegacyDriverWithoutPrinting()
    {
        const string PrinterName = "HPRT N41";
        Drawing.Printing.PrinterSettings gdi = new() { PrinterName = PrinterName };
        if (!gdi.IsValid)
        {
            return;
        }

        try
        {
            using LocalPrintServer server = new();
            using PrintQueueCollection queues = server.GetPrintQueues();
            PrintQueue? queue = queues.FirstOrDefault(candidate => candidate.Name == PrinterName);
            if (queue is null)
            {
                return;
            }

            using (queue)
            {
                EdgeSettings settings = Settings(100, 150);
                settings.DefaultPrinter = PrinterName;
                PreparedPrintTarget target = PrintTargetService.Prepare(queue, settings);

                Assert.Equal(PrintTransportKind.Gdi, target.TransportKind);
                Assert.NotNull(target.GdiPaper);
                Assert.Equal(260, target.GdiPaper.RawKind);
                Assert.InRange(target.GdiPaper.WidthMillimeters, 99, 101);
                Assert.InRange(target.GdiPaper.HeightMillimeters, 149, 151);
                Assert.Equal(203, target.DpiX);
                Assert.Contains("transport=gdi", target.Diagnostic);

                settings.PrintOrientation = "landscape";
                PreparedPrintTarget landscapeTarget = PrintTargetService.Prepare(queue, settings);
                Assert.Equal(PrintTransportKind.Gdi, landscapeTarget.TransportKind);
                Assert.Equal(150 * PrintPageContextFactory.UnitsPerMillimeter, landscapeTarget.Context.PageWidth, 3);
                Assert.Equal(100 * PrintPageContextFactory.UnitsPerMillimeter, landscapeTarget.Context.PageHeight, 3);
                Assert.Contains("driver_orientation=portrait", landscapeTarget.Diagnostic);
                Assert.Contains("content_rotation=90", landscapeTarget.Diagnostic);
            }
        }
        catch (PrintSystemException)
        {
            // The test remains portable to agents where the queue is installed but inaccessible.
        }
    }

    [Fact]
    public void RenderPageCreatesNonBlank203DpiManufacturerBitmap()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                EdgeSettings settings = Settings(100, 150);
                PrintPageContext context = PrintPageContextFactory.CreateNominal(settings);
                FixedDocument document = new ManufacturerBoxMarkPrintService().CreateDocument(
                    settings,
                    [ManufacturerBoxMarkPrintService.CreatePreviewSample()]);
                FixedPage page = Assert.IsType<FixedPage>(document.Pages[0].Child);

                using Drawing.Bitmap bitmap = PrintTargetService.RenderPage(page, context, 203, 203);

                Assert.Equal(799, bitmap.Width);
                Assert.Equal(1199, bitmap.Height);
                int nonWhiteSamples = 0;
                for (int y = 0; y < bitmap.Height; y += 20)
                {
                    for (int x = 0; x < bitmap.Width; x += 20)
                    {
                        Drawing.Color pixel = bitmap.GetPixel(x, y);
                        if (pixel.R < 245 || pixel.G < 245 || pixel.B < 245)
                        {
                            nonWhiteSamples++;
                        }
                    }
                }

                Assert.True(nonWhiteSamples > 20);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static EdgeSettings Settings(double width, double height)
    {
        return new EdgeSettings
        {
            PrintWidthMillimeters = width,
            PrintHeightMillimeters = height,
            PrintOrientation = "portrait",
            PrintSafetyInsetMillimeters = 1.5,
        };
    }
}
