using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using Whisper.net;

namespace Notulen.Services;

public record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

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
        ["large-v3"] = "ggml-large-v3.bin",
    };

    public async Task<List<TranscriptSegment>> TranscribeAsync(
        float[] samples,
        string modelSize,
        string? language,
        string vocabulary,
        Action<TranscriptSegment>? onSegment,
        CancellationToken ct = default)
    {
        var modelPath = await EnsureModelAsync(modelSize, ct);

        Status?.Invoke("Model laden…");
        using var factory = WhisperFactory.FromPath(modelPath);

        // "auto" laat whisper de taal zelf detecteren (NL/EN/…).
        var builder = factory.CreateBuilder()
            .WithLanguage(string.IsNullOrEmpty(language) ? "auto" : language);

        if (!string.IsNullOrWhiteSpace(vocabulary))
            builder = builder.WithPrompt(BuildPrompt(vocabulary));

        using var processor = builder.Build();

        Status?.Invoke("Bezig met transcriberen…");
        var segments = new List<TranscriptSegment>();
        await foreach (var seg in processor.ProcessAsync(samples, ct))
        {
            var s = new TranscriptSegment(seg.Start, seg.End, seg.Text.Trim());
            segments.Add(s);
            onSegment?.Invoke(s);
        }

        Status?.Invoke("Klaar.");
        return segments;
    }

    private async Task<string> EnsureModelAsync(string size, CancellationToken ct)
    {
        var file = ModelFiles.GetValueOrDefault(size, "ggml-small.bin");
        Directory.CreateDirectory(AppSettings.ModelsDir);
        var path = Path.Combine(AppSettings.ModelsDir, file);
        if (File.Exists(path) && new FileInfo(path).Length > 0)
            return path;

        Status?.Invoke($"Model '{size}' wordt eenmalig gedownload…");
        var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{file}";

        // Geen totale timeout: grote modellen (medium ~1,5 GB) mogen lang duren;
        // we leunen op de CancellationToken om te kunnen afbreken.
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? -1L;
        var tmp = path + ".part";

        await using (var fs = File.Create(tmp))
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            var sw = Stopwatch.StartNew();
            var lastReport = TimeSpan.FromSeconds(-1);

            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;

                // Niet bij elke blok melden; ~3x per seconde is ruim genoeg.
                if (sw.Elapsed - lastReport >= TimeSpan.FromMilliseconds(350))
                {
                    lastReport = sw.Elapsed;
                    ReportDownload(size, readTotal, total, sw.Elapsed);
                }
            }
            ReportDownload(size, readTotal, total, sw.Elapsed);
        }

        File.Move(tmp, path, true);
        DownloadProgress?.Invoke(1.0);
        return path;
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
        var sb = new StringBuilder();
        foreach (var s in segments)
        {
            if (withTimestamps)
                sb.Append($"[{Ts(s.Start)} - {Ts(s.End)}] ");
            sb.AppendLine(s.Text.Trim());
        }
        if (!withTimestamps)
            return string.Join(" ", segments.Select(s => s.Text.Trim())).Trim();
        return sb.ToString().TrimEnd();
    }

    public static string Ts(TimeSpan t)
    {
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}
