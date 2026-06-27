using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Notulen.Services;

/// <summary>Een selecteerbaar audio-apparaat (microfoon of systeemaudio).</summary>
public record AudioDevice(string Id, string Name, bool IsLoopback)
{
    public string Label => $"{Name}  ({(IsLoopback ? "systeemaudio" : "microfoon")})";
    public override string ToString() => Label;
}

/// <summary>
/// Neemt op via WASAPI (microfoon of systeem-/vergaderaudio-loopback) en zet de
/// audio meteen om naar 16 kHz mono, zodat de samples live beschikbaar zijn voor
/// transcriptie tijdens het opnemen. Schrijft ook incrementeel een WAV-backup.
/// </summary>
public class AudioRecorder
{
    private const float SilenceThreshold = 0.02f; // ~ -34 dBFS

    private readonly object _lock = new();
    private IWaveIn? _capture;
    private BufferedWaveProvider? _buffered;
    private ISampleProvider? _resampler;
    private WaveFileWriter? _wav;
    private float[] _readBuf = new float[16_000];
    private readonly List<float> _samples = new();
    private double _silenceSeconds;

    public bool IsRecording => _capture != null;
    public bool Clipped { get; private set; }
    public string? Path { get; private set; }

    /// <summary>Aantal stilte-seconden direct vóór 'nu' (voor live-knippunten).</summary>
    public double TrailingSilenceSeconds { get { lock (_lock) return _silenceSeconds; } }

    /// <summary>Aantal beschikbare 16 kHz mono samples tot nu toe.</summary>
    public int SampleCount { get { lock (_lock) return _samples.Count; } }

    /// <summary>(dbfs, fractie 0..1, clipping)</summary>
    public event Action<double, double, bool>? LevelChanged;

    /// <summary>Kopieer een bereik van de tot nu toe opgenomen 16 kHz samples.</summary>
    public float[] Snapshot(int start, int count)
    {
        lock (_lock)
        {
            if (start < 0 || count <= 0 || start >= _samples.Count) return Array.Empty<float>();
            count = Math.Min(count, _samples.Count - start);
            return _samples.GetRange(start, count).ToArray();
        }
    }

    public static List<AudioDevice> ListDevices()
    {
        var list = new List<AudioDevice>();
        try
        {
            var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                list.Add(new AudioDevice(d.ID, d.FriendlyName, false));
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add(new AudioDevice(d.ID, d.FriendlyName, true));
        }
        catch
        {
            // Geen apparaten -> lege lijst; standaard microfoon blijft mogelijk.
        }
        return list;
    }

    public void Start(string path, AudioDevice? device)
    {
        if (_capture != null)
            throw new InvalidOperationException("Opname is al bezig.");

        MMDevice? mm = null;
        if (device != null)
        {
            var en = new MMDeviceEnumerator();
            var flow = device.IsLoopback ? DataFlow.Render : DataFlow.Capture;
            mm = en.EnumerateAudioEndPoints(flow, DeviceState.Active)
                   .FirstOrDefault(d => d.ID == device.Id);
        }

        try
        {
            if (device?.IsLoopback == true)
                _capture = mm != null ? new WasapiLoopbackCapture(mm) : new WasapiLoopbackCapture();
            else
                _capture = mm != null ? new WasapiCapture(mm) : new WasapiCapture();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Kon de audiobron niet openen ({ex.Message}). Controleer of het juiste " +
                "apparaat gekozen is en niet door een andere app wordt geblokkeerd.", ex);
        }

        var format = _capture.WaveFormat;

        // Pijplijn: ruwe audio -> mono -> 16 kHz, live uitleesbaar.
        _buffered = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(30),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };
        ISampleProvider sp = _buffered.ToSampleProvider();
        if (sp.WaveFormat.Channels > 1) sp = new MonoSampleProvider(sp);
        _resampler = sp.WaveFormat.SampleRate != SampleHelpers.TargetRate
            ? new WdlResamplingSampleProvider(sp, SampleHelpers.TargetRate)
            : sp;

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _wav = new WaveFileWriter(path, new WaveFormat(SampleHelpers.TargetRate, 16, 1));
        Path = path;
        Clipped = false;
        _silenceSeconds = 0;
        _samples.Clear();

        _capture.DataAvailable += OnData;
        _capture.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_buffered == null || _resampler == null) return;
        _buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);

        int read;
        while ((read = _resampler.Read(_readBuf, 0, _readBuf.Length)) > 0)
        {
            double sumSq = 0, peak = 0;
            for (int i = 0; i < read; i++)
            {
                float s = _readBuf[i];
                sumSq += s * s;
                if (Math.Abs(s) > peak) peak = Math.Abs(s);
            }

            lock (_lock)
            {
                for (int i = 0; i < read; i++) _samples.Add(_readBuf[i]);
                try { _wav?.WriteSamples(_readBuf, 0, read); } catch { }

                double secs = read / (double)SampleHelpers.TargetRate;
                if (peak < SilenceThreshold) _silenceSeconds += secs;
                else _silenceSeconds = 0;
            }

            bool clipping = peak >= 0.99;
            if (clipping) Clipped = true;
            double rms = Math.Sqrt(sumSq / Math.Max(1, read));
            double dbfs = rms <= 1e-9 ? -120.0 : 20.0 * Math.Log10(Math.Min(rms, 1.0));
            double frac = dbfs <= -60 ? 0 : (dbfs >= 0 ? 1 : (dbfs + 60) / 60.0);
            LevelChanged?.Invoke(dbfs, frac, clipping);
        }
    }

    /// <summary>Stop en geef alle 16 kHz mono samples terug; sluit de WAV-backup.</summary>
    public float[] Stop()
    {
        if (_capture == null)
            throw new InvalidOperationException("Er is geen opname bezig.");

        _capture.DataAvailable -= OnData;
        _capture.StopRecording();
        _capture.Dispose();
        _capture = null;

        lock (_lock)
        {
            _wav?.Dispose();
            _wav = null;
            _buffered = null;
            _resampler = null;
            return _samples.ToArray();
        }
    }
}
