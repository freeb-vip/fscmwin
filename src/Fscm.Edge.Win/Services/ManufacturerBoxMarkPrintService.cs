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

public sealed class ManufacturerBoxMarkPrintService
{
    private const double UnitsPerMillimeter = 96d / 25.4d;

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

        double width = settings.PrintWidthMillimeters * UnitsPerMillimeter;
        double height = settings.PrintHeightMillimeters * UnitsPerMillimeter;

        using LocalPrintServer server = new();
        using PrintQueue queue = server.GetPrintQueue(settings.DefaultPrinter);
        PrintDialog dialog = new()
        {
            PrintQueue = queue,
            PrintTicket = queue.DefaultPrintTicket.Clone(),
        };
        dialog.PrintTicket.PageMediaSize = new PageMediaSize(PageMediaSizeName.Unknown, width, height);
        dialog.PrintTicket.PageOrientation = PageOrientation.Portrait;
        dialog.PrintTicket.CopyCount = Math.Clamp(settings.PrintCopies, 1, 99);

        FixedDocument document = new();
        foreach (ManufacturerBoxMark mark in job.BoxMarks)
        {
            document.Pages.Add(new PageContent
            {
                Child = CreatePage(mark, width, height, settings.PrintOffsetXMillimeters),
            });
        }

        document.DocumentPaginator.PageSize = new Size(width, height);
        dialog.PrintDocument(document.DocumentPaginator, $"FSCM 厂家箱唛 - {job.Id}");
    }

    private static FixedPage CreatePage(ManufacturerBoxMark mark, double width, double height, double offsetMillimeters)
    {
        double margin = 4 * UnitsPerMillimeter;
        Grid grid = new()
        {
            Width = width - (margin * 2),
            Height = height - (margin * 2),
            Background = Brushes.White,
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(CreateText(mark.SeaMark, 16, FontWeights.Bold, 0));
        grid.Children.Add(CreateText($"{mark.Shop}  {mark.StyleCode}", 13, FontWeights.Bold, 1));

        StackPanel fields = new() { Margin = new Thickness(0, 6, 0, 4) };
        fields.Children.Add(CreateText($"SKU: {string.Join(" / ", mark.SkuLines)}", 11, FontWeights.SemiBold));
        fields.Children.Add(CreateText($"SPEC: {mark.Spec}", 11, FontWeights.Normal));
        fields.Children.Add(CreateText($"PCS: {mark.Pcs}", 11, FontWeights.Normal));
        fields.Children.Add(CreateText($"CTN: {mark.SkuBoxs}   BATCH: {mark.Batch}", 11, FontWeights.Normal));
        fields.Children.Add(CreateText($"INBOUND: {mark.InboundCode}", 10, FontWeights.Normal));
        Grid.SetRow(fields, 2);
        grid.Children.Add(fields);

        StackPanel codes = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        codes.Children.Add(CreateCode(mark.SkuQrPayload, "SKU"));
        codes.Children.Add(CreateCode(mark.BoxQrPayload, "BOX"));
        Grid.SetRow(codes, 3);
        grid.Children.Add(codes);

        FixedPage page = new() { Width = width, Height = height, Background = Brushes.White };
        FixedPage.SetLeft(grid, Math.Max(margin + (offsetMillimeters * UnitsPerMillimeter), 0));
        FixedPage.SetTop(grid, margin);
        page.Children.Add(grid);
        return page;
    }

    private static TextBlock CreateText(string value, double size, FontWeight weight, int row = -1)
    {
        TextBlock text = new()
        {
            Text = value ?? string.Empty,
            FontSize = size,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (row >= 0)
        {
            Grid.SetRow(text, row);
        }

        return text;
    }

    private static StackPanel CreateCode(string value, string title)
    {
        StackPanel panel = new()
        {
            Margin = new Thickness(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        panel.Children.Add(new Image
        {
            Source = CreateQrImage(value),
            Width = 86,
            Height = 86,
            Stretch = Stretch.Uniform,
        });
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        return panel;
    }

    private static BitmapImage CreateQrImage(string value)
    {
        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(
            string.IsNullOrWhiteSpace(value) ? "-" : value,
            QRCodeGenerator.ECCLevel.M);
        using PngByteQRCode qr = new(data);
        using MemoryStream stream = new(qr.GetGraphic(6));

        BitmapImage image = new();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
