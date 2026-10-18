using System.Diagnostics;
using System.IO;

namespace Blindly;

// Launches an isolated Chrome whose web content is exposed to UI Automation, and reports its PID.
internal static class Launch
{
    static readonly string[] Flags = [
        "--force-renderer-accessibility", "--autoplay-policy=no-user-gesture-required", "--no-first-run",
        "--no-default-browser-check", "--window-size=1400,1000", "--new-window"
    ];
    internal static string ProfileDir(string dir)
    {
        if (!Path.IsPathFullyQualified(dir) || dir.Contains('"'))
            throw new CliError("--chrome-profile must be an absolute directory path without quotes");
        return dir;
    }
    static string ChromePath()
    {
        const string appPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe";
        var candidates = new[] {
            Microsoft.Win32.Registry.LocalMachine.OpenSubKey(appPath)?.GetValue(null) as string,
            Microsoft.Win32.Registry.CurrentUser.OpenSubKey(appPath)?.GetValue(null) as string,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? throw CliError.Access("Google Chrome is not installed");
    }
    // Trailing backslashes are trimmed so the closing quote is not escaped.
    internal static string Arguments(string url, string profile) =>
        string.Join(' ', Flags.Prepend($"\"--user-data-dir={profile.TrimEnd('\\')}\"").Append($"\"{url}\""));
    internal static int Chrome(string url, string profile)
    {
        Directory.CreateDirectory(profile);
        // ShellExecute does not hand Chrome our stdout/stderr, so callers reading blindly4's output are not
        // left waiting on a pipe that the long-lived browser keeps open.
        var start = new ProcessStartInfo(ChromePath(), Arguments(url, profile)) { UseShellExecute = true };
        using var process = Process.Start(start) ?? throw CliError.Access("Chrome did not start");
        for (int attempt = 0; attempt < 150; attempt++)
        {
            if (Native.Windows().Any(w => w.Pid == process.Id)) return process.Id;
            if (process.HasExited) break;
            Thread.Sleep(100);
        }
        throw CliError.Access("Chrome did not open a window for the isolated profile");
    }
}
