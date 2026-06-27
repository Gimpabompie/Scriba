using System.IO;
using System.Text.Json;

namespace Notulen.Services;

/// <summary>
/// Persistente instellingen (onthouden tussen sessies) in
/// %APPDATA%\Notulen\config.json. Volledig lokaal.
/// </summary>
public class AppSettings
{
    public string Language { get; set; } = "Automatisch detecteren";
    public string Model { get; set; } = "small";
    public string Vocabulary { get; set; } = "";
    public bool Timestamps { get; set; } = true;
    public bool SaveAudio { get; set; } = true;
    public string? Device { get; set; }

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notulen");

    private static string ConfigPath => Path.Combine(Dir, "config.json");

    public static string RecordingsDir => Path.Combine(Dir, "opnames");
    public static string ModelsDir => Path.Combine(Dir, "modellen");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Een kapotte config mag de app nooit blokkeren.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Niet fataal.
        }
    }
}
