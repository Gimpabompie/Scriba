using System.IO;
using System.Text.Json;

namespace Notulen.Services;

/// <summary>
/// Persistente instellingen (onthouden tussen sessies) in
/// %APPDATA%\Notulen\config.json. Volledig lokaal.
/// </summary>
public class AppSettings
{
    public string Language { get; set; } = "Nederlands";
    public string Model { get; set; } = "medium";
    public bool Welcomed { get; set; } = false;
    public string Vocabulary { get; set; } = "";
    public bool Timestamps { get; set; } = true;
    public bool SaveAudio { get; set; } = true;
    public bool Live { get; set; } = false;
    public bool Diarize { get; set; } = false;
    public bool Vad { get; set; } = false;
    public string? Device { get; set; }

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Scriba");

    // Oude datamap (vóór de naam 'Scriba'); we blijven hier naar modellen zoeken
    // zodat een eerder gedownload model niet opnieuw hoeft.
    private static string LegacyDir =>
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
        var env = Environment.GetEnvironmentVariable("SCRIBA_MODELS_DIR");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;
        yield return Path.Combine(AppContext.BaseDirectory, "modellen"); // naast de app
        yield return ModelsDir;                                          // %APPDATA%\Scriba
        yield return Path.Combine(LegacyDir, "modellen");                // oude %APPDATA%\Notulen
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

    /// <summary>
    /// Geef mogelijke bestanden voor een model terug: eerst de exacte naam, dan
    /// varianten in dezelfde map (bv. een handmatig geplaatst bestand met een
    /// net iets andere naam zoals 'ggml-medium.bin.bin' of 'ggml-medium (1).bin').
    /// Zo wordt een door de gebruiker neergezet bestand alsnog herkend i.p.v.
    /// onnodig opnieuw gedownload.
    /// </summary>
    public static IEnumerable<string> FindModelCandidates(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName); // ggml-medium
        var ext = Path.GetExtension(fileName);                     // .bin
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in ModelSearchDirs())
        {
            string exact;
            try { exact = Path.Combine(dir, fileName); }
            catch { continue; }

            if (TryFile(exact) && seen.Add(exact)) yield return exact;

            IEnumerable<string> variants = Array.Empty<string>();
            try
            {
                if (Directory.Exists(dir))
                    variants = Directory.EnumerateFiles(dir, baseName + "*" + ext).ToList();
            }
            catch { /* onbruikbare map overslaan */ }

            foreach (var v in variants)
                if (TryFile(v) && seen.Add(v)) yield return v;
        }

        static bool TryFile(string p)
        {
            try { return File.Exists(p) && new FileInfo(p).Length > 0; }
            catch { return false; }
        }
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
