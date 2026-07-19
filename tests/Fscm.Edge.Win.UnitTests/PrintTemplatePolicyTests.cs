// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Fscm.Edge.Win.Models;
using Fscm.Edge.Win.Services;
using Xunit;

namespace Fscm.Edge.Win.UnitTests;

public sealed class PrintTemplatePolicyTests
{
    [Fact]
    public void SelectAutomaticLabelTemplate_Prefers60x40Before100x150AndOtherLabels()
    {
        PrintTemplateProfile other = Label("other", 75, 50);
        PrintTemplateProfile large = Label("large", 100, 150);
        PrintTemplateProfile small = Label("small", 60, 40);

        PrintTemplateProfile? selected = PrintTemplatePolicy.SelectAutomaticLabelTemplate(
            [other, large, small],
            explicitTemplateId: null,
            defaultTemplateId: "large");

        Assert.Same(small, selected);
        Assert.Same(large, PrintTemplatePolicy.SelectAutomaticLabelTemplate([other, large], null, null));
        Assert.Same(other, PrintTemplatePolicy.SelectAutomaticLabelTemplate([other, large, small], "other", null));
    }

    [Fact]
    public void GetLabelSheetLayout_UsesFourUpOnlyFor100x150PortraitLabels()
    {
        Assert.Equal(LabelSheetLayout.FourUpRepeated, PrintTemplatePolicy.GetLabelSheetLayout(Label("large", 100.05, 149.95)));

        PrintTemplateProfile landscape = Label("landscape", 100, 150);
        landscape.Orientation = "landscape";
        Assert.Equal(LabelSheetLayout.Single, PrintTemplatePolicy.GetLabelSheetLayout(landscape));

        PrintTemplateProfile rotated = Label("rotated", 150, 100);
        Assert.Equal(LabelSheetLayout.Single, PrintTemplatePolicy.GetLabelSheetLayout(rotated));

        PrintTemplateProfile shipping = Label("shipping", 100, 150);
        shipping.Type = "shipping";
        Assert.Equal(LabelSheetLayout.Single, PrintTemplatePolicy.GetLabelSheetLayout(shipping));
    }

    [Fact]
    public void MigrateBuiltInTemplates_AppendsMissingPresetWithoutChangingExistingProfiles()
    {
        PrintTemplateProfile existing = Label("custom-large", 100, 150);
        existing.Printer = "Configured Printer";
        existing.LabelQrPrefix = "BOX-";
        var templates = new List<PrintTemplateProfile> { existing };

        bool changed = PrintTemplatePolicy.MigrateBuiltInTemplates(templates, sourceVersion: 1);

        Assert.True(changed);
        Assert.Equal("Configured Printer", existing.Printer);
        Assert.Equal("BOX-", existing.LabelQrPrefix);
        Assert.Contains(templates, template => template.Id == "label_100x150mm" && template.Type == "label");
        Assert.Contains(templates, template => template.Id == "manufacturer_box_mark_100x150mm" && template.Type == "manufacturer_box_mark");
    }

    [Fact]
    public void MigrateBuiltInTemplates_DoesNotOverwriteExistingStablePreset()
    {
        PrintTemplateProfile existing = Label("label_100x150mm", 100, 150);
        existing.Name = "Warehouse four-up";
        existing.Printer = "Configured Printer";
        var templates = new List<PrintTemplateProfile> { existing };

        bool changed = PrintTemplatePolicy.MigrateBuiltInTemplates(templates, sourceVersion: 1);

        Assert.True(changed);
        Assert.Equal(4, templates.Count);
        Assert.Equal("Warehouse four-up", existing.Name);
        Assert.Equal("Configured Printer", existing.Printer);
        Assert.Contains(templates, template => template.Id == "manufacturer_box_mark_100x150mm");
        Assert.Contains(templates, template => template.Id == "label_60x40mm_horizontal");
        Assert.Contains(templates, template => template.Id == "location_100x150mm_landscape");
    }

    [Fact]
    public void MigrateBuiltInTemplates_NormalizesOnlyTheStablePresetType()
    {
        PrintTemplateProfile existing = Label("label_100x150mm", 100, 150);
        existing.Type = "custom";
        existing.Name = "Warehouse four-up";
        existing.Printer = "Configured Printer";
        var templates = new List<PrintTemplateProfile> { existing };

        bool changed = PrintTemplatePolicy.MigrateBuiltInTemplates(templates, sourceVersion: 1);

        Assert.True(changed);
        Assert.Equal("label", existing.Type);
        Assert.Equal("Warehouse four-up", existing.Name);
        Assert.Equal("Configured Printer", existing.Printer);
    }

    [Fact]
    public void MigrateBuiltInTemplates_AddsBoxMarkPresetAndCopiesFourUpPrinter()
    {
        PrintTemplateProfile fourUp = Label("label_100x150mm", 100, 150);
        fourUp.Printer = "Four-up Printer";
        var templates = new List<PrintTemplateProfile> { fourUp };

        bool changed = PrintTemplatePolicy.MigrateBuiltInTemplates(templates, sourceVersion: 2);

        Assert.True(changed);
        Assert.Equal("label", fourUp.Type);
        Assert.Equal("Four-up Printer", fourUp.Printer);
        PrintTemplateProfile boxMark = Assert.Single(templates, template => template.Id == "manufacturer_box_mark_100x150mm");
        Assert.Equal("manufacturer_box_mark", boxMark.Type);
        Assert.Equal(100, boxMark.WidthMillimeters);
        Assert.Equal(150, boxMark.HeightMillimeters);
        Assert.Equal("portrait", boxMark.Orientation);
        Assert.Equal("Four-up Printer", boxMark.Printer);
    }

    [Fact]
    public void CalculateFourUpCellBounds_ReturnsFourEvenlySpacedCells()
    {
        IReadOnlyList<(double Left, double Top, double Width, double Height)> cells = QrPrintService.CalculateFourUpCellBounds((10, 20, 200, 300));

        Assert.Equal(4, cells.Count);
        Assert.Equal(cells[0].Width, cells[1].Width);
        Assert.Equal(cells[0].Height, cells[2].Height);
        Assert.Equal(cells[0].Left, cells[2].Left);
        Assert.Equal(cells[0].Top, cells[1].Top);
        Assert.True(cells[1].Left > cells[0].Left + cells[0].Width);
        Assert.True(cells[2].Top > cells[0].Top + cells[0].Height);
    }

    [Fact]
    public void MigrateBuiltInTemplates_AddsHorizontalPresetAndCopiesConfigured60x40Values()
    {
        PrintTemplateProfile stacked = Label("label_60x40mm", 60, 40);
        stacked.Printer = "Zebra";
        stacked.LabelQrPrefix = "BOX-";
        stacked.OffsetXMillimeters = 1.5;
        stacked.TextFontSizePoints = 0;
        var templates = new List<PrintTemplateProfile> { stacked };

        bool changed = PrintTemplatePolicy.MigrateBuiltInTemplates(templates, sourceVersion: 3);

        Assert.True(changed);
        Assert.Equal(PrintTemplatePolicy.StackedLayoutStyle, stacked.LayoutStyle);
        Assert.Equal(14, stacked.TextFontSizePoints);
        PrintTemplateProfile horizontal = Assert.Single(templates, template => template.Id == "label_60x40mm_horizontal");
        Assert.Equal("Zebra", horizontal.Printer);
        Assert.Equal("BOX-", horizontal.LabelQrPrefix);
        Assert.Equal(1.5, horizontal.OffsetXMillimeters);
        Assert.Equal(PrintTemplatePolicy.HorizontalLayoutStyle, horizontal.LayoutStyle);
        Assert.Equal(16, horizontal.TextFontSizePoints);
    }

    [Fact]
    public void TruncateTextToWidth_UsesEllipsisWithoutOverflowingTargetWidth()
    {
        string value = QrPrintService.TruncateTextToWidth("ABCDEFGHIJKLMNOPQRSTUVWXYZ", 16, 80, 24);

        Assert.EndsWith("...", value);
        Assert.True(value.Length < 19);
    }

    [Fact]
    public void GetTemplateVersion_UsesStableCrossPlatformContract()
    {
        PrintTemplateProfile template = Label("label_60x40mm", 60, 40);
        template.Printer = "Zebra";
        template.Mode = "fit";
        template.Copies = 1;
        template.SkuQrPrefix = "T";
        template.LabelQrPrefix = "BOX-";
        template.LayoutStyle = PrintTemplatePolicy.HorizontalLayoutStyle;
        template.TextFontSizePoints = 18;
        template.MaxDisplayLength = 16;

        Assert.Equal("bcab7f8d55da", PrintTemplatePolicy.GetTemplateVersion(template));
    }

    [Fact]
    public void MigrateBuiltInTemplates_V8UsesSafeFitAndClampsCalibration()
    {
        PrintTemplateProfile template = Label("label_60x40mm", 60, 40);
        template.Mode = "fill";
        template.OffsetXMillimeters = 20;
        template.OffsetYMillimeters = -20;
        template.SafetyInsetMillimeters = 0;
        var templates = new List<PrintTemplateProfile> { template };

        bool changed = PrintTemplatePolicy.MigrateBuiltInTemplates(templates, sourceVersion: 7);

        Assert.True(changed);
        Assert.Equal("fit", template.Mode);
        Assert.Equal(5, template.OffsetXMillimeters);
        Assert.Equal(-5, template.OffsetYMillimeters);
        Assert.Equal(PrintPageContextFactory.DefaultSafetyInsetMillimeters, template.SafetyInsetMillimeters);
    }

    [Fact]
    public void MigrateBuiltInTemplates_V9DerivesOrientationAndModeFromLayout()
    {
        PrintTemplateProfile location = Label("location", 100, 150);
        location.LayoutStyle = PrintTemplatePolicy.LocationCodeLayoutStyle;
        location.Orientation = "portrait";
        location.Mode = "fill";
        PrintTemplateProfile label = Label("label", 60, 40);
        label.LayoutStyle = PrintTemplatePolicy.StackedLayoutStyle;
        label.Orientation = "landscape";
        label.Mode = "actual_size";
        var templates = new List<PrintTemplateProfile> { location, label };

        bool changed = PrintTemplatePolicy.MigrateBuiltInTemplates(templates, sourceVersion: 8);

        Assert.True(changed);
        Assert.Equal("landscape", location.Orientation);
        Assert.Equal("fit", location.Mode);
        Assert.Equal("portrait", label.Orientation);
        Assert.Equal("fit", label.Mode);
    }

    [Theory]
    [InlineData(PrintTemplatePolicy.LocationCodeLayoutStyle, "landscape")]
    [InlineData(PrintTemplatePolicy.StackedLayoutStyle, "portrait")]
    [InlineData(PrintTemplatePolicy.HorizontalLayoutStyle, "portrait")]
    public void GetAutomaticOrientation_UsesOnlyLocationLayout(string layoutStyle, string expected)
    {
        Assert.Equal(expected, PrintTemplatePolicy.GetAutomaticOrientation(layoutStyle));
    }

    [Fact]
    public void MigrateBuiltInTemplates_V10Reduces60x40FontSizes()
    {
        PrintTemplateProfile stacked = Label("stacked", 60, 40);
        stacked.LayoutStyle = PrintTemplatePolicy.StackedLayoutStyle;
        stacked.TextFontSizePoints = 16;
        PrintTemplateProfile horizontal = Label("horizontal", 60, 40);
        horizontal.LayoutStyle = PrintTemplatePolicy.HorizontalLayoutStyle;
        horizontal.TextFontSizePoints = 18;
        PrintTemplateProfile otherSize = Label("other", 100, 150);
        otherSize.TextFontSizePoints = 18;
        var templates = new List<PrintTemplateProfile> { stacked, horizontal, otherSize };

        bool changed = PrintTemplatePolicy.MigrateBuiltInTemplates(templates, sourceVersion: 9);

        Assert.True(changed);
        Assert.Equal(14, stacked.TextFontSizePoints);
        Assert.Equal(16, horizontal.TextFontSizePoints);
        Assert.Equal(18, otherSize.TextFontSizePoints);
    }

    [Fact]
    public void MigrateBuiltInTemplates_AddsLocationTemplateAndCopies100x150PrinterCalibration()
    {
        PrintTemplateProfile source = Label("label_100x150mm", 100, 150);
        source.Printer = "Location Printer";
        source.OffsetXMillimeters = -1.25;
        var templates = new List<PrintTemplateProfile> { source };

        bool changed = PrintTemplatePolicy.MigrateBuiltInTemplates(templates, sourceVersion: 4);

        Assert.True(changed);
        PrintTemplateProfile location = Assert.Single(templates, template => template.Id == "location_100x150mm_landscape");
        Assert.Equal("Location Printer", location.Printer);
        Assert.Equal(-1.25, location.OffsetXMillimeters);
        Assert.Equal("landscape", location.Orientation);
        Assert.Equal(PrintTemplatePolicy.LocationCodeLayoutStyle, location.LayoutStyle);
        Assert.Equal(28, location.TextFontSizePoints);
        Assert.Equal(12, location.MaxDisplayLength);
        Assert.Empty(location.LabelQrPrefix);
    }

    [Fact]
    public void MigrateBuiltInTemplates_V6CopiesPrinterToUnconfiguredBoxMark()
    {
        PrintTemplateProfile source = Label("label_100x150mm", 100, 150);
        source.Printer = "Configured 100x150 Printer";
        source.OffsetXMillimeters = 1.75;
        PrintTemplateProfile boxMark = new()
        {
            Id = "manufacturer_box_mark_100x150mm",
            Name = "Box mark",
            Type = "manufacturer_box_mark",
            WidthMillimeters = 100,
            HeightMillimeters = 150,
            Orientation = "portrait",
        };
        var templates = new List<PrintTemplateProfile> { source, boxMark };

        bool changed = PrintTemplatePolicy.MigrateBuiltInTemplates(templates, sourceVersion: 5);

        Assert.True(changed);
        Assert.Equal("Configured 100x150 Printer", boxMark.Printer);
        Assert.Equal(1.75, boxMark.OffsetXMillimeters);

        boxMark.Printer = "Dedicated Box Mark Printer";
        boxMark.OffsetXMillimeters = -2;
        changed = PrintTemplatePolicy.MigrateBuiltInTemplates(templates, sourceVersion: 5);
        Assert.False(changed);
        Assert.Equal("Dedicated Box Mark Printer", boxMark.Printer);
        Assert.Equal(-2, boxMark.OffsetXMillimeters);
    }

    [Fact]
    public void MigrateBuiltInTemplates_V7AssignsStableUserFacingNumbers()
    {
        PrintTemplateProfile stacked = Label("label_60x40mm", 60, 40);
        PrintTemplateProfile shipping = new() { Id = "shipping_100x150mm", Type = "shipping" };
        PrintTemplateProfile custom = Label("warehouse_custom", 80, 50);
        var templates = new List<PrintTemplateProfile> { custom, shipping, stacked };

        bool changed = PrintTemplatePolicy.MigrateBuiltInTemplates(templates, sourceVersion: 6);

        Assert.True(changed);
        Assert.Equal("T01", stacked.TemplateNumber);
        Assert.Equal("T05", shipping.TemplateNumber);
        Assert.Equal("T08", custom.TemplateNumber);
        Assert.Equal("T09", PrintTemplatePolicy.NextTemplateNumber(templates));
    }

    [Fact]
    public void NextTemplateNumber_DoesNotReuseExistingOrRenumberTemplates()
    {
        PrintTemplateProfile first = Label("first", 60, 40);
        first.TemplateNumber = "T08";
        PrintTemplateProfile third = Label("third", 60, 40);
        third.TemplateNumber = "T10";

        Assert.Equal("T11", PrintTemplatePolicy.NextTemplateNumber([first, third]));
        Assert.Equal("T08", first.TemplateNumber);
        Assert.Equal("T10", third.TemplateNumber);
    }

    [Fact]
    public void OrderManufacturerBoxMarkTemplates_KeepsUnavailableTemplatesAndPrioritizesAvailablePrinter()
    {
        PrintTemplateProfile unconfigured = BoxMark("unconfigured", string.Empty);
        PrintTemplateProfile offline = BoxMark("offline", "Offline Printer");
        PrintTemplateProfile online = BoxMark("online", "Online Printer");
        PrintTemplateProfile wrongType = Label("label", 100, 150);

        IReadOnlyList<PrintTemplateProfile> ordered = PrintTemplatePolicy.OrderManufacturerBoxMarkTemplates(
            [unconfigured, offline, online, wrongType],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Online Printer" });

        Assert.Equal(["online", "offline", "unconfigured"], ordered.Select(template => template.Id));
    }

    [Fact]
    public void LocationCodeLayout_UsesFourQrRegionsAndKeepsFullQrPayload()
    {
        double width = 146 * QrPrintService.UnitsPerMillimeter;
        double height = 96 * QrPrintService.UnitsPerMillimeter;
        (double SideWidth, double CenterWidth, double QrRowHeight, double ColumnGap, double RowGap) layout =
            QrPrintService.CalculateLocationCodeLayout(width, height);
        PrintTemplateProfile template = Label("location", 100, 150);
        template.LayoutStyle = PrintTemplatePolicy.LocationCodeLayoutStyle;

        Assert.Equal(33, layout.SideWidth / QrPrintService.UnitsPerMillimeter, 3);
        Assert.Equal(76, layout.CenterWidth / QrPrintService.UnitsPerMillimeter, 3);
        Assert.Equal((96 - 4) / 2d, layout.QrRowHeight / QrPrintService.UnitsPerMillimeter, 3);
        Assert.Equal("ABCDEFGHIJKLMNO", QrPrintService.ResolveQrPayload("PREFIX-ABCDEFGHIJKLMNO", "ABCDEFGHIJKLMNO", template));
    }

    [Theory]
    [InlineData("A-01", 28)]
    [InlineData("ABCDEFGHIJKL", 18)]
    public void LocationCodeFont_FitsWithinConfiguredRange(string text, double minimumExpected)
    {
        double points = QrPrintService.FitLocationCodeFontSizePoints(
            text,
            76 * QrPrintService.UnitsPerMillimeter,
            28,
            18);

        Assert.InRange(points, minimumExpected, 28);
    }

    private static PrintTemplateProfile Label(string id, double width, double height)
    {
        return new PrintTemplateProfile
        {
            Id = id,
            Type = "label",
            WidthMillimeters = width,
            HeightMillimeters = height,
            Orientation = "portrait",
        };
    }

    private static PrintTemplateProfile BoxMark(string id, string printer)
    {
        return new PrintTemplateProfile
        {
            Id = id,
            Name = id,
            Type = "manufacturer_box_mark",
            Printer = printer,
            WidthMillimeters = 100,
            HeightMillimeters = 150,
            Orientation = "portrait",
        };
    }
}
