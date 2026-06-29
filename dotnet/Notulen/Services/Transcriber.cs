using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Whisper.net;

namespace Notulen.Services;

public record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text, string? Speaker = null);

/// <summary>
/// Offline transcriptie via Whisper.net (whisper.cpp). Het ggml-model wordt
/// eenmalig gedownload en lokaal gecachet; daarna werkt alles offline.
/// </summary>
public class Transcriber
{
    public event Action<string>? Status;

    /// <summary>Voortgang van de modeldownload (0..1, of -1 als onbekend).</summary>
    public event Action<double>? DownloadProgress;

    private static readonly Dictionary<string, string> ModelFiles = new()
    {
        ["tiny"] = "ggml-tiny.bin",
        ["base"] = "ggml-base.bin",
        ["small"] = "ggml-small.bin",
        ["medium"] = "ggml-medium.bin",
        ["large-v3-turbo-licht"] = "ggml-large-v3-turbo-q5_0.bin",
        ["large-v3-turbo"] = "ggml-large-v3-turbo.bin",
        ["large-v3"] = "ggml-large-v3.bin",
    };

    // Verwachte minimale bestandsgrootte per model (ruim onder de echte grootte).
    // Een afgekapte/onvolledige download is kleiner dan dit en wordt geweigerd,
    // zodat we nooit een half model aan de native loader geven (dat crasht hard).
    private static readonly Dictionary<string, long> MinSizes = new()
    {
        ["tiny"] = 50L * 1024 * 1024,    // ~75 MB
        ["base"] = 100L * 1024 * 1024,   // ~142 MB
        ["small"] = 350L * 1024 * 1024,  // ~466 MB
        ["medium"] = 1300L * 1024 * 1024, // ~1,5 GB
        ["large-v3-turbo-licht"] = 450L * 1024 * 1024, // ~547 MB
        ["large-v3-turbo"] = 1400L * 1024 * 1024,      // ~1,6 GB
        ["large-v3"] = 2800L * 1024 * 1024,            // ~3,1 GB
    };

    private static long MinSizeFor(string size) =>
        MinSizes.TryGetValue(size, out var m) ? m : 1_000_000L;

    // Het geladen model wordt hergebruikt (belangrijk voor live: niet per
    // stukje het hele model opnieuw inladen).
    private WhisperFactory? _factory;
    private string? _loadedModel;

    /// <summary>Is het gevraagde model al geladen en klaar voor gebruik?</summary>
    public bool IsReady(string modelSize) => _factory != null && _loadedModel == modelSize;

    /// <summary>Zorg dat het model gedownload én geladen is.</summary>
    public async Task EnsureReadyAsync(string modelSize, CancellationToken ct = default)
    {
        if (IsReady(modelSize)) return;
        var modelPath = await EnsureModelAsync(modelSize, ct);
        Status?.Invoke("Model laden…");
        _factory?.Dispose();
        try
        {
            _factory = WhisperFactory.FromPath(modelPath);
        }
        catch (Exception ex)
        {
            // Beschadigd/onleesbaar model: verwijderen zodat het opnieuw wordt
            // gedownload, en een nette melding geven i.p.v. een crash.
            try { File.Delete(modelPath); } catch { }
            _factory = null;
            throw new InvalidOperationException(
                "Het model kon niet geladen worden (mogelijk beschadigd of " +
                "onvolledig gedownload). Het bestand is verwijderd — probeer het " +
                "opnieuw, dan wordt het opnieuw opgehaald.\n\n(detail: " + ex.Message + ")", ex);
        }
        _loadedModel = modelSize;
    }

    /// <summary>Controleer of een bestand een geldig ggml/GGUF-model lijkt.</summary>
    private static bool LooksLikeModel(string path, long minSize = 1_000_000)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < minSize) return false; // te klein = onvolledig
            using var fs = File.OpenRead(path);
            var b = new byte[4];
            if (fs.Read(b, 0, 4) < 4) return false;
            // "ggml" of "GGUF"
            return (b[0] == 0x67 && b[1] == 0x67 && b[2] == 0x6d && b[3] == 0x6c) ||
                   (b[0] == 0x47 && b[1] == 0x47 && b[2] == 0x55 && b[3] == 0x46);
        }
        catch { return false; }
    }

    public async Task<List<TranscriptSegment>> TranscribeAsync(
        float[] samples,
        string modelSize,
        string? language,
        string vocabulary,
        Action<TranscriptSegment>? onSegment,
        CancellationToken ct = default,
        double timeOffsetSeconds = 0,
        bool announce = true,
        string contextPrompt = "")
    {
        await EnsureReadyAsync(modelSize, ct);

        // "auto" laat whisper de taal zelf detecteren (NL/EN/…).
        // WithNoContext(true): niet voortborduren op eerder gegenereerde tekst,
        // wat afdwalen/herhalen (hallucinaties) flink vermindert.
        var builder = _factory!.CreateBuilder()
            .WithLanguage(string.IsNullOrEmpty(language) ? "auto" : language)
            .WithNoContext();

        // Prompt = woordenlijst + (bij live) de voorgaande tekst als context.
        var promptParts = new List<string>();
        var vocabPrompt = BuildPrompt(vocabulary);
        if (!string.IsNullOrEmpty(vocabPrompt)) promptParts.Add(vocabPrompt);
        if (!string.IsNullOrWhiteSpace(contextPrompt)) promptParts.Add(contextPrompt.Trim());
        if (promptParts.Count > 0)
            builder = builder.WithPrompt(string.Join("\n", promptParts));

        using var processor = builder.Build();

        if (announce) Status?.Invoke("Bezig met transcriberen…");
        var offset = TimeSpan.FromSeconds(timeOffsetSeconds);
        var segments = new List<TranscriptSegment>();
        await foreach (var seg in processor.ProcessAsync(samples, ct))
        {
            var s = new TranscriptSegment(seg.Start + offset, seg.End + offset, seg.Text.Trim());
            segments.Add(s);
            onSegment?.Invoke(s);
        }

        if (announce) Status?.Invoke("Klaar.");
        return segments;
    }

    private async Task<string> EnsureModelAsync(string size, CancellationToken ct)
    {
        var file = ModelFiles.GetValueOrDefault(size, "ggml-small.bin");
        var minSize = MinSizeFor(size);

        // Eerst zoeken naar een vooraf geplaatst model (bv. op een netwerkshare
        // via SCRIBA_MODELS_DIR). Op afgeschermde netwerken wordt zo niets
        // gedownload. Alleen hergebruiken als het bestand er geldig én volledig
        // uitziet — een eerder half/corrupt gedownload bestand wordt zo niet
        // hergebruikt, maar opnieuw opgehaald.
        var existing = AppSettings.FindExistingModel(file);
        if (existing != null && LooksLikeModel(existing, minSize)) return existing;

        Directory.CreateDirectory(AppSettings.ModelsDir);
        var path = Path.Combine(AppSettings.ModelsDir, file);

        // Eerder (beschadigd) bestand opruimen zodat het opnieuw wordt opgehaald.
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        Status?.Invoke($"Model '{size}' wordt eenmalig gedownload…");
        // Interne spiegel mogelijk via SCRIBA_MODEL_BASEURL.
        var baseUrl = Environment.GetEnvironmentVariable("SCRIBA_MODEL_BASEURL")?.TrimEnd('/')
                      ?? "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";
        var url = $"{baseUrl}/{file}";
        var tmp = path + ".part";

        // Grote modellen (medium ~1,5 GB) komen op een wisselvallige verbinding
        // niet altijd in één keer binnen. We downloaden daarom hervatbaar: bij
        // een afgebroken verbinding gaan we verder waar we gebleven waren
        // (HTTP Range) i.p.v. opnieuw vanaf 0, met een paar automatische pogingen.
        long total = await DownloadResumableAsync(url, tmp, size, minSize, ct);

        // Volledigheidscontrole: is alles binnengekomen?
        long got = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
        if (total > 0 && got != total)
        {
            throw new InvalidOperationException(
                $"De download van het model ('{file}') is onderbroken " +
                $"({got / 1048576} van {total / 1048576} MB ontvangen). " +
                "De helft is bewaard — probeer opnieuw, dan gaat hij verder waar " +
                "hij gebleven was.");
        }

        File.Move(tmp, path, true);
        DownloadProgress?.Invoke(1.0);

        // Controleer of het gedownloade bestand een geldig én volledig model is
        // (niet bv. een foutpagina of een te klein/afgekapt bestand). Zo niet:
        // verwijderen en een nette melding geven i.p.v. een crash.
        if (!LooksLikeModel(path, minSize))
        {
            var diag = DescribeBadModel(path, minSize);
            try { File.Delete(path); } catch { }
            throw new InvalidOperationException(
                $"De download van '{file}' is niet het juiste modelbestand.\n{diag}\n\n" +
                "Lost opnieuw proberen het niet op? Download het model dan handmatig " +
                "(bijv. in je browser of op een ander netwerk) en plaats het hier:\n" +
                $"{AppSettings.ModelsDir}\n\n" +
                $"Directe link:\n{baseUrl}/{file}");
        }
        return path;
    }

    /// <summary>Beschrijf kort wat er mis is met een afgekeurd modelbestand,
    /// zodat een netwerk-/proxyprobleem herkenbaar is.</summary>
    private static string DescribeBadModel(string path, long minSize)
    {
        try
        {
            var fi = new FileInfo(path);
            long len = fi.Exists ? fi.Length : 0;
            string mb = $"{len / 1048576.0:0.0} MB (verwacht ≥ {minSize / 1048576} MB)";

            // Eerste bytes lezen om HTML-/foutpagina of LFS-pointer te herkennen.
            string head = "";
            using (var fs = File.OpenRead(path))
            {
                var b = new byte[256];
                int n = fs.Read(b, 0, b.Length);
                head = Encoding.ASCII.GetString(b, 0, Math.Max(0, n));
            }
            var h = head.TrimStart();
            if (h.StartsWith("<") || h.Contains("<html", StringComparison.OrdinalIgnoreCase))
                return $"Er kwam een webpagina terug i.p.v. het model ({mb}). " +
                       "Waarschijnlijk blokkeert een firewall/proxy/antivirus de download.";
            if (h.StartsWith("version https://git-lfs"))
                return $"Er kwam een Git-LFS-verwijzing terug i.p.v. het model ({mb}). " +
                       "De server gaf niet het echte bestand vrij.";
            return $"Het bestand is te klein/onvolledig: {mb}.";
        }
        catch
        {
            return "Het bestand kon niet gecontroleerd worden.";
        }
    }

    /// <summary>
    /// Download <paramref name="url"/> naar <paramref name="tmp"/>, hervatbaar
    /// (HTTP Range) en met automatische herpogingen bij netwerkfouten. Geeft de
    /// verwachte totale grootte terug (of -1 als onbekend).
    /// </summary>
    private async Task<long> DownloadResumableAsync(string url, string tmp, string size, long minSize, CancellationToken ct)
    {
        const int maxAttempts = 6;
        // Eén HttpClient hergebruiken over de pogingen heen.
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // Sommige CDN's (Cloudflare voor de Hugging Face LFS-bestanden) geven
        // zonder herkenbare User-Agent een kleine challenge-/foutpagina terug
        // i.p.v. het bestand. Een nette User-Agent voorkomt dat.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scriba/1.0 (+https://github.com/Gimpabompie/Scriba)");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
        var sw = Stopwatch.StartNew();
        var lastReport = TimeSpan.FromSeconds(-1);
        long total = -1L;
        Exception? last = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            long have = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                // Server negeert hervatten (geen 206) -> opnieuw vanaf 0 beginnen.
                bool resuming = have > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                if (have > 0 && !resuming)
                {
                    try { File.Delete(tmp); } catch { }
                    have = 0;
                }
                // .part is al volledig volgens de server.
                if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    total = resp.Content.Headers.ContentRange?.Length ?? have;
                    return total;
                }
                resp.EnsureSuccessStatusCode();

                // Bij 206 is ContentLength de rest; bij 200 het geheel.
                total = resp.Content.Headers.ContentRange?.Length
                        ?? (resp.Content.Headers.ContentLength is long cl ? have + cl : -1L);

                var mode = resuming ? FileMode.Append : FileMode.Create;
                long readTotal = have;
                await using (var fs = new FileStream(tmp, mode, FileAccess.Write, FileShare.None))
                await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                        readTotal += read;
                        if (sw.Elapsed - lastReport >= TimeSpan.FromMilliseconds(350))
                        {
                            lastReport = sw.Elapsed;
                            ReportDownload(size, readTotal, total, sw.Elapsed);
                        }
                    }
                    ReportDownload(size, readTotal, total, sw.Elapsed);
                }

                // Compleet? Dan klaar. Anders: verbinding viel weg -> herpoging.
                if (total > 0)
                {
                    if (readTotal >= total) return total;
                }
                else
                {
                    // Geen Content-Length: we kunnen volledigheid niet hard
                    // vaststellen. Accepteren zodra we minstens de verwachte
                    // ondergrens binnen hebben; anders hervatten.
                    if (readTotal >= minSize) return total;
                }
                last = new IOException($"Verbinding viel weg op {readTotal} bytes (verwacht {(total > 0 ? total.ToString() : "onbekend")}).");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex; // netwerkfout: hervatten in de volgende poging
            }

            if (attempt < maxAttempts)
            {
                int delaySec = Math.Min(16, 1 << (attempt - 1)); // 1,2,4,8,16,16…
                Status?.Invoke($"Verbinding onderbroken — opnieuw proberen ({attempt}/{maxAttempts - 1})…");
                await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);
            }
        }

        throw new InvalidOperationException(
            "Het model kon na meerdere pogingen niet volledig worden gedownload. " +
            "De voortgang is bewaard — probeer het later opnieuw, dan gaat hij " +
            "verder waar hij gebleven was.", last);
    }

    private void ReportDownload(string size, long read, long total, TimeSpan elapsed)
    {
        double mb = read / 1048576.0;
        double secs = Math.Max(0.001, elapsed.TotalSeconds);
        double speed = mb / secs; // MB/s

        if (total > 0)
        {
            double totMb = total / 1048576.0;
            double pct = read * 100.0 / total;
            double etaSec = speed > 0 ? (totMb - mb) / speed : 0;
            DownloadProgress?.Invoke(read / (double)total);
            Status?.Invoke(
                $"Model '{size}' downloaden… {pct:0}%  " +
                $"({mb:0}/{totMb:0} MB · {speed:0.0} MB/s · nog ~{FormatEta(etaSec)})");
        }
        else
        {
            DownloadProgress?.Invoke(-1);
            Status?.Invoke($"Model '{size}' downloaden… {mb:0} MB ({speed:0.0} MB/s)");
        }
    }

    private static string FormatEta(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return "—";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}u {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }

    private static string BuildPrompt(string vocabulary)
    {
        var terms = vocabulary
            .Replace(";", ",").Replace("\n", ",").Replace("\r", "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return "";
        return "Namen en termen: " + string.Join(", ", terms) + ".";
    }
}

/// <summary>Opmaak van segmenten naar leesbare notulen.</summary>
public static class Minutes
{
    public static string Format(IEnumerable<TranscriptSegment> segments, bool withTimestamps)
    {
        var list = segments.ToList();
        bool hasSpeakers = list.Any(s => !string.IsNullOrEmpty(s.Speaker));

        // Zonder tijdstempels én zonder sprekers: één doorlopende tekst.
        if (!withTimestamps && !hasSpeakers)
            return string.Join(" ", list.Select(s => s.Text.Trim())).Trim();

        var sb = new StringBuilder();
        foreach (var s in list)
        {
            if (withTimestamps)
                sb.Append($"[{Ts(s.Start)} - {Ts(s.End)}] ");
            if (!string.IsNullOrEmpty(s.Speaker))
                sb.Append($"{s.Speaker}: ");
            sb.AppendLine(s.Text.Trim());
        }
        return sb.ToString().TrimEnd();
    }

    public static string Ts(TimeSpan t)
    {
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}
