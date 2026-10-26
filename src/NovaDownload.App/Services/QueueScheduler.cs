using System.Globalization;
using System.Windows.Threading;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;

namespace NovaDownload.App.Services;

/// <summary>
/// The "Planificateur": while enabled, the queue runs between the start and stop times and pauses
/// outside them. It reacts to the clock (checked every 20 s) instead of running a job, so a machine
/// that sleeps through the start time catches up on the next tick.
/// </summary>
public sealed class QueueScheduler : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly ISettingsService _settings;
    private readonly Func<Task> _startQueue;
    private readonly Func<Task> _stopQueue;
    private bool? _inside;
    private bool _disposed;

    /// <summary>Human-readable line for the status bar when the schedule changes the queue.</summary>
    public event EventHandler<string>? Reported;

    public QueueScheduler(ISettingsService settings, Func<Task> startQueue, Func<Task> stopQueue)
    {
        _settings = settings;
        _startQueue = startQueue;
        _stopQueue = stopQueue;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(20)
        };
        _timer.Tick += (_, _) => Evaluate();
    }

    public void Start()
    {
        Evaluate();
        _timer.Start();
    }

    /// <summary>Reads the current window and acts once per crossing, never on every tick.</summary>
    private void Evaluate()
    {
        var current = _settings.Current;

        if (!current.SchedulerEnabled)
        {
            _inside = null;
            return;
        }

        if (!TryParse(current.SchedulerStartTime, out var start) ||
            !TryParse(current.SchedulerStopTime, out var stop) ||
            start == stop)
        {
            _inside = null;
            return;
        }

        var now = TimeOnly.FromDateTime(DateTime.Now);

        // A window that crosses midnight (22:00 → 06:00) is "inside" on both sides of it.
        var inside = start < stop
            ? now >= start && now < stop
            : now >= start || now < stop;

        if (_inside == inside) return;
        _inside = inside;

        _ = inside ? StartQueueAsync() : StopQueueAsync();
    }

    private async Task StartQueueAsync()
    {
        try
        {
            await _startQueue().ConfigureAwait(false);
            Report(Localizer.Get("Sched_Started"));
        }
        catch (Exception)
        {
            // The engine refuses when the queue is already running; the clock will retry.
        }
    }

    private async Task StopQueueAsync()
    {
        try
        {
            await _stopQueue().ConfigureAwait(false);
            Report(Localizer.Get("Sched_Stopped"));
        }
        catch (Exception)
        {
        }
    }

    private void Report(string message) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Reported?.Invoke(this, message));

    /// <summary>Used by the scheduler dialog: is the queue currently inside the active window?</summary>
    public static bool IsWithin(string? start, string? stop, DateTime moment)
    {
        if (!TryParse(start, out var from) || !TryParse(stop, out var to) || from == to) return false;

        var now = TimeOnly.FromDateTime(moment);
        return from < to
            ? now >= from && now < to
            : now >= from || now < to;
    }

    public static bool TryParse(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }
}
