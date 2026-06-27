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
    public bool Live { get; set; } = true;
    public string? Device { get; set; }

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notulen");

    private static string ConfigPath => Path.Combine(Dir, "config.json");

    public static string RecordingsDir => Path.Combine(Dir, "opnames");

    /// <summary>Map waar nieuw gedownloade modellen worden bewaard.</summary>
    public static string ModelsDir => Path.Combine(Dir, "modellen");

    /// <summary>
    /// Mappen waarin naar een bestaand modelbestand wordt gezocht, op volgorde.
    /// Voor afgeschermde netwerken: zet NOTULEN_MODELS_DIR naar een (netwerk)map
    /// met de vooraf geplaatste modellen, dan hoeft niets gedownload te worden.
    /// </summary>
    public static IEnumerable<string> ModelSearchDirs()
    {
        var env = Environment.GetEnvironmentVariable("NOTULEN_MODELS_DIR");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;
        yield return Path.Combine(AppContext.BaseDirectory, "modellen"); // naast de app
        yield return ModelsDir;                                          // %APPDATA%
    }

    /// <summary>Zoek een bestaand modelbestand; geef het pad terug of null.</summary>
    public static string? FindExistingModel(string fileName)
    {
        foreach (var dir in ModelSearchDirs())
        {
            try
            {
                var p = Path.Combine(dir, fileName);
                if (File.Exists(p) && new FileInfo(p).Length > 0) return p;
            }
            catch { /* ongeldige (netwerk)map negeren */ }
        }
        return null;
    }

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
