using BroadcastPlayout.Models;

namespace BroadcastPlayout.Engine;

/// <summary>
/// Supplies the items that follow a playlist (schedule automation). The engine calls <see cref="Peek"/>
/// while the current item plays, to preroll the next one, and <see cref="Take"/> at the boundary.
/// Take must return the instance Peek returned unless the schedule changed in between.
/// </summary>
public interface IPlayoutSequenceProvider
{
    PlaylistItem? Peek();
    PlaylistItem? Take();
}
