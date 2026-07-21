// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Fscm.Edge.Win.Models;

namespace Fscm.Edge.Win.Services;

public enum LabelSheetLayout
{
    Single,
    FourUpRepeated,
}

public static class PrintTemplatePolicy
{
    private const double SizeToleranceMillimeters = 0.1;
    private static readonly IReadOnlyDictionary<string, string> BuiltInTemplateNumbers =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["label_60x40mm"] = "T01",
            ["label_60x40mm_horizontal"] = "T02",
            ["label_100x150mm"] = "T03",
            ["location_100x150mm_landscape"] = "T04",
            ["shipping_100x150mm"] = "T05",
            ["manufacturer_box_mark_100x150mm"] = "T06",
            ["custom"] = "T07",
            ["manufacturer_box_mark_quad_100x150mm"] = "T08",
        };

    public const string StackedLayoutStyle = "stacked";
    public const string HorizontalLayoutStyle = "qr_left_text_right";
    public const string LocationCodeLayoutStyle = "location_code_quad_qr";
    public const string BoxMarkQuadLayoutStyle = "box_mark_quad_qr";
    public const int RestrictedDisplayTextLength = 12;
    public const double Stacked60x40FontSizePoints = 14;
    public const double Horizontal60x40FontSizePoints = 16;
    public const double LocationCodeFontSizePoints = 28;
    public const double LocationCodeMinimumFontSizePoints = 18;

    public static bool IsLabel(PrintTemplateProfile template)
    {
        return string.Equals(template.Type, "label", StringComparison.OrdinalIgnoreCase);
    }

    public static bool Is60x40Label(PrintTemplateProfile template)
    {
        return IsLabel(template) && MatchesSize(template, 60, 40);
    }

    public static bool Is100x150PortraitLabel(PrintTemplateProfile template)
    {
        return IsLabel(template) &&
            MatchesSize(template, 100, 150) &&
            string.Equals(template.Orientation, "portrait", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsManufacturerBoxMark(PrintTemplateProfile template)
    {
        return string.Equals(template.Type, "manufacturer_box_mark", StringComparison.OrdinalIgnoreCase) &&
            MatchesSize(template, 100, 150) &&
            string.Equals(template.Orientation, "portrait", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<PrintTemplateProfile> OrderManufacturerBoxMarkTemplates(
        IEnumerable<PrintTemplateProfile> templates,
        IReadOnlySet<string> availablePrinters)
    {
        return templates
            .Select((template, index) => new { Template = template, Index = index })
            .Where(item => IsManufacturerBoxMark(item.Template))
            .OrderBy(item => IsExplicitPrinterAvailable(item.Template, availablePrinters) ? 0 : 1)
            .ThenBy(item => string.IsNullOrWhiteSpace(item.Template.Printer) ? 1 : 0)
            .ThenBy(item => item.Template.SortOrder > 0 ? item.Template.SortOrder : int.MaxValue)
            .ThenBy(item => item.Index)
            .Select(item => item.Template)
            .ToList();
    }

    public static LabelSheetLayout GetLabelSheetLayout(PrintTemplateProfile template)
    {
        return Is100x150PortraitLabel(template)
            ? LabelSheetLayout.FourUpRepeated
            : LabelSheetLayout.Single;
    }

    public static string NormalizeLayoutStyle(string? layoutStyle)
    {
        if (string.Equals(layoutStyle, LocationCodeLayoutStyle, StringComparison.OrdinalIgnoreCase))
        {
            return LocationCodeLayoutStyle;
        }

        if (string.Equals(layoutStyle, BoxMarkQuadLayoutStyle, StringComparison.OrdinalIgnoreCase))
        {
            return BoxMarkQuadLayoutStyle;
        }

        return string.Equals(layoutStyle, HorizontalLayoutStyle, StringComparison.OrdinalIgnoreCase)
            ? HorizontalLayoutStyle
            : StackedLayoutStyle;
    }

    public static bool HasRestrictedDisplayText(PrintTemplateProfile template)
    {
        string layoutStyle = NormalizeLayoutStyle(template.LayoutStyle);
        return layoutStyle is HorizontalLayoutStyle or LocationCodeLayoutStyle;
    }

    public static int GetMaxDisplayLength(PrintTemplateProfile template)
    {
        return HasRestrictedDisplayText(template)
            ? RestrictedDisplayTextLength
            : template.MaxDisplayLength > 0 ? template.MaxDisplayLength : 16;
    }

    public static string? GetDisplayTextValidationError(PrintTemplateProfile template, string? displayText)
    {
        string layoutStyle = NormalizeLayoutStyle(template.LayoutStyle);
        if (layoutStyle is not (HorizontalLayoutStyle or LocationCodeLayoutStyle) ||
            CountUnicodeScalars(displayText ?? string.Empty) <= RestrictedDisplayTextLength)
        {
            return null;
        }

        return layoutStyle == LocationCodeLayoutStyle
            ? "库位码最多 12 个字符，当前内容不适配，请输入少于或等于 12 个字符。"
            : "左右排版右侧文字最多 12 个字符，当前内容不适配，请输入少于或等于 12 个字符。";
    }

    private static int CountUnicodeScalars(string value)
    {
        int count = 0;
        using var enumerator = value.EnumerateRunes().GetEnumerator();
        while (enumerator.MoveNext())
        {
            count++;
        }
        return count;
    }

    public static string GetAutomaticOrientation(string? layoutStyle)
    {
        return NormalizeLayoutStyle(layoutStyle) == LocationCodeLayoutStyle
            ? "landscape"
            : "portrait";
    }

    public static double GetTextFontSizePoints(PrintTemplateProfile template)
    {
        if (template.TextFontSizePoints > 0)
        {
            return template.TextFontSizePoints;
        }

        if (Is60x40Label(template))
        {
            return NormalizeLayoutStyle(template.LayoutStyle) == HorizontalLayoutStyle
                ? Horizontal60x40FontSizePoints
                : Stacked60x40FontSizePoints;
        }

        if (NormalizeLayoutStyle(template.LayoutStyle) == LocationCodeLayoutStyle)
        {
            return LocationCodeFontSizePoints;
        }

        return 10;
    }

    public static string GetTemplateVersion(PrintTemplateProfile template)
    {
        string canonical = string.Join(
            '|',
            template.Id.Trim(),
            template.Type.Trim().ToLowerInvariant(),
            template.Printer.Trim(),
            template.WidthMillimeters.ToString("F3", CultureInfo.InvariantCulture),
            template.HeightMillimeters.ToString("F3", CultureInfo.InvariantCulture),
            template.Orientation.Trim().ToLowerInvariant(),
            template.Mode.Trim().ToLowerInvariant(),
            template.Copies.ToString(CultureInfo.InvariantCulture),
            template.OffsetXMillimeters.ToString("F3", CultureInfo.InvariantCulture),
            template.OffsetYMillimeters.ToString("F3", CultureInfo.InvariantCulture),
            template.SafetyInsetMillimeters.ToString("F3", CultureInfo.InvariantCulture),
            template.SkuQrPrefix,
            template.LabelQrPrefix,
            NormalizeLayoutStyle(template.LayoutStyle),
            GetTextFontSizePoints(template).ToString("F2", CultureInfo.InvariantCulture),
            GetMaxDisplayLength(template).ToString(CultureInfo.InvariantCulture));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }

    public static string NextTemplateNumber(IEnumerable<PrintTemplateProfile> templates)
    {
        int highest = templates
            .Select(template => template.TemplateNumber?.Trim())
            .Where(number => number is { Length: > 1 } && (number[0] is 'T' or 't'))
            .Select(number => int.TryParse(number![1..], NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0)
            .DefaultIfEmpty(7)
            .Max();
        int next = Math.Max(highest + 1, 8);
        if (next < 10_000)
        {
            return $"T{next:00}";
        }

        throw new InvalidOperationException("无法生成新的打印模板编号。");
    }

    public static IReadOnlyList<PrintTemplateProfile> OrderLabelTemplates(
        IEnumerable<PrintTemplateProfile> templates,
        string? defaultTemplateId = null)
    {
        return OrderTemplates(templates)
            .Where(IsLabel)
            .ToList();
    }

    public static IReadOnlyList<PrintTemplateProfile> OrderTemplates(IEnumerable<PrintTemplateProfile> templates)
    {
        return templates
            .Select((template, index) => new { Template = template, Index = index })
            .OrderBy(item => item.Template.SortOrder > 0 ? item.Template.SortOrder : int.MaxValue)
            .ThenBy(item => item.Index)
            .Select(item => item.Template)
            .ToList();
    }

    public static PrintTemplateProfile? SelectAutomaticLabelTemplate(
        IEnumerable<PrintTemplateProfile> availableTemplates,
        string? explicitTemplateId,
        string? defaultTemplateId)
    {
        var ordered = OrderLabelTemplates(availableTemplates, defaultTemplateId);
        return ordered.FirstOrDefault(template => MatchesId(template, explicitTemplateId))
            ?? ordered.FirstOrDefault(template => MatchesId(template, defaultTemplateId))
            ?? ordered.FirstOrDefault();
    }

    internal static bool MigrateBuiltInTemplates(ICollection<PrintTemplateProfile> templates, int sourceVersion)
    {
        bool changed = false;
        if (sourceVersion < 2)
        {
            PrintTemplateProfile? existing = templates.FirstOrDefault(template => MatchesId(template, "label_100x150mm"));
            if (existing is not null)
            {
                if (!IsLabel(existing))
                {
                    existing.Type = "label";
                    changed = true;
                }
            }
            else
            {
                templates.Add(Create100x150LabelTemplate());
                changed = true;
            }
        }

        if (sourceVersion < 3 && !templates.Any(template => MatchesId(template, "manufacturer_box_mark_100x150mm")))
        {
            PrintTemplateProfile? source = templates.FirstOrDefault(template => MatchesId(template, "label_100x150mm"));
            templates.Add(CreateManufacturerBoxMarkTemplate(source));
            changed = true;
        }

        if (sourceVersion < 4)
        {
            foreach (PrintTemplateProfile template in templates.Where(IsLabel))
            {
                string layoutStyle = NormalizeLayoutStyle(template.LayoutStyle);
                if (!string.Equals(template.LayoutStyle, layoutStyle, StringComparison.Ordinal))
                {
                    template.LayoutStyle = layoutStyle;
                    changed = true;
                }

                if (template.TextFontSizePoints <= 0)
                {
                    template.TextFontSizePoints = GetTextFontSizePoints(template);
                    changed = true;
                }
            }

            if (!templates.Any(template => MatchesId(template, "label_60x40mm_horizontal")))
            {
                PrintTemplateProfile? source = templates.FirstOrDefault(template => MatchesId(template, "label_60x40mm"));
                templates.Add(CreateHorizontal60x40LabelTemplate(source));
                changed = true;
            }
        }

        if (sourceVersion < 5 && !templates.Any(template => MatchesId(template, "location_100x150mm_landscape")))
        {
            PrintTemplateProfile? source = templates.FirstOrDefault(template => MatchesId(template, "label_100x150mm"));
            templates.Add(CreateLocationCodeTemplate(source));
            changed = true;
        }

        if (sourceVersion < 6)
        {
            PrintTemplateProfile? boxMark = templates.FirstOrDefault(template => MatchesId(template, "manufacturer_box_mark_100x150mm"));
            PrintTemplateProfile? source = templates.FirstOrDefault(template => MatchesId(template, "label_100x150mm"));
            if (boxMark is not null && source is not null &&
                string.IsNullOrWhiteSpace(boxMark.Printer) && !string.IsNullOrWhiteSpace(source.Printer))
            {
                boxMark.Printer = source.Printer;
                boxMark.OffsetXMillimeters = source.OffsetXMillimeters;
                changed = true;
            }
        }

        if (sourceVersion < 7 || templates.Any(template => string.IsNullOrWhiteSpace(template.TemplateNumber)))
        {
            changed |= AssignStableTemplateNumbers(templates);
        }

        if (sourceVersion < 8)
        {
            foreach (PrintTemplateProfile template in templates)
            {
                double offsetX = Math.Clamp(template.OffsetXMillimeters, -5, 5);
                double offsetY = Math.Clamp(template.OffsetYMillimeters, -5, 5);
                double safetyInset = template.SafetyInsetMillimeters > 0
                    ? Math.Clamp(template.SafetyInsetMillimeters, 0.5, 5)
                    : PrintPageContextFactory.DefaultSafetyInsetMillimeters;
                if (template.OffsetXMillimeters != offsetX || template.OffsetYMillimeters != offsetY ||
                    template.SafetyInsetMillimeters != safetyInset || !string.Equals(template.Mode, "fit", StringComparison.Ordinal))
                {
                    template.OffsetXMillimeters = offsetX;
                    template.OffsetYMillimeters = offsetY;
                    template.SafetyInsetMillimeters = safetyInset;
                    template.Mode = "fit";
                    changed = true;
                }
            }
        }

        if (sourceVersion < 9)
        {
            foreach (PrintTemplateProfile template in templates)
            {
                string layoutStyle = NormalizeLayoutStyle(template.LayoutStyle);
                string orientation = GetAutomaticOrientation(layoutStyle);
                if (!string.Equals(template.LayoutStyle, layoutStyle, StringComparison.Ordinal) ||
                    !string.Equals(template.Orientation, orientation, StringComparison.Ordinal) ||
                    !string.Equals(template.Mode, "fit", StringComparison.Ordinal))
                {
                    template.LayoutStyle = layoutStyle;
                    template.Orientation = orientation;
                    template.Mode = "fit";
                    changed = true;
                }
            }
        }

        if (sourceVersion < 10)
        {
            foreach (PrintTemplateProfile template in templates.Where(Is60x40Label))
            {
                double fontSize = NormalizeLayoutStyle(template.LayoutStyle) == HorizontalLayoutStyle
                    ? Horizontal60x40FontSizePoints
                    : Stacked60x40FontSizePoints;
                if (template.TextFontSizePoints != fontSize)
                {
                    template.TextFontSizePoints = fontSize;
                    changed = true;
                }
            }
        }

        if (sourceVersion < 11)
        {
            foreach (PrintTemplateProfile template in templates.Where(HasRestrictedDisplayText))
            {
                if (template.MaxDisplayLength != RestrictedDisplayTextLength)
                {
                    template.MaxDisplayLength = RestrictedDisplayTextLength;
                    changed = true;
                }
            }
        }

        if (sourceVersion < 13 && !templates.Any(template => MatchesId(template, "manufacturer_box_mark_quad_100x150mm")))
        {
            PrintTemplateProfile? source = templates.FirstOrDefault(template => MatchesId(template, "manufacturer_box_mark_100x150mm"));
            templates.Add(CreateQuadManufacturerBoxMarkTemplate(source));
            changed = true;
            changed |= AssignStableTemplateNumbers(templates);
        }

        if (sourceVersion < 12 || templates.Any(template => template.SortOrder <= 0) ||
            templates.Select(template => template.SortOrder).Distinct().Count() != templates.Count)
        {
            IReadOnlyList<PrintTemplateProfile> ordered = OrderTemplates(templates);
            for (int index = 0; index < ordered.Count; index++)
            {
                int sortOrder = index + 1;
                if (ordered[index].SortOrder != sortOrder)
                {
                    ordered[index].SortOrder = sortOrder;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private static bool AssignStableTemplateNumbers(ICollection<PrintTemplateProfile> templates)
    {
        bool changed = false;
        HashSet<string> used = templates
            .Select(template => template.TemplateNumber?.Trim())
            .Where(number => !string.IsNullOrWhiteSpace(number))
            .Select(number => number!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (PrintTemplateProfile template in templates.Where(template => string.IsNullOrWhiteSpace(template.TemplateNumber)))
        {
            if (BuiltInTemplateNumbers.TryGetValue(template.Id, out string? builtInNumber) && !used.Contains(builtInNumber))
            {
                template.TemplateNumber = builtInNumber;
                used.Add(builtInNumber);
                changed = true;
            }
        }

        foreach (PrintTemplateProfile template in templates.Where(template => string.IsNullOrWhiteSpace(template.TemplateNumber)))
        {
            template.TemplateNumber = NextTemplateNumber(templates);
            used.Add(template.TemplateNumber);
            changed = true;
        }

        return changed;
    }

    internal static IReadOnlyList<PrintTemplateProfile> CreateDefaultTemplates()
    {
        List<PrintTemplateProfile> templates =
        [
            new() { Id = "label_60x40mm", TemplateNumber = "T01", Name = "标签 60 x 40 mm", Type = "label", WidthMillimeters = 60, HeightMillimeters = 40, Orientation = "portrait", SkuQrPrefix = "T", LayoutStyle = StackedLayoutStyle, TextFontSizePoints = Stacked60x40FontSizePoints, MaxDisplayLength = 16 },
            CreateHorizontal60x40LabelTemplate(),
            Create100x150LabelTemplate(),
            CreateLocationCodeTemplate(),
            CreateManufacturerBoxMarkTemplate(),
            CreateQuadManufacturerBoxMarkTemplate(),
            new() { Id = "shipping_100x150mm", TemplateNumber = "T05", Name = "面单 100 x 150 mm", Type = "shipping", WidthMillimeters = 100, HeightMillimeters = 150, Orientation = "portrait", SkuQrPrefix = "T", MaxDisplayLength = 16 },
            new() { Id = "custom", TemplateNumber = "T07", Name = "自定义", Type = "custom", WidthMillimeters = 60, HeightMillimeters = 40, Orientation = "portrait", SkuQrPrefix = "T", MaxDisplayLength = 16 },
        ];
        for (int index = 0; index < templates.Count; index++)
        {
            templates[index].SortOrder = index + 1;
        }
        AssignStableTemplateNumbers(templates);
        return templates;
    }

    private static PrintTemplateProfile CreateLocationCodeTemplate(PrintTemplateProfile? source = null)
    {
        return new PrintTemplateProfile
        {
            Id = "location_100x150mm_landscape",
            TemplateNumber = "T04",
            Name = "库位码 100 x 150 mm（横向四码）",
            Type = "label",
            Printer = source?.Printer ?? string.Empty,
            WidthMillimeters = 100,
            HeightMillimeters = 150,
            Orientation = "landscape",
            Mode = "fit",
            Copies = 1,
            OffsetXMillimeters = source?.OffsetXMillimeters ?? 0,
            OffsetYMillimeters = source?.OffsetYMillimeters ?? 0,
            SafetyInsetMillimeters = source is { SafetyInsetMillimeters: > 0 } ? source.SafetyInsetMillimeters : PrintPageContextFactory.DefaultSafetyInsetMillimeters,
            SkuQrPrefix = string.Empty,
            LabelQrPrefix = string.Empty,
            LayoutStyle = LocationCodeLayoutStyle,
            TextFontSizePoints = LocationCodeFontSizePoints,
            MaxDisplayLength = 12,
        };
    }

    private static PrintTemplateProfile CreateHorizontal60x40LabelTemplate(PrintTemplateProfile? source = null)
    {
        return new PrintTemplateProfile
        {
            Id = "label_60x40mm_horizontal",
            TemplateNumber = "T02",
            Name = "标签 60 x 40 mm（左右排版）",
            Type = "label",
            Printer = source?.Printer ?? string.Empty,
            WidthMillimeters = 60,
            HeightMillimeters = 40,
            Orientation = "portrait",
            Mode = "fit",
            Copies = 1,
            OffsetXMillimeters = source?.OffsetXMillimeters ?? 0,
            OffsetYMillimeters = source?.OffsetYMillimeters ?? 0,
            SafetyInsetMillimeters = source is { SafetyInsetMillimeters: > 0 } ? source.SafetyInsetMillimeters : PrintPageContextFactory.DefaultSafetyInsetMillimeters,
            SkuQrPrefix = source?.SkuQrPrefix ?? "T",
            LabelQrPrefix = source?.LabelQrPrefix ?? string.Empty,
            LayoutStyle = HorizontalLayoutStyle,
            TextFontSizePoints = Horizontal60x40FontSizePoints,
            MaxDisplayLength = RestrictedDisplayTextLength,
        };
    }

    private static PrintTemplateProfile CreateManufacturerBoxMarkTemplate(PrintTemplateProfile? source = null)
    {
        return new PrintTemplateProfile
        {
            Id = "manufacturer_box_mark_100x150mm",
            TemplateNumber = "T06",
            Name = "厂家箱唛 100 x 150 mm",
            Type = "manufacturer_box_mark",
            Printer = source?.Printer ?? string.Empty,
            WidthMillimeters = 100,
            HeightMillimeters = 150,
            Orientation = "portrait",
            Mode = "fit",
            Copies = 1,
            OffsetXMillimeters = source?.OffsetXMillimeters ?? 0,
            OffsetYMillimeters = source?.OffsetYMillimeters ?? 0,
            SafetyInsetMillimeters = source is { SafetyInsetMillimeters: > 0 } ? source.SafetyInsetMillimeters : PrintPageContextFactory.DefaultSafetyInsetMillimeters,
            MaxDisplayLength = 16,
        };
    }

    private static PrintTemplateProfile CreateQuadManufacturerBoxMarkTemplate(PrintTemplateProfile? source = null)
    {
        return new PrintTemplateProfile
        {
            Id = "manufacturer_box_mark_quad_100x150mm",
            Name = "箱唛 100 x 150 mm（横向四码）",
            Type = "manufacturer_box_mark",
            Printer = source?.Printer ?? string.Empty,
            WidthMillimeters = 100,
            HeightMillimeters = 150,
            Orientation = "portrait",
            Mode = "fit",
            Copies = 1,
            OffsetXMillimeters = source?.OffsetXMillimeters ?? 0,
            OffsetYMillimeters = source?.OffsetYMillimeters ?? 0,
            SafetyInsetMillimeters = source is { SafetyInsetMillimeters: > 0 } ? source.SafetyInsetMillimeters : PrintPageContextFactory.DefaultSafetyInsetMillimeters,
            LayoutStyle = BoxMarkQuadLayoutStyle,
            MaxDisplayLength = 16,
        };
    }

    private static bool IsExplicitPrinterAvailable(PrintTemplateProfile template, IReadOnlySet<string> availablePrinters)
    {
        return !string.IsNullOrWhiteSpace(template.Printer) && availablePrinters.Contains(template.Printer.Trim());
    }

    private static PrintTemplateProfile Create100x150LabelTemplate()
    {
        return new PrintTemplateProfile
        {
            Id = "label_100x150mm",
            TemplateNumber = "T03",
            Name = "标签 100 x 150 mm（四联）",
            Type = "label",
            WidthMillimeters = 100,
            HeightMillimeters = 150,
            Orientation = "portrait",
            Mode = "fit",
            Copies = 1,
            SkuQrPrefix = "T",
            MaxDisplayLength = 16,
        };
    }

    private static bool MatchesSize(PrintTemplateProfile template, double width, double height)
    {
        return Math.Abs(template.WidthMillimeters - width) <= SizeToleranceMillimeters &&
            Math.Abs(template.HeightMillimeters - height) <= SizeToleranceMillimeters;
    }

    private static bool MatchesId(PrintTemplateProfile template, string? id)
    {
        return !string.IsNullOrWhiteSpace(id) &&
            string.Equals(template.Id, id, StringComparison.OrdinalIgnoreCase);
    }
}
