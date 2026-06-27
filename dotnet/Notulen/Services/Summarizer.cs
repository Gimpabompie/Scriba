using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Notulen.Services;

/// <summary>
/// Maakt offline een samenvatting (samenvatting + besluiten + actiepunten) van
/// een transcript met een lokaal taalmodel via LLamaSharp (llama.cpp).
/// Het GGUF-model wordt eenmalig gedownload en lokaal gecachet.
/// </summary>
public class Summarizer
{
    public event Action<string>? Status;
    public event Action<double>? DownloadProgress;

    // Klein, meertalig instruct-model (goed in Nederlands), ~2 GB.
    private const string ModelFile = "Qwen2.5-3B-Instruct-Q4_K_M.gguf";
    private const string ModelUrl =
        "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf";

    private LLamaWeights? _weights;
    private ModelParams? _params;

    public bool IsReady => _weights != null;

    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        if (_weights != null) return;
        var modelPath = await EnsureModelAsync(ct);
        Status?.Invoke("Samenvattingsmodel laden…");
        _params = new ModelParams(modelPath) { ContextSize = 8192, GpuLayerCount = 0 };
        _weights = await Task.Run(() => LLamaWeights.LoadFromFile(_params), ct);
    }

    public async Task<string> SummarizeAsync(
        string transcript, Action<string>? onToken, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        var executor = new StatelessExecutor(_weights!, _params!);

        // Beperk de invoer tot de context (kap netjes af bij erg lange notulen).
        string text = transcript.Length > 12000 ? transcript[..12000] : transcript;

        string prompt =
            "<|im_start|>system\n" +
            "Je bent een Nederlandse notulist. Je vat vergaderingen bondig en zakelijk samen.<|im_end|>\n" +
            "<|im_start|>user\n" +
            "Vat de volgende vergadering samen in het Nederlands. Geef:\n" +
            "1. Korte samenvatting (enkele zinnen).\n" +
            "2. Besluiten (opsomming).\n" +
            "3. Actiepunten met (indien genoemd) wie en wanneer (opsomming).\n\n" +
            "Transcript:\n" + text + "<|im_end|>\n" +
            "<|im_start|>assistant\n";

        var inferenceParams = new InferenceParams
        {
            MaxTokens = 800,
            AntiPrompts = new List<string> { "<|im_end|>", "<|im_start|>" },
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.3f },
        };

        Status?.Invoke("Samenvatting schrijven…");
        var sb = new StringBuilder();
        await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct))
        {
            sb.Append(token);
            onToken?.Invoke(token);
        }
        Status?.Invoke("Samenvatting klaar.");
        return sb.ToString().Trim();
    }

    private async Task<string> EnsureModelAsync(CancellationToken ct)
    {
        // Eerst zoeken naar een vooraf geplaatst model (bv. netwerkshare).
        var existing = AppSettings.FindExistingModel(ModelFile);
        if (existing != null) return existing;

        Directory.CreateDirectory(AppSettings.ModelsDir);
        var path = Path.Combine(AppSettings.ModelsDir, ModelFile);

        Status?.Invoke("Samenvattingsmodel wordt eenmalig gedownload…");
        // Interne spiegel/eigen URL mogelijk via NOTULEN_LLM_URL.
        var url = Environment.GetEnvironmentVariable("NOTULEN_LLM_URL") ?? ModelUrl;
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
                if (sw.Elapsed - lastReport >= TimeSpan.FromMilliseconds(350))
                {
                    lastReport = sw.Elapsed;
                    Report(readTotal, total, sw.Elapsed);
                }
            }
            Report(readTotal, total, sw.Elapsed);
        }
        File.Move(tmp, path, true);
        DownloadProgress?.Invoke(1.0);
        return path;
    }

    private void Report(long read, long total, TimeSpan elapsed)
    {
        double mb = read / 1048576.0;
        double secs = Math.Max(0.001, elapsed.TotalSeconds);
        double speed = mb / secs;
        if (total > 0)
        {
            double totMb = total / 1048576.0;
            double pct = read * 100.0 / total;
            double etaSec = speed > 0 ? (totMb - mb) / speed : 0;
            DownloadProgress?.Invoke(read / (double)total);
            Status?.Invoke(
                $"Samenvattingsmodel downloaden… {pct:0}%  " +
                $"({mb:0}/{totMb:0} MB · {speed:0.0} MB/s · nog ~{FormatEta(etaSec)})");
        }
        else
        {
            DownloadProgress?.Invoke(-1);
            Status?.Invoke($"Samenvattingsmodel downloaden… {mb:0} MB ({speed:0.0} MB/s)");
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
}
