namespace BroadcastPlayout.Models;

public sealed class ChannelStatusSnapshot
{
    public string ChannelId { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public string State { get; set; } = "Ready";
    public string ProgramTitle { get; set; } = "";
    public string NextTitle { get; set; } = "";
    public string Format { get; set; } = "";
    public double ElapsedSeconds { get; set; }
    public double AudioLeft { get; set; }
    public double AudioRight { get; set; }
    public string ProgramPreviewJpegBase64 { get; set; } = "";
    public string PreviewJpegBase64 { get; set; } = "";
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CgRemoteCommand
{
    /// <summary>Unique command identity used to ignore duplicate delivery across local/network transports.</summary>
    public string CommandId { get; set; } = System.Guid.NewGuid().ToString("N");
    public string Action { get; set; } = "PLAY"; // PLAY, UPDATE, STOP
    /// <summary>Independent CG bus. PREVIEW never changes the on-air PROGRAM bus.</summary>
    public string Bus { get; set; } = "PROGRAM"; // PREVIEW, PROGRAM
    /// <summary>External graphics layer. -1 means legacy whole-stack behavior.</summary>
    public int Layer { get; set; } = -1;
    /// <summary>Optional controller TAKE transition. None preserves the existing additive PLAY behavior.</summary>
    public string Transition { get; set; } = "None"; // None, Fade, Wipe Left, Wipe Right, Zoom
    public double TransitionSeconds { get; set; } = 0.45;
    public CgProject? Project { get; set; }
}

public sealed class ChannelDefinition : System.ComponentModel.INotifyPropertyChanged
{
    private string _channelId = $"KTX-CH-{Random.Shared.Next(1, 999):000}";
    private string _channelName = "New Channel";
    private string _format = "1080p50";
    private string _host = "127.0.0.1";
    private string _notes = string.Empty;
    private bool _enabled = true;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string ChannelId { get => _channelId; set => Set(ref _channelId, (value ?? string.Empty).Trim().ToUpperInvariant()); }
    public string ChannelName { get => _channelName; set => Set(ref _channelName, string.IsNullOrWhiteSpace(value) ? "New Channel" : value.Trim()); }
    public string Format { get => _format; set => Set(ref _format, string.IsNullOrWhiteSpace(value) ? "1080p50" : value.Trim()); }
    public string Host { get => _host; set => Set(ref _host, string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim()); }
    public string Notes { get => _notes; set => Set(ref _notes, value ?? string.Empty); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
