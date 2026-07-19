// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fscm.Edge.Win.Models;
using Fscm.Edge.Win.Services;
using Xunit;

namespace Fscm.Edge.Win.UnitTests;

public sealed class ManufacturerBoxMarkPrintServiceTests
{
    private const double UnitsPerMillimeter = 96d / 25.4d;

    [Fact]
    public void CreatePageMatchesBackend100x150Layout()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                const double Width = 100 * UnitsPerMillimeter;
                const double Height = 150 * UnitsPerMillimeter;
                ManufacturerBoxMark mark = new()
                {
                    SeaMark = "DA999",
                    Shop = "1F2601AF90BF",
                    StyleCode = "TB15",
                    SkuLines = ["TB15-Black-XL x 400"],
                    Spec = "35*13*26",
                    Pcs = "400 / 60x35x60cm",
                    SkuBoxs = "1/2",
                    Batch = "20260718 (1/44)",
                    SkuQrPayload = "TB15-Black-XL",
                    BoxQrPayload = "AB12CD34EF56,TB15-Black-XL=400",
                };

                FixedPage page = ManufacturerBoxMarkPrintService.CreatePage(mark, Width, Height, 0);
                Assert.Equal(Width, page.Width, 3);
                Assert.Equal(Height, page.Height, 3);
                Canvas layout = Assert.IsType<Canvas>(Assert.Single(page.Children));
                List<Image> qrImages = layout.Children.OfType<Image>().ToList();
                Assert.Equal(3, qrImages.Count);
                Assert.Equal(18, layout.Children.OfType<Border>().Count());
                string[] texts = Descendants<TextBlock>(layout).Select(text => text.Text).ToArray();
                foreach (string title in new[]
                {
                    "海运唛头", "SEA MARK", "箱唛码", "BOX MARK", "产品编码", "STYLE CODE",
                    "SKU", "规格", "SPEC", "数量/箱规", "PCS/CARTON", "箱序", "BOX INDEX", "日期/批次", "DATE/BATCH",
                })
                {
                    Assert.Contains(title, texts);
                }

                Assert.Contains("1F2601AF90BF", texts);
                Assert.Contains("TB15-Black-XL x 400", texts);
                Assert.Contains("400 / 60x35x60cm", texts);
                Assert.Contains("20260718 (1/44)", texts);
                Assert.True(Canvas.GetTop(qrImages[0]) < Canvas.GetTop(qrImages[1]));
                Assert.True(Canvas.GetTop(qrImages[1]) < Canvas.GetTop(qrImages[2]));
                Assert.All(qrImages, image => Assert.InRange(Canvas.GetTop(image) + image.Height, 0, Height));

                Border firstKey = Assert.IsType<Border>(layout.Children[1]);
                double expectedTop = 26.33 * UnitsPerMillimeter;
                Assert.InRange(Canvas.GetTop(firstKey), expectedTop - 1, expectedTop + 1);
                SolidColorBrush yellow = Assert.IsType<SolidColorBrush>(firstKey.Background);
                Assert.Equal(Color.FromRgb(0xFF, 0xF7, 0x99), yellow.Color);

                mark.SkuLines = ["TB15-Black-XL x 120", "TB15-White-L x 140", "TB15-Blue-M x 140"];
                FixedPage mixedPage = ManufacturerBoxMarkPrintService.CreatePage(mark, Width, Height, 0);
                Canvas mixedLayout = Assert.IsType<Canvas>(Assert.Single(mixedPage.Children));
                Border mixedSkuCell = Assert.IsType<Border>(mixedLayout.Children[7]);
                Assert.True(mixedSkuCell.Height > 10.6 * UnitsPerMillimeter);
                Assert.Equal(3, mixedLayout.Children.OfType<Image>().Count());

                EdgeSettings settings = new()
                {
                    PrintWidthMillimeters = 100,
                    PrintHeightMillimeters = 150,
                    PrintOrientation = "portrait",
                };
                FixedDocument document = new ManufacturerBoxMarkPrintService().CreateDocument(settings, [mark, mark]);
                Assert.Equal(2, document.Pages.Count);
                Assert.Equal(Width, document.DocumentPaginator.PageSize.Width, 3);
                Assert.Equal(Height, document.DocumentPaginator.PageSize.Height, 3);

                InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() =>
                    ManufacturerBoxMarkPrintService.CreatePage(new ManufacturerBoxMark(), Width, Height, 0));
                Assert.Contains("箱唛码", missing.Message);

                page.Measure(new Size(Width, Height));
                page.Arrange(new Rect(0, 0, Width, Height));
                page.UpdateLayout();
                string? outputPath = Environment.GetEnvironmentVariable("FSCM_BOX_MARK_PREVIEW_PATH");
                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    SavePreview(page, Width, Height, outputPath);
                }
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

    [Fact]
    public void QrPayloadsUseCompatibleFallbacks()
    {
        ManufacturerBoxMark mark = new()
        {
            BoxUid = "BOX-UID-001",
            InboundCode = "IN-001",
            SkuLines = ["SKU-RED-XL x 24"],
        };

        Assert.Equal("BOX-UID-001", ManufacturerBoxMarkPrintService.ResolveBoxMarkCode(mark));
        Assert.Equal("BOX-UID-001", ManufacturerBoxMarkPrintService.ResolveBoxQrPayload(mark));
        Assert.Equal("SKU-RED-XL", ManufacturerBoxMarkPrintService.ResolveSkuQrPayload(mark));

        mark.BoxQrPayload = "BOX-QR";
        mark.SkuQrPayload = "SKU-QR";
        Assert.Equal("BOX-QR", ManufacturerBoxMarkPrintService.ResolveBoxQrPayload(mark));
        Assert.Equal("SKU-QR", ManufacturerBoxMarkPrintService.ResolveSkuQrPayload(mark));
    }

    [Fact]
    public void PreviewSampleContainsAllRequiredBoxMarkContent()
    {
        ManufacturerBoxMark sample = ManufacturerBoxMarkPrintService.CreatePreviewSample();

        Assert.NotEmpty(ManufacturerBoxMarkPrintService.ResolveBoxMarkCode(sample));
        Assert.NotEmpty(ManufacturerBoxMarkPrintService.ResolveBoxQrPayload(sample));
        Assert.NotEmpty(ManufacturerBoxMarkPrintService.ResolveSkuQrPayload(sample));
        Assert.NotEmpty(sample.SkuLines);
        Assert.Contains("20260718", sample.Batch);
        Assert.Contains("400", sample.Pcs);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void SavePreview(FrameworkElement page, double width, double height, string path)
    {
        const double Scale = 2;
        DrawingVisual visual = new();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(page), null, new Rect(0, 0, width * Scale, height * Scale));
        }

        RenderTargetBitmap bitmap = new((int)Math.Ceiling(width * Scale), (int)Math.Ceiling(height * Scale), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Path.GetTempPath());
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
