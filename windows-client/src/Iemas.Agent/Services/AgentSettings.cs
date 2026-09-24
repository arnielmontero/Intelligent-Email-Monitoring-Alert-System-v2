using System.IO;
using System.Text.Json;

namespace Iemas.Agent.Services;

/// <summary>§10 Settings (server URL configuration), §8.1 "Report technical state." Non-sensitive — plain JSON, unlike CredentialStore which is DPAPI-protected.</summary>
public class AgentSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IemasAgent");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public string ServerUrl { get; set; } = "https://localhost:8443";
    public bool StartWithWindows { get; set; }
    public bool AllowInsecureTls { get; set; } = false;

    public static AgentSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AgentSettings>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch (IOException) { }
        catch (JsonException) { }

        return new AgentSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
    }
}
