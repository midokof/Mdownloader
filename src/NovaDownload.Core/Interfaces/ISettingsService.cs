using NovaDownload.Core.Models;

namespace NovaDownload.Core.Interfaces;

public interface ISettingsService
{
    AppSettings Current { get; }
    Task LoadAsync();
    Task SaveAsync();
}
