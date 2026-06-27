using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Notulen.Services;

/// <summary>Hulpfuncties om audio naar 16 kHz mono float-samples te brengen.</summary>
public static class SampleHelpers
{
    public const int TargetRate = 16_000;

    /// <summary>Ruwe opnamebytes (in <paramref name="format"/>) -> 16 kHz mono float[].</summary>
    public static float[] To16kMono(byte[] raw, WaveFormat format)
    {
        if (raw.Length == 0) return Array.Empty<float>();
        using var ms = new MemoryStream(raw);
        var stream = new RawSourceWaveStream(ms, format);
        return Resample(stream.ToSampleProvider());
    }

    /// <summary>Laad een audiobestand (wav/mp3/m4a/…) als 16 kHz mono float[].</summary>
    public static float[] LoadFile(string path)
    {
        using var reader = new AudioFileReader(path);
        return Resample(reader);
    }

    private static float[] Resample(ISampleProvider source)
    {
        ISampleProvider mono = source.WaveFormat.Channels > 1
            ? new MonoSampleProvider(source)
            : source;

        ISampleProvider provider = mono.WaveFormat.SampleRate != TargetRate
            ? new WdlResamplingSampleProvider(mono, TargetRate)
            : mono;

        var result = new List<float>();
        var buffer = new float[TargetRate];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            result.AddRange(buffer.Take(read));
        return result.ToArray();
    }

    public static void WriteWav16kMono(string path, float[] samples)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(TargetRate, 16, 1));
        writer.WriteSamples(samples, 0, samples.Length);
    }
}

/// <summary>Mengt meerdere kanalen naar mono door te middelen.</summary>
public class MonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _buffer = Array.Empty<float>();

    public MonoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int needed = count * _channels;
        if (_buffer.Length < needed) _buffer = new float[needed];

        int read = _source.Read(_buffer, 0, needed);
        int frames = read / _channels;
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++) sum += _buffer[i * _channels + c];
            buffer[offset + i] = sum / _channels;
        }
        return frames;
    }
}
