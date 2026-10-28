using System.ComponentModel;
using System.Windows;
using NetworkDownloader.Core;

namespace NetworkDownloader.App;

public partial class BtFilesWindow : Window
{
    private readonly List<FileChoice> choices;
    public int[] SelectedIndices => choices.Where(f => f.IsSelected).Select(f => f.File.Index).ToArray();
    public BtFilesWindow(BtMetadata metadata)
    {
        choices = metadata.Files.Where(f => !f.Padding).Select(f => new FileChoice(f)
            { IsSelected = metadata.SelectedFiles is null || metadata.SelectedFiles.Contains(f.Index) }).ToList();
        InitializeComponent();
        NameText.Text = metadata.Name;
        NameText.ToolTip = metadata.Hash;
        FilesList.ItemsSource = choices;
        UpdateSelection();
    }
    private void Selection_Changed(object sender, RoutedEventArgs e) { if (SelectionText is not null) UpdateSelection(); }
    private void All_Click(object sender, RoutedEventArgs e) { foreach (var file in choices) file.IsSelected = true; UpdateSelection(); }
    private void None_Click(object sender, RoutedEventArgs e) { foreach (var file in choices) file.IsSelected = false; UpdateSelection(); }
    private void Download_Click(object sender, RoutedEventArgs e) { if (SelectedIndices.Length > 0) DialogResult = true; }
    private void UpdateSelection()
    {
        var selected = choices.Where(f => f.IsSelected).ToArray();
        SelectionText.Text = $"已选 {selected.Length} / {choices.Count} 个文件 · {FormatSize(selected.Sum(f => f.File.Size))}";
        DownloadButton.IsEnabled = selected.Length > 0;
    }
    private static string FormatSize(long bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        double value = bytes; var i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {units[i]}";
    }
    private sealed class FileChoice(BtFile file) : INotifyPropertyChanged
    {
        private bool selected = true;
        public BtFile File { get; } = file;
        public string SizeText => FormatSize(File.Size);
        public bool IsSelected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
