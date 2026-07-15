// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Globalization;
using System.Windows;
using Fscm.Edge.Win.Models;

namespace Fscm.Edge.Win;

public partial class PrintPreviewWindow : Window
{
    private const double PreviewMaxWidth = 720;
    private const double PreviewMaxHeight = 480;

    public PrintPreviewWindow(EdgeSettings settings)
    {
        InitializeComponent();

        double width = Math.Max(settings.PrintWidthMillimeters, 1);
        double height = Math.Max(settings.PrintHeightMillimeters, 1);
        bool landscape = string.Equals(settings.PrintOrientation, "landscape", StringComparison.OrdinalIgnoreCase);
        (width, height) = OrientPageSize(width, height, landscape);
        double scale = Math.Min(PreviewMaxWidth / width, PreviewMaxHeight / height);
        PreviewPaper.Width = Math.Max(width * scale, 160);
        PreviewPaper.Height = Math.Max(height * scale, 120);

        string size = $"{width.ToString("0.##", CultureInfo.InvariantCulture)} x {height.ToString("0.##", CultureInfo.InvariantCulture)} mm";
        PreviewDescriptionText.Text = $"{size} · 标签纸预览 · 内容按纸张边界缩放显示";
        PreviewSizeText.Text = $"实际页面：{size}\n方向：{(landscape ? "横向（40 x 60 mm）" : "纵向（60 x 40 mm）")}";
        PreviewPrinterText.Text = $"打印机：{settings.DefaultPrinter}";
        PreviewHintText.Text = $"确认页面内容完整显示在蓝色边界内后，再点击确认打印。水平校准：{settings.PrintOffsetXMillimeters:0.##} mm";
    }

    private static (double Width, double Height) OrientPageSize(double width, double height, bool landscape)
    {
        return landscape ? (height, width) : (width, height);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnConfirmPrintClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}