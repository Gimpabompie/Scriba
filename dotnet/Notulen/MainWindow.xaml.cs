using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Notulen.Services;

namespace Notulen;

public partial class MainWindow : Window
{
    // Weergavenaam -> whisper-taalcode ("" = automatisch detecteren)
    private static readonly Dictionary<string, string> Languages = new()
    {
        ["Automatisch detecteren"] = "",
        ["Nederlands"] = "nl",
        ["Engels"] = "en",
    };

    private static readonly string[] Models = { "tiny", "base", "small", "medium", "large-v3" };

    private const string Placeholder =
        "Het transcript verschijnt hier zodra je opneemt of een audiobestand laadt.";

    private readonly AppSettings _settings;
    private readonly AudioRecorder _recorder = new();
    private readonly Transcriber _transcriber = new();

    private List<TranscriptSegment>? _result;
    private bool _busy;
    private bool _hasPlaceholder = true;
    private bool _keepAudio = true;
    private string? _tempAudio;

    private static readonly Brush Accent = (Brush)new BrushConverter().ConvertFrom("#4F46E5")!;
    private static readonly Brush Good = (Brush)new BrushConverter().ConvertFrom("#16A34A")!;
    private static readonly Brush Warn = (Brush)new BrushConverter().ConvertFrom("#F59E0B")!;
    private static readonly Brush Bad = (Brush)new BrushConverter().ConvertFrom("#DC2626")!;
    private static readonly Brush Rec = (Brush)new BrushConverter().ConvertFrom("#DC2626")!;
    private static readonly Brush Muted = (Brush)new BrushConverter().ConvertFrom("#6B7280")!;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();

        LangBox.ItemsSource = Languages.Keys.ToList();
        ModelBox.ItemsSource = Models;
        LangBox.SelectedItem = Languages.ContainsKey(_settings.Language)
            ? _settings.Language : "Automatisch detecteren";
        ModelBox.SelectedItem = Models.Contains(_settings.Model) ? _settings.Model : "small";
        TimestampsBox.IsChecked = _settings.Timestamps;
        SaveAudioBox.IsChecked = _settings.SaveAudio;
        VocabBox.Text = _settings.Vocabulary;

        _recorder.LevelChanged += (db, frac, clip) =>
            Dispatcher.Invoke(() => UpdateLevel(db, frac, clip));
        _transcriber.Status += msg =>
            Dispatcher.Invoke(() => SetStatus(msg, _busy ? Warn : Good));
        _transcriber.DownloadProgress += frac =>
            Dispatcher.Invoke(() => ShowDownloadProgress(frac));

        PopulateDevices();
        Closing += (_, _) => OnClosing();
    }

    // ---------- Apparaten ----------
    private void Refresh_Click(object sender, RoutedEventArgs e) => PopulateDevices();

    private void PopulateDevices()
    {
        DeviceBox.Items.Clear();
        DeviceBox.Items.Add("(standaard microfoon)");
        foreach (var d in AudioRecorder.ListDevices())
            DeviceBox.Items.Add(d);

        var saved = _settings.Device;
        object? match = DeviceBox.Items.Cast<object>()
            .FirstOrDefault(i => i.ToString() == saved);
        DeviceBox.SelectedItem = match ?? DeviceBox.Items[0];
    }

    private AudioDevice? SelectedDevice() => DeviceBox.SelectedItem as AudioDevice;

    // ---------- Opname ----------
    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        if (_busy) return;
        Directory.CreateDirectory(AppSettings.RecordingsDir);
        var path = Path.Combine(AppSettings.RecordingsDir,
            $"notulen-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
        _keepAudio = SaveAudioBox.IsChecked == true;

        try
        {
            _recorder.Start(path, SelectedDevice());
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Audiobron", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RecordBtn.Content = "■  Opname stoppen";
        RecordBtn.Style = (Style)FindResource("RecordingButton");
        FileBtn.IsEnabled = false;
        SetStatus(_keepAudio ? $"Opname loopt… (opslag: {path})" : "Opname loopt… spreek maar.", Rec);
    }

    private void StopRecording()
    {
        var samples = _recorder.Stop();
        RecordBtn.Content = "●  Opname starten";
        RecordBtn.Style = (Style)FindResource("AccentButton");
        FileBtn.IsEnabled = true;
        LevelBar.Value = 0;
        LevelHint.Text = "";

        if (samples.Length == 0)
        {
            SetStatus("Geen audio opgenomen.", Good);
            return;
        }
        SetStatus(_recorder.Clipped
            ? "Let op: oversturing gedetecteerd. Transcriberen…"
            : "Opname gestopt. Transcriberen…", Warn);

        _tempAudio = _keepAudio ? null : _recorder.Path;
        _ = RunTranscription(samples);
    }

    private void UpdateLevel(double dbfs, double fraction, bool clipping)
    {
        LevelBar.Value = Math.Clamp(fraction * 100, 0, 100);
        if (clipping)
        {
            LevelBar.Foreground = Bad; LevelHint.Text = "⚠ te luid"; LevelHint.Foreground = Bad;
        }
        else if (dbfs < -45)
        {
            LevelBar.Foreground = Warn; LevelHint.Text = "⚠ erg zacht"; LevelHint.Foreground = Warn;
        }
        else
        {
            LevelBar.Foreground = Good; LevelHint.Text = ""; LevelHint.Foreground = Muted;
        }
    }

    // ---------- Bestand laden ----------
    private void LoadFile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dlg = new OpenFileDialog
        {
            Title = "Kies een audiobestand",
            Filter = "Audio|*.wav;*.mp3;*.m4a;*.flac;*.ogg;*.aac;*.wma;*.mp4|Alle bestanden|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var samples = SampleHelpers.LoadFile(dlg.FileName);
            _tempAudio = null; // nooit een geladen bestand verwijderen
            _ = RunTranscription(samples);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Bestand", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- Transcriptie ----------
    private async Task RunTranscription(float[] samples)
    {
        ClearTranscript();
        SetBusy(true);

        var model = (string)ModelBox.SelectedItem;
        var language = Languages[(string)LangBox.SelectedItem];
        var vocab = VocabBox.Text.Trim();
        var timestamps = TimestampsBox.IsChecked == true;

        try
        {
            var segments = await Task.Run(() => _transcriber.TranscribeAsync(
                samples, model, language, vocab,
                seg => Dispatcher.Invoke(() => AppendSegment(seg, timestamps))));
            _result = segments;
            SaveBtn.IsEnabled = segments.Count > 0;
            SetStatus("Klaar.", Good);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Fout opgetreden.", Bad);
        }
        finally
        {
            SetBusy(false);
            CleanupTemp();
            ResetLevelBar();
        }
    }

    private void ShowDownloadProgress(double fraction)
    {
        LevelBar.Foreground = Accent;
        if (fraction < 0)
        {
            LevelBar.IsIndeterminate = true; // onbekende grootte
        }
        else
        {
            LevelBar.IsIndeterminate = false;
            LevelBar.Value = Math.Clamp(fraction * 100, 0, 100);
        }
    }

    private void ResetLevelBar()
    {
        LevelBar.IsIndeterminate = false;
        LevelBar.Value = 0;
        LevelBar.Foreground = Good;
    }

    private void AppendSegment(TranscriptSegment seg, bool timestamps)
    {
        ClearPlaceholder();
        var line = timestamps
            ? $"[{Minutes.Ts(seg.Start)} - {Minutes.Ts(seg.End)}] {seg.Text}\n"
            : seg.Text + " ";
        TranscriptBox.AppendText(line);
        TranscriptBox.ScrollToEnd();
    }

    // ---------- Opslaan / wissen ----------
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Notulen opslaan",
            FileName = $"notulen-{DateTime.Now:yyyyMMdd}",
            DefaultExt = ".md",
            Filter = "Markdown|*.md|Tekst|*.txt",
        };
        if (dlg.ShowDialog() != true) return;

        var timestamps = TimestampsBox.IsChecked == true;
        var content = Minutes.Format(_result, timestamps);
        var header = dlg.FileName.EndsWith(".md")
            ? $"# Notulen\n\nDatum: {DateTime.Now:yyyy-MM-dd HH:mm}\n\n---\n\n"
            : "";
        File.WriteAllText(dlg.FileName, header + content + "\n");
        SetStatus($"Opgeslagen: {dlg.FileName}", Good);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ClearTranscript();

    private void ClearTranscript()
    {
        TranscriptBox.Clear();
        TranscriptBox.Foreground = (Brush)FindResource("Ink");
        _hasPlaceholder = false;
        _result = null;
        SaveBtn.IsEnabled = false;
    }

    private void ClearPlaceholder()
    {
        if (_hasPlaceholder)
        {
            TranscriptBox.Clear();
            TranscriptBox.Foreground = (Brush)FindResource("Ink");
            _hasPlaceholder = false;
        }
    }

    // ---------- Hulp ----------
    private void SetBusy(bool busy)
    {
        _busy = busy;
        RecordBtn.IsEnabled = !busy;
        FileBtn.IsEnabled = !busy;
    }

    private void SetStatus(string text, Brush dot)
    {
        StatusText.Text = text;
        StatusDot.Foreground = dot;
    }

    private void CleanupTemp()
    {
        if (_tempAudio != null)
        {
            try { File.Delete(_tempAudio); } catch { }
            _tempAudio = null;
        }
    }

    private void OnClosing()
    {
        if (_recorder.IsRecording)
        {
            try { _recorder.Stop(); } catch { }
        }
        _settings.Language = (string)LangBox.SelectedItem;
        _settings.Model = (string)ModelBox.SelectedItem;
        _settings.Timestamps = TimestampsBox.IsChecked == true;
        _settings.SaveAudio = SaveAudioBox.IsChecked == true;
        _settings.Device = DeviceBox.SelectedItem?.ToString();
        _settings.Vocabulary = VocabBox.Text.Trim();
        _settings.Save();
    }
}
