using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipJoin.Services;

public class AppSettings
{
    public string ApiEndpoint { get; set; } = "https://openkey.cloud/v1/chat/completions";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "deepseek-v3";

    /// <summary>
    /// Remembered conflict-resolution choice. <see cref="ConflictResolution.Ask"/> means always prompt.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public ConflictResolution DefaultConflictResolution { get; set; } = ConflictResolution.Ask;

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipJoin");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupted settings file — return defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(SettingsFile, json);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiEndpoint);
}
