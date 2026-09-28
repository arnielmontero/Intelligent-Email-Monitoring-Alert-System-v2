using Microsoft.Win32;

namespace Iemas.Agent.Services;

/// <summary>§10 "Optional automatic startup" — the standard per-user Run key; no admin rights required, matches "optional" (a scheduled task would be the alternative but needs elevation to install).</summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "IemasAgent";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return;

        if (enabled)
        {
            // Assembly.Location is empty in a single-file .exe, so fall back to the app folder instead.
            var exePath = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "IemasAgent.exe");
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
    }
}
