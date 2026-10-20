namespace BroadcastPlayout.Models;

/// <summary>One rendered block in the operator rundown timeline.</summary>
public sealed class RundownTimelineItem
{
    public int PlaylistIndex { get; init; }
    public int Sequence { get; init; }
    public string Title { get; init; } = string.Empty;
    public string DurationText { get; init; } = string.Empty;
    public string StartText { get; init; } = string.Empty;
    public string Background { get; init; } = "#30203F";
    public string Border { get; init; } = "#674382";
    public double Left { get; init; }
    public double Width { get; init; }
    public bool IsCurrent { get; init; }
}
