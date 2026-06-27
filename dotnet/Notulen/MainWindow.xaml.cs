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
    private readonly Summarizer _summarizer = new();
    private readonly SpeakerDiarizer _diarizer = new();

    private List<SpeakerTurn>? _lastTurns;                 // laatste diarization-resultaat
    private Dictionary<int, string> _speakerNames = new(); // hernoemde sprekers

    private List<TranscriptSegment>? _result;
    private bool _busy;
    private bool _hasPlaceholder = true;
    private bool _keepAudio = true;
    private string? _tempAudio;

    // Live-transcriptie tijdens het opnemen.
    private CancellationTokenSource? _liveCts;
    private Task? _liveTask;
    private int _liveCutSamples;
    private List<TranscriptSegment> _liveSegments = new();
    private string _liveModel = "small";
    private string _liveLang = "";
    private string _liveVocab = "";
    private bool _liveTimestamps = true;

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
        LiveBox.IsChecked = _settings.Live;
        DiarizeBox.IsChecked = _settings.Diarize;
        VocabBox.Text = _settings.Vocabulary;

        _recorder.LevelChanged += (db, frac, clip) =>
            Dispatcher.Invoke(() => UpdateLevel(db, frac, clip));
        _transcriber.Status += msg =>
            Dispatcher.Invoke(() => SetStatus(msg, _busy ? Warn : Good));
        _transcriber.DownloadProgress += frac =>
            Dispatcher.Invoke(() => ShowDownloadProgress(frac));
        _diarizer.Status += msg => Dispatcher.Invoke(() => SetStatus(msg, Warn));
        _diarizer.DownloadProgress += frac =>
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

    private void OpenRecordings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.RecordingsDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppSettings.RecordingsDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Opnamemap", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- Opname ----------
    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording) await StopRecording();
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
        _tempAudio = _keepAudio ? null : path;

        if (LiveBox.IsChecked == true)
        {
            // Vastleggen wat we tijdens deze opname gebruiken (UI mag niet wijzigen).
            _liveModel = (string)ModelBox.SelectedItem;
            _liveLang = Languages[(string)LangBox.SelectedItem];
            _liveVocab = VocabBox.Text.Trim();
            _liveTimestamps = TimestampsBox.IsChecked == true;
            _liveSegments = new List<TranscriptSegment>();
            _liveCutSamples = 0;
            ClearTranscript();
            SetStatus("Live transcriberen… spreek maar.", Rec);

            // Model alvast (laten) laden terwijl je spreekt.
            _ = _transcriber.EnsureReadyAsync(_liveModel);
            _liveCts = new CancellationTokenSource();
            _liveTask = LiveLoop(_liveCts.Token);
        }
        else
        {
            SetStatus(_keepAudio ? $"Opname loopt… (opslag: {path})" : "Opname loopt… spreek maar.", Rec);
        }
    }

    private async Task StopRecording()
    {
        bool live = _liveTask != null;
        if (live)
        {
            _liveCts!.Cancel();
            try { await _liveTask!; } catch { }
            _liveTask = null;
        }

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

        if (live)
        {
            // Verwerk het laatste stuk dat nog niet live is getranscribeerd.
            if (samples.Length > _liveCutSamples)
            {
                SetStatus("Laatste stuk transcriberen…", Warn);
                var tail = samples[_liveCutSamples..];
                await TranscribeLiveChunk(tail, _liveCutSamples / 16000.0, LiveContextTail(), CancellationToken.None);
            }
            _result = _liveSegments;
            EnableResultButtons(_liveSegments.Count > 0);
            SetStatus(_recorder.Clipped ? "Klaar. (let op: oversturing gehoord)" : "Klaar.", Good);
            CleanupTemp();
            ResetLevelBar();
            return;
        }

        SetStatus(_recorder.Clipped
            ? "Let op: oversturing gedetecteerd. Transcriberen…"
            : "Opname gestopt. Transcriberen…", Warn);
        _ = RunTranscription(samples);
    }

    // ---------- Live-transcriptie ----------
    private async Task LiveLoop(CancellationToken token)
    {
        const double minChunk = 4.0;  // langere stukjes = meer context = beter
        const double maxChunk = 22.0; // forceer een knip bij doorpraten
        const double silenceCut = 0.6; // knip na een pauze

        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(350, token);
                if (!_transcriber.IsReady(_liveModel)) continue; // wacht tot model klaar is

                int total = _recorder.SampleCount;
                int pending = total - _liveCutSamples;
                double pendingSec = pending / 16000.0;
                if (pendingSec < minChunk) continue;

                bool silent = _recorder.TrailingSilenceSeconds >= silenceCut;
                if (silent || pendingSec >= maxChunk)
                {
                    var chunk = _recorder.Snapshot(_liveCutSamples, pending);
                    double offset = _liveCutSamples / 16000.0;
                    _liveCutSamples = total;

                    // Sla (vrijwel) stille stukken over: voorkomt verzonnen tekst.
                    if (chunk.Length == 0 || PeakOf(chunk) < 0.02f) continue;

                    // Geef de laatste woorden mee als context voor betere aansluiting.
                    string context = LiveContextTail();
                    await TranscribeLiveChunk(chunk, offset, context, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normaal bij stoppen.
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => SetStatus($"Live-fout: {ex.Message}", Bad));
        }
    }

    private async Task TranscribeLiveChunk(
        float[] chunk, double offsetSeconds, string context, CancellationToken token)
    {
        await _transcriber.TranscribeAsync(
            chunk, _liveModel, _liveLang, _liveVocab,
            seg => Dispatcher.Invoke(() =>
            {
                _liveSegments.Add(seg);
                AppendSegment(seg, _liveTimestamps);
                EnableResultButtons(true);
            }),
            token, offsetSeconds, announce: false, contextPrompt: context);
    }

    private static float PeakOf(float[] samples)
    {
        float peak = 0;
        foreach (var s in samples)
        {
            float a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        return peak;
    }

    private string LiveContextTail()
    {
        // Laatste ~200 tekens van de tot nu toe herkende tekst.
        var text = string.Join(" ", _liveSegments.Select(s => s.Text));
        return text.Length > 200 ? text[^200..] : text;
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
            EnableResultButtons(segments.Count > 0);

            if (DiarizeBox.IsChecked == true && segments.Count > 0)
                await RunDiarization(samples, timestamps);

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

    private async Task RunDiarization(float[] samples, bool timestamps)
    {
        try
        {
            var turns = await _diarizer.DiarizeAsync(samples);
            _lastTurns = turns;
            _speakerNames = new Dictionary<int, string>();
            if (_result != null && turns.Count > 0)
            {
                _result = SpeakerDiarizer.AssignSpeakers(_result, turns);
                RenderResult(timestamps);
                RenameSpeakersBtn.IsEnabled = true;
                int n = turns.Select(t => t.Speaker).Distinct().Count();
                SetStatus($"{n} spreker(s) herkend.", Good);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Sprekerherkenning is niet gelukt:\n" + ex.Message +
                "\n\nDe transcriptie zelf is wel klaar.",
                "Sprekers", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RenderResult(bool timestamps)
    {
        if (_result == null) return;
        _hasPlaceholder = false;
        TranscriptBox.Foreground = (Brush)FindResource("Ink");
        TranscriptBox.Text = Minutes.Format(_result, timestamps);
        TranscriptBox.ScrollToEnd();
    }

    private void RenameSpeakers_Click(object sender, RoutedEventArgs e)
    {
        if (_lastTurns == null || _result == null) return;
        var ids = _lastTurns.Select(t => t.Speaker).Distinct().OrderBy(x => x).ToList();
        var dlg = new RenameSpeakersWindow(ids, _speakerNames) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _speakerNames = dlg.Names;
            _result = SpeakerDiarizer.AssignSpeakers(_result, _lastTurns, _speakerNames);
            RenderResult(TimestampsBox.IsChecked == true);
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
        _lastTurns = null;
        _speakerNames = new Dictionary<int, string>();
        RenameSpeakersBtn.IsEnabled = false;
        EnableResultButtons(false);
    }

    private void EnableResultButtons(bool enabled)
    {
        SaveBtn.IsEnabled = enabled;
        SummarizeBtn.IsEnabled = enabled;
    }

    private void Summarize_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null || _result.Count == 0) return;
        var transcript = Minutes.Format(_result, withTimestamps: false);
        var win = new SummaryWindow(_summarizer, transcript) { Owner = this };
        win.Show();
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
        try { _liveCts?.Cancel(); } catch { }
        if (_recorder.IsRecording)
        {
            try { _recorder.Stop(); } catch { }
        }
        _settings.Language = (string)LangBox.SelectedItem;
        _settings.Model = (string)ModelBox.SelectedItem;
        _settings.Timestamps = TimestampsBox.IsChecked == true;
        _settings.SaveAudio = SaveAudioBox.IsChecked == true;
        _settings.Live = LiveBox.IsChecked == true;
        _settings.Diarize = DiarizeBox.IsChecked == true;
        _settings.Device = DeviceBox.SelectedItem?.ToString();
        _settings.Vocabulary = VocabBox.Text.Trim();
        _settings.Save();
    }
}
