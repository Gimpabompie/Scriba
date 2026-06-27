using System.IO;
using System.Windows;
using Microsoft.Win32;
using Notulen.Services;

namespace Notulen;

/// <summary>
/// Toont een streamend tekstresultaat van het lokale taalmodel (samenvatting of
/// gecorrigeerd transcript). De daadwerkelijke bewerking wordt als delegate
/// meegegeven, zodat ditzelfde venster voor beide functies dient.
/// </summary>
public partial class SummaryWindow : Window
{
    private readonly Summarizer _summarizer;
    private readonly string _title;
    private readonly Func<Action<string>, CancellationToken, Task> _run;
    private readonly CancellationTokenSource _cts = new();
    private bool _started;

    public SummaryWindow(
        Summarizer summarizer, string title,
        Func<Action<string>, CancellationToken, Task> run)
    {
        InitializeComponent();
        _summarizer = summarizer;
        _title = title;
        _run = run;

        Title = title;
        HeaderText.Text = "📋  " + title;

        _summarizer.Status += OnStatus;
        _summarizer.DownloadProgress += OnProgress;

        Loaded += async (_, _) => await Run();
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _summarizer.Status -= OnStatus;
            _summarizer.DownloadProgress -= OnProgress;
        };
    }

    private async Task Run()
    {
        try
        {
            await Task.Run(() => _run(
                token => Dispatcher.Invoke(() => Append(token)),
                _cts.Token));
            ProgBar.Visibility = Visibility.Collapsed;
            CopyBtn.IsEnabled = true;
            SaveBtn.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            // Venster gesloten tijdens de bewerking.
        }
        catch (Exception ex)
        {
            StatusText.Text = "Fout: " + ex.Message;
        }
    }

    private void Append(string token)
    {
        if (!_started)
        {
            SummaryBox.Clear();
            _started = true;
        }
        SummaryBox.AppendText(token);
        SummaryBox.ScrollToEnd();
    }

    private void OnStatus(string msg) => Dispatcher.Invoke(() => StatusText.Text = msg);

    private void OnProgress(double frac) => Dispatcher.Invoke(() =>
    {
        ProgBar.Visibility = Visibility.Visible;
        if (frac < 0) { ProgBar.IsIndeterminate = true; }
        else { ProgBar.IsIndeterminate = false; ProgBar.Value = Math.Clamp(frac * 100, 0, 100); }
    });

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(SummaryBox.Text); StatusText.Text = "Gekopieerd."; } catch { }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = _title + " opslaan",
            FileName = $"{_title.ToLower().Replace(' ', '-')}-{DateTime.Now:yyyyMMdd}",
            DefaultExt = ".md",
            Filter = "Markdown|*.md|Tekst|*.txt",
        };
        if (dlg.ShowDialog() != true) return;
        var header = dlg.FileName.EndsWith(".md")
            ? $"# {_title}\n\nDatum: {DateTime.Now:yyyy-MM-dd HH:mm}\n\n---\n\n"
            : "";
        File.WriteAllText(dlg.FileName, header + SummaryBox.Text.Trim() + "\n");
        StatusText.Text = "Opgeslagen: " + dlg.FileName;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
