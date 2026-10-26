using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NovaDownload.Core.Models;

namespace NovaDownload.App.ViewModels;

/// <summary>
/// One file inside a multi-file torrent. The user toggles <see cref="IsSelected"/> to choose
/// what is downloaded. The view model that owns this collection is responsible for pushing
/// the selection to the engine when it changes.
/// </summary>
public sealed partial class TorrentFileViewModel : ObservableObject
{
    private readonly TorrentFileInfo _model;

    public TorrentFileViewModel(TorrentFileInfo model)
    {
        _model = model;
        IsSelected = model.IsSelected;
    }

    public TorrentFileInfo Model => _model;

    public string Path => _model.Path;
    public long Length => _model.Length;
    public string Name => _model.Name;

    /// <summary>Path as shown under the name; kept apart from <see cref="Path"/> because XAML
    /// reads a bare <c>Path</c> binding as the binding's own property.</summary>
    public string RelativePath => _model.Path;

    public string SizeText => NovaDownload.Core.Common.Formatters.Bytes(_model.Length);

    [ObservableProperty]
    private bool isSelected;

    partial void OnIsSelectedChanged(bool value) => _model.IsSelected = value;
}