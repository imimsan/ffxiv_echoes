using System;
using System.IO;
using System.Threading;
using Dalamud.Plugin.Services;

namespace FfxivEchoes.Triggers;

/// <summary>
/// triggers/ ディレクトリの変更を監視し、デバウンス後に <see cref="TriggerStore.Reload"/> を呼ぶ。
/// </summary>
/// <remarks>
/// FileSystemWatcher は短時間にイベントを連発するため、500ms のデバウンス窓でまとめる。
/// Dalamud のメインスレッド以外から呼ばれることに注意（コールバックは worker thread）。
/// </remarks>
public sealed class TriggerWatcher : IDisposable
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(500);

    private readonly TriggerStore _store;
    private readonly IPluginLog _log;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private readonly object _gate = new();

    private bool _pendingReload;
    private bool _disposed;

    public TriggerWatcher(string triggersDir, TriggerStore store, IPluginLog log)
    {
        _store = store;
        _log = log;

        Directory.CreateDirectory(triggersDir);

        _watcher = new FileSystemWatcher(triggersDir, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = false,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;

        _debounce = new Timer(OnDebounceFire, null, Timeout.Infinite, Timeout.Infinite);

        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _watcher.Changed -= OnChanged;
        _watcher.Created -= OnChanged;
        _watcher.Deleted -= OnChanged;
        _watcher.Renamed -= OnRenamed;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounce.Dispose();
    }

    private void OnChanged(object _, FileSystemEventArgs e) => ScheduleReload();
    private void OnRenamed(object _, RenamedEventArgs e) => ScheduleReload();

    private void ScheduleReload()
    {
        lock (_gate)
        {
            _pendingReload = true;
            _debounce.Change(DebounceWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDebounceFire(object? _)
    {
        bool shouldRun;
        lock (_gate)
        {
            shouldRun = _pendingReload;
            _pendingReload = false;
        }
        if (!shouldRun)
        {
            return;
        }

        try
        {
            _store.Reload();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] 自動リロード中に例外");
        }
    }
}
