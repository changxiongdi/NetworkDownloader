using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NetworkDownloader.Core;

namespace NetworkDownloader.App;

public partial class NewTaskWindow : Window
{
    private readonly string settingsPath;
    public DownloadInput Input { get; private set; } = null!;
    public NetworkAdapter Adapter { get; private set; } = null!;
    public string SaveDirectory { get; private set; } = "";

    public NewTaskWindow(string dataDirectory)
    {
        InitializeComponent();
        settingsPath = Path.Combine(dataDirectory, "settings.json");
        PathBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        RefreshAdapters();
        try
        {
            if (!File.Exists(settingsPath)) return;
            var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath));
            if (saved is null) return;
            UrlBox.Text = saved.Url ?? "";
            PathBox.Text = saved.SaveDirectory ?? (!string.IsNullOrWhiteSpace(saved.Destination) ? Path.GetDirectoryName(saved.Destination) : PathBox.Text);
            AdapterBox.SelectedItem = ((IReadOnlyList<NetworkAdapter>)AdapterBox.ItemsSource).FirstOrDefault(a => a.Id == saved.AdapterId) ?? AdapterBox.SelectedItem;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }
    private void RefreshAdapters()
    {
        try
        {
            var previous = AdapterBox.SelectedItem as NetworkAdapter;
            var adapters = NetworkAdapters.GetAvailable();
            AdapterBox.ItemsSource = adapters;
            AdapterBox.SelectedItem = adapters.FirstOrDefault(a => a.Id == previous?.Id && a.Address.Equals(previous.Address)) ?? adapters.FirstOrDefault();
        }
        catch (Exception ex) { Error(ex.Message); }
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshAdapters();
    private void Url_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (FileNameText is null) return;
        try
        {
            var input = DownloadInput.Parse(UrlBox.Text);
            FileNameText.Text = input.Kind == DownloadKind.Http ? "文件名：" + DownloadDestination.GetFileName(new(input.Value))
                : input.Kind == DownloadKind.Magnet ? "已识别 Hash：" + input.InfoHash : "种子文件：" + Path.GetFileName(input.Value);
            FileNameText.ToolTip = FileNameText.Text;
        }
        catch (ArgumentException) { FileNameText.Text = "输入有效地址后自动识别文件名称"; }
    }
    private void Torrent_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "打开种子文件", Filter = "BitTorrent 种子 (*.torrent)|*.torrent", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) UrlBox.Text = dialog.FileName;
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择保存目录", Multiselect = false };
        if (Directory.Exists(PathBox.Text.Trim())) dialog.InitialDirectory = Path.GetFullPath(PathBox.Text.Trim());
        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FolderName;
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Input = DownloadInput.Parse(UrlBox.Text);
            Adapter = AdapterBox.SelectedItem as NetworkAdapter ?? throw new ArgumentException("请选择可用的网络适配器。");
            if (string.IsNullOrWhiteSpace(PathBox.Text)) throw new ArgumentException("请选择保存目录。");
            SaveDirectory = Path.GetFullPath(PathBox.Text.Trim());
            if (File.Exists(SaveDirectory)) throw new ArgumentException("保存位置必须是目录。");
            if (Input.Kind == DownloadKind.TorrentFile && !File.Exists(Input.Value)) throw new FileNotFoundException("种子文件不存在。");
            DialogResult = true;
        }
        catch (Exception ex) { Error(ex.Message); }
    }
    public void SaveDefaults()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var safeUrl = Input.Kind == DownloadKind.Http && string.IsNullOrEmpty(new Uri(Input.Value).Query) ? Input.Value : "";
            File.WriteAllText(settingsPath + ".tmp", JsonSerializer.Serialize(new Settings(safeUrl, SaveDirectory, Adapter.Id)));
            File.Move(settingsPath + ".tmp", settingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private void Error(string message) => MessageBox.Show(this, message, "新建下载", MessageBoxButton.OK, MessageBoxImage.Information);
    private sealed record Settings(string? Url, string? SaveDirectory, string? AdapterId, string? Destination = null);
}
