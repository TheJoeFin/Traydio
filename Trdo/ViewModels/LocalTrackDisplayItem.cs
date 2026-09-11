namespace Trdo.ViewModels;

/// <summary>One row of the local music details page's track list.</summary>
public sealed class LocalTrackDisplayItem
{
    public required int Index { get; init; }
    public required string Path { get; init; }
    public required string DisplayTitle { get; init; }
}
