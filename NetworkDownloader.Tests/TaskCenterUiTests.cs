using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NetworkDownloader.App;
using NetworkDownloader.Core;

internal static class TaskCenterUiTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Create(string testRoot)
    {
        yield return ("Task UI renders compact layouts, filters and virtualizes 10000 history rows", () =>
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { Verify(testRoot); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task;
        });
    }

    private static void Verify(string testRoot)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/NetworkDownloader;component/Theme.xaml", UriKind.Relative) });
        var errors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var main = new MainWindow(); // Never Show or Run: no desktop window, no live task catalog.
        var model = (TaskCenterViewModel)main.DataContext;
        var states = new[] { DownloadTaskState.Downloading, DownloadTaskState.Downloading, DownloadTaskState.Paused, DownloadTaskState.Completed, DownloadTaskState.NetworkUnavailable };
        var names = new[] { "ubuntu-22.04.5-desktop-amd64.iso", "SDK-release.zip", "项目资料.zip", "产品使用手册.pdf", "测试环境镜像.tar" };
        for (var i = 0; i < states.Length; i++)
        {
            model.Tasks.Add(new(new DownloadTaskRecord
            {
                Input = DownloadInput.Parse(i == 0 ? new string('a', 40) : "https://example.test/" + Uri.EscapeDataString(names[i])),
                Name = names[i], SaveDirectory = testRoot, AdapterId = "a", AdapterName = i % 2 == 0 ? "以太网" : "Wi-Fi",
                Address = i % 2 == 0 ? "192.168.142.40" : "192.168.1.20", State = states[i], Total = 1024L * 1024 * 1024,
                Downloaded = states[i] == DownloadTaskState.Completed ? 1024L * 1024 * 1024 : (i + 1) * 120L * 1024 * 1024, BytesPerSecond = i < 2 ? 8L * 1024 * 1024 : 0,
                OutputPath = Path.Combine(testRoot, names[i]), Message = i == 4 ? "指定网卡已断开，未切换默认网络" : "已有断点独立保存",
                ElapsedMilliseconds = (i + 1) * 180000, ElapsedIsPartial = i == 4,
                EstimateState = i < 2 ? DownloadEstimateState.Available : DownloadEstimateState.Inactive,
                EstimatedRemainingSeconds = i < 2 ? 120 : null,
                EstimatedCompletionAt = i < 2 ? DateTimeOffset.UtcNow.AddMinutes(2) : null,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i)
            }));
        }
        model.Search = ""; model.Selected = model.Tasks[0]; model.Notice = "每个任务独立指定网卡 · 暂停后保留断点";
        var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "ui-task-center");
        Directory.CreateDirectory(output);
        var root = (FrameworkElement)main.Content;
        Render(root, 1104, 720, Path.Combine(output, "task-center.png"));
        Check(Descendants<ListBoxItem>(root).Count() == 5, "Navigation categories were clipped or not realized");
        Check(Descendants<TextBlock>(root).Any(t => t.Text == "剩余 约2分钟") && Descendants<TextBlock>(root).Any(t => t.Text == "已用 3分00秒"), "Timing labels missing from visible rows");
        ((Expander)main.FindName("DetailsExpander")).IsExpanded = false;
        Render(root, 884, 580, Path.Combine(output, "task-center-compact.png"));
        var grid = (DataGrid)main.FindName("TaskList");
        Check(grid.Columns.Sum(c => c.ActualWidth) <= grid.ActualWidth, "Timing column clips actions at minimum width");
        Check(Descendants<DataGridRow>(root).All(row => Math.Abs(row.ActualHeight - 68) < 1), "Timing changed compact row height");
        ((Expander)main.FindName("DetailsExpander")).IsExpanded = true;
        model.Category = model.Categories.Single(c => c.Key == "paused");
        Check(model.TaskView.Cast<object>().Count() == 1, "Paused filter failed");
        model.Category = model.Categories[0]; model.Search = "sdk";
        Check(model.TaskView.Cast<DownloadTaskViewModel>().Single().Name == "SDK-release.zip", "Search failed");
        model.Search = "";
        var template = model.Tasks[3].Record;
        model.Tasks.AddRange(Enumerable.Range(0, 10000).Select(i => new DownloadTaskViewModel(template with
            { Id = Guid.NewGuid(), Name = $"历史任务-{i}.zip", CreatedAt = DateTimeOffset.UtcNow.AddDays(-1).AddSeconds(-i) })));
        model.Search = "";
        Render(root, 1104, 720, Path.Combine(output, "task-center-history.png"));
        var realized = Descendants<DataGridRow>(root).Count();
        Check(realized is > 0 and < 100, $"Virtualization failed: {realized} realized rows");
        var dialog = new NewTaskWindow(Path.Combine(testRoot, "ui-defaults"));
        Render((FrameworkElement)dialog.Content, 644, 440, Path.Combine(output, "new-task.png"));
        Check(errors.Messages.Count == 0, "WPF binding errors: " + string.Join("\n", errors.Messages));
        PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
        Console.WriteLine($"  Offscreen WPF images: {output}; virtualized rows: {realized}/10005");
        // No Application.Run, external process, or user-window manipulation.
    }

    private static void Render(FrameworkElement element, int width, int height, string path)
    {
        element.Measure(new(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); png.Save(file);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject element) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
