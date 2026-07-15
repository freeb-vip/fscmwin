// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Diagnostics;

namespace Fscm.Edge.UpdateLauncher;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (!TryReadArguments(args, out int parentProcessId, out string? installerPath) || !File.Exists(installerPath))
        {
            return 2;
        }

        try
        {
            using Process parent = Process.GetProcessById(parentProcessId);
            if (!parent.WaitForExit(30000))
            {
                return 3;
            }
        }
        catch (ArgumentException)
        {
            // The parent application has already exited.
        }

        using Process installer = Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("Unable to start the update installer.");
        return 0;
    }

    private static bool TryReadArguments(string[] args, out int parentProcessId, out string? installerPath)
    {
        parentProcessId = 0;
        installerPath = null;
        for (int index = 0; index < args.Length - 1; index += 2)
        {
            if (string.Equals(args[index], "--parent-pid", StringComparison.Ordinal))
            {
                _ = int.TryParse(args[index + 1], out parentProcessId);
            }
            else if (string.Equals(args[index], "--installer", StringComparison.Ordinal))
            {
                installerPath = args[index + 1];
            }
        }

        return parentProcessId > 0 && !string.IsNullOrWhiteSpace(installerPath);
    }
}
