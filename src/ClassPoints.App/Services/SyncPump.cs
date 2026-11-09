using ClassPoints.Data.Sync;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClassPoints.App.Services;

/// <summary>
/// 同步的心跳。前台 45 秒一轮，窗口重新激活时立刻补一次。
///
/// 不做 WebSocket，也不用长连接：两台设备的场景不值得为"实时"付这个复杂度。
/// 重要的是这一条 —— 它永远不在用户点"加一分"的路径上，本地写成功就算成功。
/// </summary>
public sealed class SyncPump(AppServices services)
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(45) };
    private bool _running;
    private bool _busy;

    public DateTime LastAttempt { get; private set; } = DateTime.MinValue;
    public string LastError { get; private set; } = "";
    public int LastPushed { get; private set; }
    public int LastPulled { get; private set; }

    /// <summary>每轮结束后触发，界面用来刷新角标。</summary>
    public event Action? Changed;

    public bool IsBusy => _busy;

    public string StatusText
    {
        get
        {
            if (!services.Sync.IsConfigured) return "未启用";
            if (_busy) return "同步中…";
            if (!string.IsNullOrEmpty(LastError)) return "上次失败：" + LastError;
            return services.Sync.LastSyncText;
        }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _timer.Tick += OnTick;
        _timer.Start();
        _ = RunAsync();
    }

    public void Stop()
    {
        _running = false;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, object e) => _ = RunAsync();

    /// <summary>窗口重新激活时调一次：一体机休眠醒来后 TCP 是假活的，光靠定时器会撞上挂死的连接。</summary>
    public void Wake() => _ = RunAsync();

    public async Task<SyncReport?> RunAsync()
    {
        if (!services.Sync.IsConfigured || _busy) return null;
        _busy = true;
        Notify();
        try
        {
            var report = await services.Sync.RunOnceAsync();
            LastAttempt = DateTime.Now;
            LastError = report.Ok ? "" : report.Error ?? "";
            LastPushed += report.Pushed;
            LastPulled += report.Pulled;
            return report;
        }
        catch (Exception ex)
        {
            services.LogError("sync", ex);
            LastError = ex.Message;
            return null;
        }
        finally
        {
            _busy = false;
            Notify();
        }
    }

    private void Notify()
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null) Changed?.Invoke();
        else queue.TryEnqueue(() => Changed?.Invoke());
    }
}
