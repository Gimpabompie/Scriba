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
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var tmp = path + ".part";
        await using (var fs = File.Create(tmp))
            await resp.Content.CopyToAsync(fs, ct);
        File.Move(tmp, path, true);
        return path;
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
