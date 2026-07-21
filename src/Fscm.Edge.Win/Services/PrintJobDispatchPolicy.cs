// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Fscm.Edge.Win.Services;

public static class PrintJobDispatchPolicy
{
    public const int MaxCopiesPerContent = 5;

    public static bool IsTextLabel(string? kind)
    {
        return string.Equals(kind, "manual_text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "batch_content", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCopiesAllowed(int copies)
    {
        return copies is >= 1 and <= MaxCopiesPerContent;
    }

    public static void EnsureCopiesAllowed(int copies)
    {
        if (!IsCopiesAllowed(copies))
        {
            throw new InvalidOperationException($"打印保护已拦截：同一内容单次最多打印 {MaxCopiesPerContent} 份。");
        }
    }

    public static void EnsureContentCopiesAllowed(IEnumerable<(string Content, int Copies)> items)
    {
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((string content, int copies) in items)
        {
            EnsureCopiesAllowed(copies);
            string key = content.Trim();
            if (key.Length == 0)
            {
                continue;
            }
            totals[key] = totals.GetValueOrDefault(key) + copies;
            if (totals[key] > MaxCopiesPerContent)
            {
                throw new InvalidOperationException($"打印保护已拦截：内容“{key}”累计超过 {MaxCopiesPerContent} 份。");
            }
        }
    }
}
