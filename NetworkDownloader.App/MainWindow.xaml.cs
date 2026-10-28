using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NetworkDownloader.Core;

namespace NetworkDownloader.App;

public partial class MainWindow : Window
{
    private readonly TaskCenterViewModel model;
    private Task? initialization;
    private bool closing;
    private bool canClose;
    private bool autoCollapsed;
    public MainWindow()
    {
        InitializeComponent();
        model = new(Dispatcher) { ShowError = ShowError, OpenFolder = OpenFolder, ChooseFiles = ChooseFilesAsync };
        DataContext = model;
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        initialization = model.InitializeAsync();
        await initialization;
    }
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DetailsExpander is null) return;
        if (e.NewSize.Height < 680 && DetailsExpander.IsExpanded) { DetailsExpander.IsExpanded = false; autoCollapsed = true; }
        else if (e.NewSize.Height >= 720 && autoCollapsed) { DetailsExpander.IsExpanded = true; autoCollapsed = false; }
    }
    private async void New_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewTaskWindow(model.DataDirectory) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await model.AddAsync(dialog.Input, dialog.SaveDirectory, dialog.Adapter);
            dialog.SaveDefaults();
            if (model.Selected is not null) TaskList.ScrollIntoView(model.Selected);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private async Task<int[]?> ChooseFilesAsync(DownloadTaskRecord task, BtMetadata metadata, CancellationToken token)
    {
        return await Dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            var dialog = new BtFilesWindow(metadata) { Owner = this, Title = "选择下载文件 · " + task.Name };
            using var registration = token.Register(() => Dispatcher.BeginInvoke(new Action(dialog.Close)));
            return dialog.ShowDialog() == true ? dialog.SelectedIndices : null;
        });
    }
    private async void Rebind_Click(object sender, RoutedEventArgs e)
    {
        if (model.Selected is not { CanRebind: true } task) return;
        try
        {
            var adapters = NetworkAdapters.GetAvailable();
            var picker = new ComboBox { ItemsSource = adapters, DisplayMemberPath = "DisplayName", SelectedItem = adapters.FirstOrDefault(a => a.Id == task.Record.AdapterId && a.Address.ToString() == task.Record.Address) ?? adapters.FirstOrDefault(), Margin = new(0,12,0,18) };
            var window = new Window { Owner = this, Title = "更换任务网卡", Width = 480, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, FontFamily = FontFamily, FontSize = 12 };
            var confirm = new Button { Content = "保存网卡", Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
            confirm.Click += (_, _) => { if (picker.SelectedItem is not null) window.DialogResult = true; };
            var panel = new StackPanel { Margin = new(22) };
            panel.Children.Add(new TextBlock { Text = "为当前任务选择网卡；不会自动继续下载。", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(picker); panel.Children.Add(confirm); window.Content = panel;
            if (window.ShowDialog() == true && picker.SelectedItem is NetworkAdapter adapter) await model.RebindAsync(task.Id, adapter);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var ids = TaskList.SelectedItems.Cast<DownloadTaskViewModel>().Select(t => t.Id).ToArray();
        if (ids.Length == 0) return;
        if (MessageBox.Show(this, $"移除所选 {ids.Length} 条任务记录？\n\n下载文件、临时文件和断点全部保留。正在运行的任务须先暂停。", "移除记录", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try { await model.RemoveAsync(ids); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void Folder_Click(object sender, RoutedEventArgs e) { if (model.Selected is { } task) OpenFolder(task); }
    private void OpenFolder(DownloadTaskViewModel task)
    {
        try
        {
            var directory = task.Record.IsBt ? task.Record.OutputPath ?? task.Record.SaveDirectory : Path.GetDirectoryName(task.Record.OutputPath) ?? task.Record.SaveDirectory;
            if (!Directory.Exists(directory)) directory = task.Record.SaveDirectory;
            if (!Directory.Exists(directory)) throw new IOException("保存目录尚不存在，或者已被移动。");
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Path.GetFullPath(directory) }, UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (canClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        IsEnabled = false;
        model.Notice = "正在停止任务并保存断点，请稍候…";
        try
        {
            if (initialization is not null) await initialization;
            await model.DisposeAsync();
            canClose = true;
            Close();
        }
        catch (Exception ex)
        {
            closing = false; IsEnabled = true;
            ShowError("退出前保存失败，请保留数据并重试。\n" + ex.Message);
        }
    }
    private void ShowError(string message) => MessageBox.Show(this, message, "下载任务中心", MessageBoxButton.OK, MessageBoxImage.Information);
}
