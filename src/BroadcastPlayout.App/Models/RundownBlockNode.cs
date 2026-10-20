using System.Collections.ObjectModel;

namespace BroadcastPlayout.Models;

public sealed class RundownBlockNode
{
    public string Name { get; set; } = "RUNDOWN";
    public string Category { get; set; } = "Mixed";
    public int FirstIndex { get; set; }
    public ObservableCollection<PlaylistItem> Items { get; } = [];
    public int Count => Items.Count;
    public TimeSpan Duration => TimeSpan.FromTicks(Items.Sum(x => x.Duration.Ticks));
    public string DurationText => Timecode.Format(Duration);
    public bool IsLoop => Items.Count > 0 && Items.All(x => x.IsBlockLoop);
    public string LoopBadge => IsLoop ? "🔁 LOOP" : "";
    public string Header => $"{Name} · {Count} · {DurationText}{(IsLoop ? " · 🔁 LOOP" : "")}";
}
