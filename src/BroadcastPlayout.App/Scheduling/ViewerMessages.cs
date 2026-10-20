using System.Text;

namespace BroadcastPlayout.Scheduling;

public enum ViewerMessageChannel { Sms, WhatsApp, Telegram, Other }
public enum ViewerMessageState { Pending, Approved, Rejected }

/// <summary>One viewer message as received from a connector. Text is already cleaned by the queue.</summary>
public sealed record ViewerMessage(string Id, ViewerMessageChannel Channel, string Sender, string Text, DateTimeOffset ReceivedAt)
{
    public ViewerMessageState State { get; internal set; } = ViewerMessageState.Pending;
    public int AiredCount { get; internal set; }
}

/// <summary>
/// A connector that delivers viewer messages (WhatsApp, Telegram, SMS gateway...). Connectors run in the
/// playout process of one channel and only raise <see cref="MessageReceived"/>; moderation and airing
/// belong to <see cref="ViewerMessageQueue"/>. The event may fire on any thread.
/// </summary>
public interface IViewerMessageSource : IDisposable
{
    string Name { get; }
    event Action<ViewerMessage>? MessageReceived;
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}

/// <summary>
/// Bounded, thread-safe moderation queue that feeds the bottom-band ticker ("SMS on demand").
/// Memory stays fixed: pending and approved lists have hard caps and the oldest entry is dropped first.
/// Messages are approved or rejected by the operator, or approved automatically when <see cref="AutoApprove"/> is on.
/// </summary>
public sealed class ViewerMessageQueue
{
    public const int MaxTextLength = 160;
    private readonly object _gate = new();
    private readonly LinkedList<ViewerMessage> _pending = new();
    private readonly LinkedList<ViewerMessage> _approved = new();
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);
    private readonly int _pendingCapacity;
    private readonly int _approvedCapacity;
    private LinkedListNode<ViewerMessage>? _cursor;

    public ViewerMessageQueue(int pendingCapacity = 500, int approvedCapacity = 100)
    {
        _pendingCapacity = Math.Max(1, pendingCapacity);
        _approvedCapacity = Math.Max(1, approvedCapacity);
    }

    /// <summary>Approve every clean message without operator review.</summary>
    public bool AutoApprove { get; set; }
    /// <summary>An approved message leaves the rotation after airing this many times (0 = keep until removed).</summary>
    public int MaxAirings { get; set; } = 3;
    /// <summary>Same sender and text within this window is ignored as a duplicate.</summary>
    public TimeSpan DuplicateWindow { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Messages containing any of these words are rejected on arrival (case-insensitive).</summary>
    public IReadOnlyCollection<string> BlockedWords { get; set; } = [];

    /// <summary>Raised after any change. May fire on a connector thread; UI subscribers marshal themselves.</summary>
    public event Action? Changed;

    public int PendingCount { get { lock (_gate) return _pending.Count; } }
    public int ApprovedCount { get { lock (_gate) return _approved.Count; } }

    public IReadOnlyList<ViewerMessage> Pending() { lock (_gate) return [.. _pending]; }
    public IReadOnlyList<ViewerMessage> Approved() { lock (_gate) return [.. _approved]; }

    /// <summary>Hook a connector to this queue. Returns a token that unhooks it.</summary>
    public IDisposable Attach(IViewerMessageSource source)
    {
        source.MessageReceived += OnReceived;
        return new Detach(() => source.MessageReceived -= OnReceived);
    }

    private void OnReceived(ViewerMessage message) => Add(message);

    /// <summary>Cleans and queues a message. Returns false when it was empty, a duplicate or blocked.</summary>
    public bool Add(ViewerMessage message)
    {
        var text = Clean(message.Text);
        if (text.Length == 0) return false;
        var clean = message with { Text = text, Sender = Clean(message.Sender) };
        var blocked = IsBlocked(text);
        lock (_gate)
        {
            PruneRecent(clean.ReceivedAt);
            var dedupeKey = clean.Sender + "\n" + text.ToUpperInvariant();
            if (_recent.ContainsKey(dedupeKey)) return false;
            _recent[dedupeKey] = clean.ReceivedAt;
            if (blocked) { clean.State = ViewerMessageState.Rejected; return false; }
            if (AutoApprove) AddApproved(clean);
            else
            {
                if (_pending.Count >= _pendingCapacity) _pending.RemoveFirst();
                _pending.AddLast(clean);
            }
        }
        Changed?.Invoke();
        return true;
    }

    public bool Approve(string id) => Moderate(id, approve: true);
    public bool Reject(string id) => Moderate(id, approve: false);

    /// <summary>Removes an approved message from the rotation (for example after a complaint).</summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            var node = Find(_approved, id);
            if (node is null) return false;
            RemoveApproved(node);
        }
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        lock (_gate) { _pending.Clear(); _approved.Clear(); _recent.Clear(); _cursor = null; }
        Changed?.Invoke();
    }

    /// <summary>
    /// Takes up to <paramref name="count"/> approved messages in round-robin order for the next ticker pass
    /// and counts them as aired. Messages that reached <see cref="MaxAirings"/> leave the rotation.
    /// </summary>
    public IReadOnlyList<ViewerMessage> TakeForTicker(int count)
    {
        var taken = new List<ViewerMessage>(Math.Max(0, count));
        lock (_gate)
        {
            while (taken.Count < count && _approved.Count > 0)
            {
                var node = _cursor ?? _approved.First!;
                if (taken.Exists(m => ReferenceEquals(m, node.Value))) break;
                _cursor = node.Next;
                node.Value.AiredCount++;
                taken.Add(node.Value);
                if (MaxAirings > 0 && node.Value.AiredCount >= MaxAirings) RemoveApproved(node);
            }
        }
        if (taken.Count > 0) Changed?.Invoke();
        return taken;
    }

    /// <summary>One ticker line from the next messages, e.g. "RAM: Happy birthday  •  ...".</summary>
    public string TickerText(int count = 10, string separator = "  •  ")
    {
        var messages = TakeForTicker(count);
        return string.Join(separator, messages.Select(m => string.IsNullOrEmpty(m.Sender) ? m.Text : m.Sender + ": " + m.Text));
    }

    private bool Moderate(string id, bool approve)
    {
        lock (_gate)
        {
            var node = Find(_pending, id);
            if (node is null) return false;
            _pending.Remove(node);
            if (approve) AddApproved(node.Value);
            else node.Value.State = ViewerMessageState.Rejected;
        }
        Changed?.Invoke();
        return true;
    }

    private void AddApproved(ViewerMessage message)
    {
        message.State = ViewerMessageState.Approved;
        if (_approved.Count >= _approvedCapacity) RemoveApproved(_approved.First!);
        _approved.AddLast(message);
    }

    private void RemoveApproved(LinkedListNode<ViewerMessage> node)
    {
        if (_cursor == node) _cursor = node.Next;
        _approved.Remove(node);
    }

    private void PruneRecent(DateTimeOffset now)
    {
        if (_recent.Count < 64) return;
        foreach (var key in _recent.Where(x => now - x.Value > DuplicateWindow).Select(x => x.Key).ToList()) _recent.Remove(key);
        // Hard cap: a flood of unique messages must not grow the dedupe table without limit.
        if (_recent.Count > _pendingCapacity * 4) _recent.Clear();
    }

    private bool IsBlocked(string text)
    {
        var words = BlockedWords;
        return words.Count > 0 && words.Any(w => !string.IsNullOrWhiteSpace(w) && text.Contains(w.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static LinkedListNode<ViewerMessage>? Find(LinkedList<ViewerMessage> list, string id)
    {
        for (var node = list.First; node is not null; node = node.Next)
            if (node.Value.Id == id) return node;
        return null;
    }

    /// <summary>Single line, no control characters, collapsed spaces, at most <see cref="MaxTextLength"/> characters.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(Math.Min(text.Length, MaxTextLength + 1));
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            if (sb.Length >= MaxTextLength - (char.IsHighSurrogate(c) ? 1 : 0)) break; // never split an emoji
            sb.Append(c);
        }
        return sb.ToString();
    }

    private sealed class Detach(Action action) : IDisposable
    {
        private Action? _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
