using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Trdo.ViewModels;

/// <summary>One row of the local music details page's track list.</summary>
public sealed class LocalTrackDisplayItem : INotifyPropertyChanged
{
    private bool _isPlaying;

    public required int Index { get; init; }
    public required string Path { get; init; }
    public required string DisplayTitle { get; init; }

    /// <summary>True when this is the track currently loaded in the player.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
