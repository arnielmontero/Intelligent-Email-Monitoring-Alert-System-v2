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
            var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
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
