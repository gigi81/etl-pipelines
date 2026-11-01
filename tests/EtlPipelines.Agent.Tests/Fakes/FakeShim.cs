namespace EtlPipelines.Agent.Tests.Fakes;

/// <summary>A throwaway script standing in for an installed pipeline package's shim: lives about a second, then exits.</summary>
internal static class FakeShim
{
    /// <summary>Writes a shim into <paramref name="directory"/> (which must not hold another file) and returns its path.</summary>
    public static string Write(string directory, int exitCode = 0)
    {
        Directory.CreateDirectory(directory);

        if (OperatingSystem.IsWindows())
        {
            var cmdPath = Path.Combine(directory, "shim.cmd");
            File.WriteAllText(cmdPath, $"@ping -n 3 127.0.0.1 >nul\r\n@exit /b {exitCode}\r\n");
            return cmdPath;
        }

        var shPath = Path.Combine(directory, "shim.sh");
        File.WriteAllText(shPath, $"#!/bin/sh\nsleep 1\nexit {exitCode}\n");
        File.SetUnixFileMode(shPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return shPath;
    }
}
