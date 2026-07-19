// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Windows;
using Fscm.Edge.Win.Models;
using Fscm.Edge.Win.Services;
using Xunit;

namespace Fscm.Edge.Win.UnitTests;

public sealed class PrintPageContextFactoryTests
{
    private const double UnitsPerMillimeter = PrintPageContextFactory.UnitsPerMillimeter;

    [Fact]
    public void CalculateUsesAsymmetricImageableOriginAndKeepsContentInsideSafeArea()
    {
        EdgeSettings settings = Settings(100, 150);
        Rect imageable = MillimeterRect(2, 3, 96, 144);

        PrintPageContext context = PrintPageContextFactory.Calculate(
            100 * UnitsPerMillimeter,
            150 * UnitsPerMillimeter,
            imageable,
            settings,
            100,
            150);

        Assert.Equal(3.5 * UnitsPerMillimeter, context.SafeArea.Left, 3);
        Assert.Equal(4.5 * UnitsPerMillimeter, context.SafeArea.Top, 3);
        Assert.True(context.ContentLeft >= context.SafeArea.Left);
        Assert.True(context.ContentTop >= context.SafeArea.Top);
        Assert.True(context.ContentLeft + (context.DesignWidth * context.Scale) <= context.SafeArea.Right + 0.01);
        Assert.True(context.ContentTop + (context.DesignHeight * context.Scale) <= context.SafeArea.Bottom + 0.01);
        Assert.True(context.HasWarning);
    }

    [Fact]
    public void CalculateReservesCalibrationSpaceBeforeScaling()
    {
        EdgeSettings settings = Settings(100, 150);
        settings.PrintOffsetXMillimeters = 2;
        settings.PrintOffsetYMillimeters = -1;

        PrintPageContext context = PrintPageContextFactory.Calculate(
            100 * UnitsPerMillimeter,
            150 * UnitsPerMillimeter,
            MillimeterRect(0, 0, 100, 150),
            settings,
            100,
            150);

        Assert.True(context.ContentLeft >= context.SafeArea.Left);
        Assert.True(context.ContentTop >= context.SafeArea.Top);
        Assert.True(context.ContentLeft + (context.DesignWidth * context.Scale) <= context.SafeArea.Right + 0.01);
        Assert.True(context.ContentTop + (context.DesignHeight * context.Scale) <= context.SafeArea.Bottom + 0.01);
    }

    [Fact]
    public void CalculateRejectsImageableAreaBelowNinetyPercent()
    {
        EdgeSettings settings = Settings(100, 150);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PrintPageContextFactory.Calculate(
                100 * UnitsPerMillimeter,
                150 * UnitsPerMillimeter,
                MillimeterRect(10, 0, 80, 150),
                settings,
                100,
                150));

        Assert.Contains("90", error.Message);
    }

    [Fact]
    public void NormalizeImageableAreaKeepsLogicalLandscapeCoordinates()
    {
        double logicalWidth = 150 * UnitsPerMillimeter;
        double logicalHeight = 100 * UnitsPerMillimeter;
        Rect area = new(
            2 * UnitsPerMillimeter,
            3 * UnitsPerMillimeter,
            146 * UnitsPerMillimeter,
            94 * UnitsPerMillimeter);

        Rect normalized = PrintPageContextFactory.NormalizeImageableArea(
            area,
            logicalWidth,
            logicalHeight,
            100 * UnitsPerMillimeter,
            150 * UnitsPerMillimeter,
            landscape: true);

        Assert.Equal(area.X, normalized.Left, 3);
        Assert.Equal(area.Y, normalized.Top, 3);
        Assert.Equal(area.Width, normalized.Width, 3);
        Assert.Equal(area.Height, normalized.Height, 3);
    }

    [Fact]
    public void NormalizeImageableAreaRejectsMismatchedDriverCoordinates()
    {
        Rect area = new(0, 0, 210 * UnitsPerMillimeter, 297 * UnitsPerMillimeter);

        Assert.Throws<InvalidOperationException>(() => PrintPageContextFactory.NormalizeImageableArea(
            area,
            100 * UnitsPerMillimeter,
            150 * UnitsPerMillimeter,
            100 * UnitsPerMillimeter,
            150 * UnitsPerMillimeter,
            landscape: false));
    }

    [Fact]
    public void ValidateAcceptedMediaRejectsDriverPaperSubstitution()
    {
        EdgeSettings settings = Settings(100, 150);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PrintPageContextFactory.ValidateAcceptedMedia(
                settings,
                210 * UnitsPerMillimeter,
                297 * UnitsPerMillimeter,
                landscape: false));

        Assert.Contains("210", error.Message);
        Assert.Contains("297", error.Message);
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

    private static Rect MillimeterRect(double x, double y, double width, double height)
    {
        return new Rect(
            x * UnitsPerMillimeter,
            y * UnitsPerMillimeter,
            width * UnitsPerMillimeter,
            height * UnitsPerMillimeter);
    }
}
