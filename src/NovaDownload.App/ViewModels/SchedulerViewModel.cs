using CommunityToolkit.Mvvm.ComponentModel;
using NovaDownload.App.Services;
using NovaDownload.Core.Interfaces;
using NovaDownload.Core.Localization;

namespace NovaDownload.App.ViewModels;

/// <summary>
/// "Planificateur": the queue runs between two HH:mm marks. Saving is what arms it; the shell
/// re-reads the schedule immediately instead of waiting for the next tick.
/// </summary>
public partial class SchedulerViewModel : LocalizedViewModel
{
    private readonly ISettingsService _settings;

    public SchedulerViewModel(ISettingsService settings)
    {
        _settings = settings;

        enabled = settings.Current.SchedulerEnabled;
        startText = settings.Current.SchedulerStartTime;
        stopText = settings.Current.SchedulerStopTime;

        UpdateState();
    }

    [ObservableProperty] private bool enabled;
    [ObservableProperty] private string startText = "02:00";
    [ObservableProperty] private string stopText = "06:00";
    [ObservableProperty] private string? error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>"Actif maintenant" / "Hors de la plage" — reads the current clock.</summary>
    public string StateText => !Enabled
        ? Localizer.Get("Sched_Disabled")
        : QueueScheduler.IsWithin(StartText, StopText, DateTime.Now)
            ? Localizer.Get("Sched_Inside")
            : Localizer.Get("Sched_Outside");

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnEnabledChanged(bool value) => UpdateState();

    partial void OnStartTextChanged(string value) => UpdateState();

    partial void OnStopTextChanged(string value) => UpdateState();

    private void UpdateState() => OnPropertyChanged(nameof(StateText));

    protected override void OnLanguageChanged() => OnPropertyChanged(nameof(StateText));

    /// <summary>Validates both marks and persists them. False keeps the dialog open.</summary>
    public bool Save()
    {
        if (!QueueScheduler.TryParse(StartText, out var start) ||
            !QueueScheduler.TryParse(StopText, out var stop))
        {
            Error = Localizer.Get("Sched_Invalid");
            return false;
        }

        if (start == stop)
        {
            Error = Localizer.Get("Sched_SameMoment");
            return false;
        }

        var current = _settings.Current;
        current.SchedulerEnabled = Enabled;
        current.SchedulerStartTime = StartText.Trim();
        current.SchedulerStopTime = StopText.Trim();
        _ = _settings.SaveAsync();

        Error = null;
        return true;
    }
}
