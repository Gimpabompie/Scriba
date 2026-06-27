using System.Diagnostics;
using System.IO;
using System.Net.Http;
using SharpCompress.Readers;
using SherpaOnnx;

namespace Notulen.Services;

public record SpeakerTurn(double Start, double End, int Speaker);

/// <summary>
/// Offline sprekerherkenning (diarization) met sherpa-onnx: bepaalt wie wanneer
/// spreekt en koppelt dat aan de transcript-segmenten. Werkt op de hele opname.
///
/// Twee ONNX-modellen zijn nodig (eenmalig gedownload of vooraf geplaatst in de
/// modelmap): een segmentatiemodel en een spreker-embeddingmodel.
/// </summary>
public class SpeakerDiarizer
{
    public event Action<string>? Status;
    public event Action<double>? DownloadProgress;

    private const string SegFile = "diar-segmentation.onnx";
    private const string EmbFile = "diar-embedding.onnx";

    // Standaardbronnen (override met SCRIBA_DIAR_SEG_URL / SCRIBA_DIAR_EMB_URL).
    private const string SegUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2";
    private const string EmbUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx";

    private OfflineSpeakerDiarization? _sd;

    /// <summary>Voer diarization uit op 16 kHz mono samples.</summary>
    public async Task<List<SpeakerTurn>> DiarizeAsync(float[] samples, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        Status?.Invoke("Sprekers herkennen…");

        return await Task.Run(() =>
        {
            var result = _sd!.Process(samples);
            var turns = new List<SpeakerTurn>();
            foreach (var s in result.SortByStartTime())
                turns.Add(new SpeakerTurn(s.Start, s.End, s.Speaker));
            return turns;
        }, ct);
    }

    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_sd != null) return;

        var seg = await EnsureSegmentationAsync(ct);
        var emb = await EnsureEmbeddingAsync(ct);

        Status?.Invoke("Sprekermodel laden…");
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = seg;
        config.Embedding.Model = emb;
        config.Clustering.NumClusters = -1;   // automatisch bepalen
        config.Clustering.Threshold = 0.5f;   // hoger = minder, lager = meer sprekers
        _sd = new OfflineSpeakerDiarization(config);
    }

    // ---------- Modellen ----------
    private async Task<string> EnsureEmbeddingAsync(CancellationToken ct)
    {
        var existing = AppSettings.FindExistingModel(EmbFile);
        if (existing != null) return existing;

        Directory.CreateDirectory(AppSettings.ModelsDir);
        var dest = Path.Combine(AppSettings.ModelsDir, EmbFile);
        var url = Environment.GetEnvironmentVariable("SCRIBA_DIAR_EMB_URL") ?? EmbUrl;
        Status?.Invoke("Spreker-embeddingmodel downloaden…");
        await DownloadAsync(url, dest, ct);
        return dest;
    }

    private async Task<string> EnsureSegmentationAsync(CancellationToken ct)
    {
        var existing = AppSettings.FindExistingModel(SegFile);
        if (existing != null) return existing;

        Directory.CreateDirectory(AppSettings.ModelsDir);
        var dest = Path.Combine(AppSettings.ModelsDir, SegFile);
        var url = Environment.GetEnvironmentVariable("SCRIBA_DIAR_SEG_URL") ?? SegUrl;

        Status?.Invoke("Segmentatiemodel downloaden…");
        if (url.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            await DownloadAsync(url, dest, ct);
        }
        else
        {
            // .tar.bz2: downloaden en het model.onnx eruit halen.
            var archive = Path.Combine(AppSettings.ModelsDir, "segmentation.tar.bz2");
            await DownloadAsync(url, archive, ct);
            Status?.Invoke("Segmentatiemodel uitpakken…");
            ExtractOnnx(archive, dest);
            try { File.Delete(archive); } catch { }
        }
        return dest;
    }

    private static void ExtractOnnx(string archivePath, string destPath)
    {
        using var fs = File.OpenRead(archivePath);
        using var reader = ReaderFactory.Open(fs);
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory) continue;
            var key = reader.Entry.Key ?? "";
            if (key.EndsWith("model.onnx", StringComparison.OrdinalIgnoreCase) ||
                key.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            {
                using var es = reader.OpenEntryStream();
                using var outFs = File.Create(destPath);
                es.CopyTo(outFs);
                return;
            }
        }
        throw new InvalidOperationException("Geen .onnx gevonden in het segmentatie-archief.");
    }

    private async Task DownloadAsync(string url, string dest, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1L;
        var tmp = dest + ".part";
        await using (var fsOut = File.Create(tmp))
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[81920];
            long read = 0;
            int n;
            var sw = Stopwatch.StartNew();
            var last = TimeSpan.FromSeconds(-1);
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await fsOut.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (sw.Elapsed - last >= TimeSpan.FromMilliseconds(350))
                {
                    last = sw.Elapsed;
                    DownloadProgress?.Invoke(total > 0 ? read / (double)total : -1);
                }
            }
        }
        File.Move(tmp, dest, true);
        DownloadProgress?.Invoke(1.0);
    }

    // ---------- Koppelen aan transcript ----------

    /// <summary>
    /// Ken aan elk transcript-segment de spreker met de grootste tijdsoverlap toe
    /// en geef een nieuwe lijst met ingevulde sprekerlabels terug.
    /// </summary>
    public static List<TranscriptSegment> AssignSpeakers(
        IEnumerable<TranscriptSegment> segments, List<SpeakerTurn> turns,
        IReadOnlyDictionary<int, string>? names = null)
    {
        var result = new List<TranscriptSegment>();
        foreach (var seg in segments)
        {
            double segStart = seg.Start.TotalSeconds;
            double segEnd = seg.End.TotalSeconds;
            int best = -1;
            double bestOverlap = 0;
            foreach (var t in turns)
            {
                double ov = Math.Max(0, Math.Min(segEnd, t.End) - Math.Max(segStart, t.Start));
                if (ov > bestOverlap) { bestOverlap = ov; best = t.Speaker; }
            }
            string? label = best < 0 ? null : Label(best, names);
            result.Add(seg with { Speaker = label });
        }
        return result;
    }

    /// <summary>Standaardlabel voor een spreker-id (of een toegekende naam).</summary>
    public static string Label(int speaker, IReadOnlyDictionary<int, string>? names = null)
    {
        if (names != null && names.TryGetValue(speaker, out var n) && !string.IsNullOrWhiteSpace(n))
            return n.Trim();
        return $"Spreker {speaker + 1}";
    }
}
