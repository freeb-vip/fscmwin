// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Diagnostics;
using Microsoft.Win32;

namespace Fscm.Edge.Win.Services;

public sealed class StartupService
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FSCM Edge";

    public bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
        return key?.GetValue(ValueName) is string value &&
            string.Equals(value, QuoteExecutablePath(GetExecutablePath()), StringComparison.Ordinal);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using RegistryKey writeKey = Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);
            writeKey.SetValue(ValueName, QuoteExecutablePath(GetExecutablePath()), RegistryValueKind.String);
            return;
        }

        using RegistryKey? readKey = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true);
        readKey?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string GetExecutablePath()
    {
        return Environment.ProcessPath ??
            Process.GetCurrentProcess().MainModule?.FileName ??
            throw new InvalidOperationException("Unable to locate the FSCM Edge executable.");
    }

    private static string QuoteExecutablePath(string executablePath)
    {
        return $"\"{executablePath}\"";
    }
}
