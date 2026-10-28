using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NetworkDownloader.Core;

namespace NetworkDownloader.App;

public partial class MainWindow : Window
{
    private CancellationTokenSource? downloadCancellation;
    private Task? activeDownload;
    private bool closeWhenStopped;
    private readonly string settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkDownloader", "settings.json");

    public MainWindow()
    {
        InitializeComponent();
        RefreshAdapters();
        LoadSettings();
        if (string.IsNullOrWhiteSpace(PathBox.Text))
            PathBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    private void RefreshAdapters()
    {
        var selectedId = (AdapterBox.SelectedItem as NetworkAdapter)?.Id;
        try
        {
            var adapters = NetworkAdapters.GetAvailable();
            AdapterBox.ItemsSource = adapters;
            AdapterBox.SelectedItem = adapters.FirstOrDefault(x => x.Id == selectedId) ?? adapters.FirstOrDefault();
            StatusText.Text = adapters.Count == 0 ? "未发现已连接的 IPv4 有线 / Wi-Fi 网卡" : "等待下载";
        }
        catch (Exception ex) { StatusText.Text = "读取网卡失败：" + ex.Message; }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshAdapters();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择下载保存目录", Multiselect = false };
        if (Directory.Exists(PathBox.Text.Trim())) dialog.InitialDirectory = Path.GetFullPath(PathBox.Text.Trim());
        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FolderName;
    }

    private void Url_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (FileNameText is null) return;
        FileNameText.Text = Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? $"文件名：{DownloadDestination.GetFileName(url)}"
            : "文件名：输入下载地址后自动识别";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (downloadCancellation is not null) return;
        if (!Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        { ShowError("请输入有效的 HTTP 或 HTTPS 文件地址。"); return; }
        if (AdapterBox.SelectedItem is not NetworkAdapter selected)
        { ShowError("请选择可用的网络适配器。"); return; }
        string destination;
        string directory;
        try
        {
            if (string.IsNullOrWhiteSpace(PathBox.Text)) throw new ArgumentException("请选择保存目录。");
            directory = Path.GetFullPath(PathBox.Text.Trim());
            destination = DownloadDestination.Resolve(url, directory);
            // Re-resolve DHCP address and interface index every time the user starts/resumes.
            selected = NetworkAdapters.GetAvailable().FirstOrDefault(x => x.Id == selected.Id && x.Address.Equals(selected.Address))
                ?? NetworkAdapters.GetAvailable().FirstOrDefault(x => x.Id == selected.Id)
                ?? throw new IOException("所选网络已经断开，请刷新网卡。");
        }
        catch (Exception ex) { ShowError(ex.Message); return; }

        PathBox.Text = directory;
        FileNameText.Text = $"文件名：{Path.GetFileName(destination)}";
        FileNameText.ToolTip = destination;
        SaveSettings();
        downloadCancellation = new CancellationTokenSource();
        SetBusy(true);
        NetworkText.Text = $"当前下载网络：{selected.DisplayName}";
        DownloadBar.Value = 0;
        PercentText.Text = "0%";
        var progress = new Progress<DownloadProgress>(UpdateProgress);
        try
        {
            using var client = BoundHttpClientFactory.Create(selected);
            var service = new DownloadService(client);
            activeDownload = service.DownloadAsync(url, destination, progress, downloadCancellation.Token, GetConnectionCount());
            await activeDownload;
            StatusText.Text = "下载完成";
            DownloadBar.IsIndeterminate = false;
            DownloadBar.Value = 100;
            PercentText.Text = "100%";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已暂停，临时文件已保留";
            DownloadBar.IsIndeterminate = false;
        }
        catch (Exception ex)
        {
            StatusText.Text = "下载失败，临时文件已保留";
            DownloadBar.IsIndeterminate = false;
            if (!closeWhenStopped) ShowError(ex.Message);
        }
        finally
        {
            activeDownload = null;
            downloadCancellation.Dispose();
            downloadCancellation = null;
            SetBusy(false);
            if (closeWhenStopped) Close();
        }
    }

    private void UpdateProgress(DownloadProgress progress)
    {
        // Ignore queued callbacks after the awaited task has completed/paused.
        if (downloadCancellation is null) return;
        StatusText.Text = progress.Message;
        DownloadBar.IsIndeterminate = !progress.Total.HasValue;
        var percent = progress.Total > 0 ? 100.0 * progress.Downloaded / progress.Total.Value : 0;
        DownloadBar.Value = Math.Clamp(percent, 0, 100);
        PercentText.Text = progress.Total.HasValue ? $"{percent:F1}%" : "—";
        DetailsText.Text = $"{FormatSize(progress.Downloaded)} / {(progress.Total.HasValue ? FormatSize(progress.Total.Value) : "未知大小")} · {FormatSize((long)progress.BytesPerSecond)}/s";
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = false;
        StatusText.Text = "正在暂停并保存…";
        downloadCancellation?.Cancel();
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(PathBox.Text)) { ShowError("请选择保存目录。"); return; }
            var directory = Path.GetFullPath(PathBox.Text.Trim());
            if (!Directory.Exists(directory)) { ShowError("保存目录尚不存在。"); return; }
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { directory }, UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void SetBusy(bool busy)
    {
        StartButton.IsEnabled = !busy;
        PauseButton.IsEnabled = busy;
        UrlBox.IsEnabled = PathBox.IsEnabled = AdapterBox.IsEnabled = ConnectionBox.IsEnabled = RefreshButton.IsEnabled = BrowseButton.IsEnabled = !busy;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveSettings();
        if (activeDownload is null) return;
        e.Cancel = true;
        closeWhenStopped = true;
        downloadCancellation?.Cancel();
        StatusText.Text = "正在保存后退出…";
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(settingsPath)) return;
            var settings = System.Text.Json.JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(settingsPath));
            if (settings is null) return;
            UrlBox.Text = settings.Url;
            // Migrate the old full-file-path setting to its containing directory.
            PathBox.Text = !string.IsNullOrWhiteSpace(settings.SaveDirectory) ? settings.SaveDirectory :
                !string.IsNullOrWhiteSpace(settings.Destination) ? Path.GetDirectoryName(Path.GetFullPath(settings.Destination)) : "";
            if (AdapterBox.ItemsSource is IReadOnlyList<NetworkAdapter> adapters)
                AdapterBox.SelectedItem = adapters.FirstOrDefault(x => x.Id == settings.AdapterId) ?? AdapterBox.SelectedItem;
            foreach (ComboBoxItem item in ConnectionBox.Items)
                if (item.Tag?.ToString() == settings.ConnectionCount.ToString()) ConnectionBox.SelectedItem = item;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { StatusText.Text = "无法读取上次设置，已使用默认值。"; }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            // Signed download links may contain credentials. Do not persist their query strings.
            var safeUrl = Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var url) &&
                string.IsNullOrEmpty(url.Query) && string.IsNullOrEmpty(url.UserInfo) ? url.AbsoluteUri : "";
            var settings = new SavedSettings(safeUrl, PathBox.Text, (AdapterBox.SelectedItem as NetworkAdapter)?.Id,
                ConnectionCount: GetConnectionCount());
            File.WriteAllText(settingsPath, System.Text.Json.JsonSerializer.Serialize(settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { /* Settings persistence is optional; it must not interrupt a download. */ }
    }

    private void ShowError(string message) => MessageBox.Show(this, message, "下载器", MessageBoxButton.OK, MessageBoxImage.Information);

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.##} {units[index]}";
    }

    private int GetConnectionCount() =>
        int.TryParse((ConnectionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var count) ? count : 4;

    private sealed record SavedSettings(string Url, string? SaveDirectory, string? AdapterId, string? Destination = null,
        int ConnectionCount = 4);
}
