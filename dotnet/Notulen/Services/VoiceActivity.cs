using System.Diagnostics;
using System.IO;
using System.Net.Http;
using SherpaOnnx;

namespace Notulen.Services;

/// <summary>Een stuk audio waarin spraak is gedetecteerd.</summary>
public record SpeechChunk(double OffsetSeconds, float[] Samples);

/// <summary>
/// Spraakdetectie (VAD) met Silero via sherpa-onnx. Filtert stiltes/ruis weg
/// vóór de transcriptie, wat verzonnen woorden ("hallucinaties") sterk
/// vermindert. Werkt offline op 16 kHz mono samples.
/// </summary>
public class VoiceActivity
{
    public event Action<string>? Status;
    public event Action<double>? DownloadProgress;

    private const string ModelFile = "silero_vad.onnx";
    private const string ModelUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx";

    private const int Window = 512; // Silero-vensterlengte bij 16 kHz

    private VadModelConfig? _config;

    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        if (_config != null) return;
        var model = await EnsureModelAsync(ct);

        _config = new VadModelConfig();
        _config.SileroVad.Model = model;
        _config.SileroVad.Threshold = 0.5f;
        _config.SileroVad.MinSilenceDuration = 0.5f;
        _config.SileroVad.MinSpeechDuration = 0.25f;
        _config.SileroVad.WindowSize = Window;
        _config.SampleRate = SampleHelpers.TargetRate;
    }

    /// <summary>Detecteer spraakstukken in 16 kHz mono samples.</summary>
    public async Task<List<SpeechChunk>> SegmentAsync(float[] samples, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        Status?.Invoke("Spraak detecteren…");

        return await Task.Run(() =>
        {
            // Buffer ruim genoeg voor lange spraakstukken.
            var vad = new VoiceActivityDetector(_config!, 120.0f);
            var chunks = new List<SpeechChunk>();

            void Drain()
            {
                while (!vad.IsEmpty())
                {
                    var seg = vad.Front();
                    chunks.Add(new SpeechChunk(seg.Start / (double)SampleHelpers.TargetRate, seg.Samples));
                    vad.Pop();
                }
            }

            for (int i = 0; i + Window <= samples.Length; i += Window)
            {
                ct.ThrowIfCancellationRequested();
                var window = new float[Window];
                Array.Copy(samples, i, window, 0, Window);
                vad.AcceptWaveform(window);
                Drain();
            }
            vad.Flush();
            Drain();

            return chunks;
        }, ct);
    }

    private async Task<string> EnsureModelAsync(CancellationToken ct)
    {
        var existing = AppSettings.FindExistingModel(ModelFile);
        if (existing != null) return existing;

        Directory.CreateDirectory(AppSettings.ModelsDir);
        var dest = Path.Combine(AppSettings.ModelsDir, ModelFile);
        var url = Environment.GetEnvironmentVariable("SCRIBA_VAD_URL") ?? ModelUrl;

        Status?.Invoke("Spraakdetectie-model downloaden…");
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
        return dest;
    }
}
