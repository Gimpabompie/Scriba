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

    private string? _modelPath;

    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        if (_modelPath != null) return;
        _modelPath = await EnsureModelAsync(ct);
    }

    private VadModelConfig BuildConfig()
    {
        // VadModelConfig is een struct: lokaal opbouwen (niet als veld bewaren).
        var config = new VadModelConfig();
        config.SileroVad.Model = _modelPath!;
        config.SileroVad.Threshold = 0.5f;
        config.SileroVad.MinSilenceDuration = 0.5f;
        config.SileroVad.MinSpeechDuration = 0.25f;
        config.SileroVad.WindowSize = Window;
        config.SampleRate = SampleHelpers.TargetRate;
        return config;
    }

    /// <summary>Detecteer spraakstukken in 16 kHz mono samples.</summary>
    public async Task<List<SpeechChunk>> SegmentAsync(float[] samples, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        Status?.Invoke("Spraak detecteren…");

        return await Task.Run(() =>
        {
            // Extra zekerheid: een ongeldig/beschadigd model laat de native
            // VoiceActivityDetector-constructor crashen met een AccessViolation
            // (niet op te vangen in .NET). Daarom valideren we het bestand hier
            // nog één keer en gooien we anders een nette, opvangbare fout.
            if (!LooksLikeOnnx(_modelPath))
            {
                try { if (_modelPath != null) File.Delete(_modelPath); } catch { }
                _modelPath = null;
                throw new InvalidOperationException(
                    "Het spraakdetectie-model (silero_vad.onnx) is ongeldig of " +
                    "onvolledig gedownload. Het bestand is verwijderd; transcriptie " +
                    "gaat door zonder ruisfilter.");
            }

            // Buffer ruim genoeg voor lange spraakstukken.
            var vad = new VoiceActivityDetector(BuildConfig(), 120.0f);
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
        // Bestaand model alleen hergebruiken als het er geldig uitziet.
        var existing = AppSettings.FindExistingModel(ModelFile);
        if (existing != null && LooksLikeOnnx(existing)) return existing;

        Directory.CreateDirectory(AppSettings.ModelsDir);
        var dest = Path.Combine(AppSettings.ModelsDir, ModelFile);

        // Eerder (beschadigd) bestand opruimen zodat het opnieuw wordt opgehaald.
        try { if (File.Exists(dest)) File.Delete(dest); } catch { }

        var url = Environment.GetEnvironmentVariable("SCRIBA_VAD_URL") ?? ModelUrl;

        Status?.Invoke("Spraakdetectie-model downloaden…");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1L;
        var tmp = dest + ".part";
        long read = 0;
        await using (var fsOut = File.Create(tmp))
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[81920];
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

        // Onvolledige download (afgekapt) opruimen i.p.v. een kapot bestand laten staan.
        if (total > 0 && read != total)
        {
            try { File.Delete(tmp); } catch { }
            throw new InvalidOperationException(
                "De download van het spraakdetectie-model is onderbroken " +
                $"({read}/{total} bytes). Probeer het later opnieuw.");
        }

        File.Move(tmp, dest, true);
        DownloadProgress?.Invoke(1.0);

        // Controleer of het een echt ONNX-bestand is (niet bv. een foutpagina).
        if (!LooksLikeOnnx(dest))
        {
            try { File.Delete(dest); } catch { }
            throw new InvalidOperationException(
                "Het gedownloade spraakdetectie-model is ongeldig (geen geldig " +
                "ONNX-bestand). Het bestand is verwijderd; probeer het opnieuw.");
        }
        return dest;
    }

    /// <summary>
    /// Globale check of een bestand een geldig ONNX-model lijkt. ONNX is
    /// protobuf (geen vaste magic), dus we sluiten vooral foute downloads uit:
    /// HTML-/foutpagina's en (veel) te kleine of ontbrekende bestanden.
    /// silero_vad.onnx is ~1,8 MB.
    /// </summary>
    private static bool LooksLikeOnnx(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return false;
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < 500_000) return false; // veel te klein
            using var fs = File.OpenRead(path);
            int first = fs.ReadByte();
            // '<' (0x3C) = begin van een HTML-/XML-foutpagina; spaties/lege regel
            // = ook geen binair model.
            if (first is '<' or ' ' or '\n' or '\r' or '\t' or -1) return false;
            return true;
        }
        catch { return false; }
    }
}
