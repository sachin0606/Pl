namespace BroadcastPlayout.Models;

/// <summary>
/// Marks a playlist or schedule item as a viewer request ("on demand"). A copy of the viewer details
/// travels with the item, so the Now/Next graphics can show "Requested by ..." without a lookup.
/// </summary>
public sealed record RequestTag(Guid RequestId, string ViewerName, string City, string Message, string Source);
