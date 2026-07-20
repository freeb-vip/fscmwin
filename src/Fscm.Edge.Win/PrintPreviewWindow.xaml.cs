// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using Fscm.Edge.Win.Models;
using Fscm.Edge.Win.Services;

namespace Fscm.Edge.Win;

public partial class PrintPreviewWindow : Window
{
    public PrintPreviewWindow(
        EdgeSettings settings,
        PrintTemplateProfile template,
        string qrPayload,
        string displayText)
        : this(
            CreateLabelPreview(settings, template, qrPayload, displayText),
            settings,
            template)
    {
    }

    private PrintPreviewWindow(
        (FixedDocument Document, string Diagnostic) preview,
        EdgeSettings settings,
        PrintTemplateProfile template)
        : this(
            preview.Document,
            "标签打印预览",
            BuildLabelDescription(settings, template),
            preview.Diagnostic,
            showConfirmButton: true)
    {
    }

    public PrintPreviewWindow(
        FixedDocument document,
        string title,
        string description,
        string hint,
        bool showConfirmButton,
        string confirmButtonText = "确认打印")
    {
        InitializeComponent();
        Title = title;
        PreviewTitleText.Text = title;
        PreviewDescriptionText.Text = description;
        PreviewHintText.Text = hint;
        PreviewViewer.Document = document;
        ConfirmPrintButton.Visibility = showConfirmButton ? Visibility.Visible : Visibility.Collapsed;
        ConfirmPrintButton.Content = confirmButtonText;
        CancelButton.Content = showConfirmButton ? "返回修改" : "关闭";
    }

    private static string BuildLabelDescription(EdgeSettings settings, PrintTemplateProfile template)
    {
        double width = Math.Max(settings.PrintWidthMillimeters, 1);
        double height = Math.Max(settings.PrintHeightMillimeters, 1);
        bool landscape = string.Equals(settings.PrintOrientation, "landscape", StringComparison.OrdinalIgnoreCase);
        (width, height) = landscape ? (height, width) : (width, height);
        string size = $"{width.ToString("0.##", CultureInfo.InvariantCulture)} x {height.ToString("0.##", CultureInfo.InvariantCulture)} mm";
        string layoutStyle = PrintTemplatePolicy.NormalizeLayoutStyle(template.LayoutStyle);
        string layout = layoutStyle switch
        {
            PrintTemplatePolicy.HorizontalLayoutStyle => "左右排版",
            PrintTemplatePolicy.LocationCodeLayoutStyle => "库位码四码排版",
            _ => "上下排版",
        };
        string fontSize = layoutStyle == PrintTemplatePolicy.LocationCodeLayoutStyle
            ? "自动最大字号"
            : $"{PrintTemplatePolicy.GetTextFontSizePoints(template):0.##} pt";
        return $"{template.Name} · {size} · {layout} · {fontSize}";
    }

    private static (FixedDocument Document, string Diagnostic) CreateLabelPreview(
        EdgeSettings settings,
        PrintTemplateProfile template,
        string qrPayload,
        string displayText)
    {
        FixedDocument document = new QrPrintService().CreatePreviewDocument(
            settings,
            template,
            qrPayload,
            displayText,
            out string diagnostic);
        return (document, diagnostic);
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
