using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using NetworkDownloader.Core;

namespace NetworkDownloader.App;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class TaskCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new(nameof(Count)));
        OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class AsyncUiCommand(Func<object?, Task> execute, Action<Exception> error) : ICommand
{
    private bool busy;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !busy;
    public async void Execute(object? parameter)
    {
        if (busy) return;
        busy = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(parameter); }
        catch (Exception ex) { error(ex); }
        finally { busy = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}

public sealed class TaskCategory(string key, string name) : ObservableModel
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    private int count;
    public int Count { get => count; set { count = value; Notify(); } }
}

public sealed class DownloadTaskViewModel(DownloadTaskRecord record) : ObservableModel
{
    public DownloadTaskRecord Record { get; private set; } = record;
    public Guid Id => Record.Id;
    public DateTimeOffset CreatedAt => Record.CreatedAt;
    public string Name => Record.Name;
    public string Protocol => Record.IsBt ? "BT" : "HTTP";
    public string Network => $"{Record.AdapterName} · {Record.Address}";
    public string Subtitle => $"{Protocol} · {Network}";
    public string Status => Record.State switch
    {
        DownloadTaskState.Queued => "排队中", DownloadTaskState.Connecting => "连接中",
        DownloadTaskState.ResolvingMetadata => "解析元数据", DownloadTaskState.AwaitingFileSelection => "等待选择",
        DownloadTaskState.Checking => "校验中", DownloadTaskState.Downloading => "下载中",
        DownloadTaskState.Pausing => "正在暂停", DownloadTaskState.Paused => "已暂停",
        DownloadTaskState.Completed => Record.FilesMissing ? "文件缺失" : "已完成",
        DownloadTaskState.NetworkUnavailable => "网络不可用", DownloadTaskState.Interrupted => "待恢复", _ => "下载失败"
    };
    public string StatusColor => Record.State switch
    {
        DownloadTaskState.Completed => Record.FilesMissing ? "#A54B3E" : "#247553",
        DownloadTaskState.Failed or DownloadTaskState.NetworkUnavailable => "#A54B3E",
        DownloadTaskState.Paused or DownloadTaskState.Interrupted => "#8B681F", _ => "#3764E7"
    };
    public string ProgressColor => Record.State is DownloadTaskState.Paused or DownloadTaskState.Interrupted ? "#9BA9BE" : StatusColor;
    public double Percent => Record.State == DownloadTaskState.Completed ? 100 : Record.Total > 0 ? Math.Clamp(100d * Record.Downloaded / Record.Total.Value, 0, 100) : 0;
    public string PercentText => Record.State == DownloadTaskState.Checking ? "校验 / 恢复中" : Record.Total.HasValue ? $"{Percent:F1}%" : "等待大小";
    public bool Indeterminate => Record.State is DownloadTaskState.Checking or DownloadTaskState.ResolvingMetadata or DownloadTaskState.Connecting;
    public string SizeText => $"{FormatSize(Record.Downloaded)} / {(Record.Total.HasValue ? FormatSize(Record.Total.Value) : "未知大小")}";
    public string Rate => Record.State == DownloadTaskState.Downloading ? FormatSize((long)Record.BytesPerSecond) + "/s" : "—";
    public string Message => Record.FilesMissing ? "文件已移动、删除或大小改变；不会自动重新下载。" : Record.Message;
    public string OutputPath => Record.OutputPath ?? Record.SaveDirectory;
    public string CreatedText => Record.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string CompletedText => Record.CompletedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
    public string Connections => Record.IsBt ? "BT 独立工作进程" : Record.IsRunning
        ? $"自动并发 · 当前 {Record.ActiveHttpRequests} 个请求" : "HTTP · 自动并发";
    public string Hash => Record.InfoHash ?? "—";
    public string PrimaryAction => Record.State == DownloadTaskState.Completed ? "打开目录" : CanPause ? "暂停" : Record.State is DownloadTaskState.Failed or DownloadTaskState.NetworkUnavailable ? "重试" : "继续";
    public bool CanPause => Record.IsRunning && Record.State != DownloadTaskState.Pausing || Record.State == DownloadTaskState.Queued;
    public bool CanRebind => Record.CanResume;
    public bool CanPrimary => CanPause || Record.CanResume || Record.State == DownloadTaskState.Completed;
    public void Update(DownloadTaskRecord value) { Record = value; Notify(null); }
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes); var i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {units[i]}";
    }
}

public sealed class TaskCenterViewModel : ObservableModel, IAsyncDisposable
{
    private readonly Dispatcher dispatcher;
    private DownloadTaskStore? store;
    private DownloadTaskCoordinator? coordinator;
    private readonly Dictionary<Guid, DownloadTaskViewModel> lookup = [];
    private TaskCategory category;
    private string search = "";
    private string notice = "正在读取任务记录…";
    private bool ready;
    private DownloadTaskViewModel? selected;
    public TaskCollection<DownloadTaskViewModel> Tasks { get; } = [];
    public ObservableCollection<TaskCategory> Categories { get; } = [new("all", "全部任务"), new("active", "进行中"), new("paused", "已暂停"), new("done", "已完成"), new("failed", "异常任务")];
    public ICollectionView TaskView { get; }
    public Action<string>? ShowError { get; set; }
    public Action<DownloadTaskViewModel>? OpenFolder { get; set; }
    public Func<DownloadTaskRecord, BtMetadata, CancellationToken, Task<int[]?>>? ChooseFiles { get; set; }
    public ICommand PauseSelectedCommand { get; }
    public ICommand ResumeSelectedCommand { get; }
    public ICommand PauseAllCommand { get; }
    public ICommand PrimaryCommand { get; }
    public bool Ready { get => ready; private set { ready = value; Notify(); } }
    public string Notice { get => notice; set { notice = value; Notify(); } }
    public string Heading => Category.Name;
    public TaskCategory Category { get => category; set { if (value is null) return; category = value; TaskView.Refresh(); Notify(); Notify(nameof(Heading)); Notify(nameof(Empty)); } }
    public string Search { get => search; set { search = value; TaskView.Refresh(); Notify(); Notify(nameof(Empty)); } }
    public bool Empty => TaskView.IsEmpty;
    public bool HasSelection => Selected is not null;
    public DownloadTaskViewModel? Selected { get => selected; set { selected = value; Notify(); Notify(nameof(HasSelection)); } }
    public string Summary => $"{Tasks.Count(t => Matches(t.Record, "active"))} 个进行中 · {Tasks.Count(t => Matches(t.Record, "paused"))} 个暂停 · {Tasks.Count(t => t.Record.State == DownloadTaskState.Completed)} 个完成";
    public string TotalSpeed => $"↓ {DownloadTaskViewModel.FormatSize((long)Tasks.Sum(t => t.Record.BytesPerSecond))}/s";
    public string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkDownloader");

    public TaskCenterViewModel(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        category = Categories[0];
        TaskView = CollectionViewSource.GetDefaultView(Tasks);
        TaskView.SortDescriptions.Add(new(nameof(DownloadTaskViewModel.CreatedAt), ListSortDirection.Descending));
        TaskView.Filter = value => value is DownloadTaskViewModel task && Matches(task.Record, Category.Key) &&
            (task.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || task.Network.Contains(Search, StringComparison.OrdinalIgnoreCase));
        Tasks.CollectionChanged += (_, _) => UpdateCounts();
        void Error(Exception ex) => ShowError?.Invoke(ex.Message);
        PauseSelectedCommand = new AsyncUiCommand(p => Coordinator.PauseAsync(Ids(p)), Error);
        ResumeSelectedCommand = new AsyncUiCommand(p => Coordinator.ResumeAsync(Ids(p)), Error);
        PauseAllCommand = new AsyncUiCommand(_ => Coordinator.PauseAsync(Tasks.Where(t => t.CanPause).Select(t => t.Id).ToArray()), Error);
        PrimaryCommand = new AsyncUiCommand(async p =>
        {
            if (p is not DownloadTaskViewModel task) return;
            if (task.Record.State == DownloadTaskState.Completed) OpenFolder?.Invoke(task);
            else if (task.CanPause) await Coordinator.PauseAsync([task.Id]);
            else if (task.Record.CanResume) await Coordinator.ResumeAsync([task.Id]);
        }, Error);
    }

    public async Task InitializeAsync()
    {
        try
        {
            store = await Task.Run(() => new DownloadTaskStore(Path.Combine(DataDirectory, "tasks.db"), new WindowsTaskDataProtector()));
            coordinator = new(store, new DownloadTaskRunner(), Path.Combine(DataDirectory, "torrent-cache"));
            coordinator.ChooseFilesAsync = (task, metadata, token) => ChooseFiles is null ? Task.FromResult<int[]?>(null) : ChooseFiles(task, metadata, token);
            coordinator.TaskChanged += task => dispatcher.BeginInvoke(new Action(() => Update(task)));
            coordinator.TaskRemoved += id => dispatcher.BeginInvoke(new Action(() =>
            {
                if (lookup.Remove(id, out var item)) Tasks.Remove(item);
                UpdateCounts();
            }));
            coordinator.Faulted += message => dispatcher.BeginInvoke(new Action(() => { Ready = false; Notice = message; ShowError?.Invoke(message); }));
            await coordinator.InitializeAsync();
            var loaded = (await coordinator.GetTasksAsync()).Select(task => new DownloadTaskViewModel(task)).ToArray();
            foreach (var task in loaded) lookup.Add(task.Id, task);
            Tasks.AddRange(loaded);
            UpdateCounts();
            Ready = true;
            Notice = "已就绪 · 已暂停和中断任务不会自动启动 · 每个任务独立指定网卡";
        }
        catch (Exception ex)
        {
            Notice = "任务中心初始化失败：数据库不会被覆盖。请确认未重复运行新版程序，并检查磁盘权限。";
            ShowError?.Invoke(Notice + "\n\n" + ex.Message);
        }
    }

    public async Task AddAsync(DownloadInput input, string path, NetworkAdapter adapter)
    {
        var task = await Coordinator.AddAsync(input, path, adapter);
        if (!lookup.ContainsKey(task.Id)) Update(task);
        Category = Categories[0]; Search = ""; Selected = lookup[task.Id];
    }
    public Task RebindAsync(Guid id, NetworkAdapter adapter) => Coordinator.RebindAsync(id, adapter);
    public Task RemoveAsync(Guid[] ids) => Coordinator.RemoveAsync(ids);
    private DownloadTaskCoordinator Coordinator => coordinator ?? throw new InvalidOperationException("任务中心尚未初始化。");
    private static Guid[] Ids(object? value) => value is IList items ? items.Cast<DownloadTaskViewModel>().Select(t => t.Id).ToArray() : [];
    private void Update(DownloadTaskRecord task)
    {
        var refresh = true;
        if (lookup.TryGetValue(task.Id, out var item))
        {
            if (task.Version < item.Record.Version) return;
            refresh = item.Record.State != task.State || item.Record.Name != task.Name || item.Record.AdapterName != task.AdapterName;
            item.Update(task);
        }
        else { item = new(task); lookup.Add(task.Id, item); Tasks.Add(item); }
        if (refresh) { TaskView.Refresh(); UpdateCounts(); }
        Notify(nameof(TotalSpeed));
    }
    private void UpdateCounts()
    {
        foreach (var item in Categories) item.Count = Tasks.Count(t => Matches(t.Record, item.Key));
        Notify(nameof(Summary)); Notify(nameof(Empty)); Notify(nameof(TotalSpeed));
    }
    private static bool Matches(DownloadTaskRecord task, string category) => category switch
    {
        "active" => task.IsRunning || task.State == DownloadTaskState.Queued,
        "paused" => task.State is DownloadTaskState.Paused or DownloadTaskState.Interrupted,
        "done" => task.State == DownloadTaskState.Completed,
        "failed" => task.State is DownloadTaskState.Failed or DownloadTaskState.NetworkUnavailable, _ => true
    };
    public async ValueTask DisposeAsync()
    {
        Ready = false;
        if (coordinator is not null) await coordinator.DisposeAsync();
        store?.Dispose();
    }
}
