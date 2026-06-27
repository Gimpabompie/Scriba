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
/// Neemt op via WASAPI (microfoon of systeem-/vergaderaudio-loopback), meet het
/// niveau live, en levert 16 kHz mono samples + een WAV-bestand. WASAPI bepaalt
/// zelf de native samplerate; we resamplen achteraf netjes naar 16 kHz.
/// </summary>
public class AudioRecorder
{
    private IWaveIn? _capture;
    private MemoryStream? _buffer;
    private WaveFormat? _format;

    public bool IsRecording => _capture != null;
    public bool Clipped { get; private set; }
    public string? Path { get; private set; }

    /// <summary>(dbfs, fractie 0..1, clipping)</summary>
    public event Action<double, double, bool>? LevelChanged;

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
            // Geen apparaten beschikbaar -> lege lijst, standaard microfoon blijft mogelijk.
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

        _format = _capture.WaveFormat;
        _buffer = new MemoryStream();
        Clipped = false;
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        _capture.DataAvailable += OnData;
        _capture.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        _buffer?.Write(e.Buffer, 0, e.BytesRecorded);

        // Niveaumeting (afhankelijk van het opnameformaat).
        if (LevelChanged == null || _format == null || e.BytesRecorded == 0) return;

        double sumSq = 0;
        double peak = 0;
        int count = 0;
        if (_format.Encoding == WaveFormatEncoding.IeeeFloat && _format.BitsPerSample == 32)
        {
            for (int i = 0; i + 4 <= e.BytesRecorded; i += 4)
            {
                float s = BitConverter.ToSingle(e.Buffer, i);
                sumSq += s * s; peak = Math.Max(peak, Math.Abs(s)); count++;
            }
        }
        else if (_format.BitsPerSample == 16)
        {
            for (int i = 0; i + 2 <= e.BytesRecorded; i += 2)
            {
                float s = BitConverter.ToInt16(e.Buffer, i) / 32768f;
                sumSq += s * s; peak = Math.Max(peak, Math.Abs(s)); count++;
            }
        }
        if (count == 0) return;

        double rms = Math.Sqrt(sumSq / count);
        bool clipping = peak >= 0.99;
        if (clipping) Clipped = true;
        double dbfs = rms <= 1e-9 ? -120.0 : 20.0 * Math.Log10(Math.Min(rms, 1.0));
        double frac = dbfs <= -60 ? 0 : (dbfs >= 0 ? 1 : (dbfs + 60) / 60.0);
        LevelChanged?.Invoke(dbfs, frac, clipping);
    }

    /// <summary>Stop en geef 16 kHz mono samples terug; schrijft ook het WAV-bestand.</summary>
    public float[] Stop()
    {
        if (_capture == null || _buffer == null || _format == null)
            throw new InvalidOperationException("Er is geen opname bezig.");

        _capture.DataAvailable -= OnData;
        _capture.StopRecording();
        _capture.Dispose();
        _capture = null;

        var samples = SampleHelpers.To16kMono(_buffer.ToArray(), _format);
        _buffer.Dispose();
        _buffer = null;

        if (samples.Length > 0 && Path != null)
            SampleHelpers.WriteWav16kMono(Path, samples);

        return samples;
    }
}
