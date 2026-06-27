using System.Windows;
using System.Windows.Controls;
using Notulen.Services;

namespace Notulen;

public partial class RenameSpeakersWindow : Window
{
    private readonly Dictionary<int, TextBox> _boxes = new();

    public Dictionary<int, string> Names { get; private set; } = new();

    public RenameSpeakersWindow(List<int> speakerIds, IReadOnlyDictionary<int, string> current)
    {
        InitializeComponent();

        foreach (var id in speakerIds)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(new TextBlock
            {
                Text = $"Spreker {id + 1}:",
                Width = 90,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var box = new TextBox
            {
                Width = 200,
                Padding = new Thickness(6, 4, 6, 4),
                Text = current.TryGetValue(id, out var n) ? n : $"Spreker {id + 1}",
            };
            row.Children.Add(box);
            _boxes[id] = box;
            RowsPanel.Children.Add(row);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Names = new Dictionary<int, string>();
        foreach (var (id, box) in _boxes)
            if (!string.IsNullOrWhiteSpace(box.Text))
                Names[id] = box.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
