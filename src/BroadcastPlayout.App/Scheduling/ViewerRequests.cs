using System.Text.Json.Serialization;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Scheduling;

public enum ViewerRequestStatus { Waiting, Scheduled, Played, Rejected }

/// <summary>
/// A viewer asking for a song, movie or programme ("on demand"). Entered by the operator today; the
/// WhatsApp / Telegram / SMS connectors (IViewerMessageSource) will create them later. When scheduled,
/// a <see cref="RequestTag"/> copy travels with the playlist or schedule item so the graphics can show
/// "Requested by ...", and the status moves to Played when it goes on air.
/// </summary>
public sealed class ViewerRequest : ObservableObject
{
    private ViewerRequestStatus _status = ViewerRequestStatus.Waiting;
    private DateTime? _scheduledFor;
    private DateTime? _playedAt;
    private string _mediaPath = string.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string ViewerName { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    /// <summary>Manual, Phone call, SMS, WhatsApp, Telegram.</summary>
    public string Source { get; set; } = "Manual";
    /// <summary>Dedication or message to show with the request.</summary>
    public string Message { get; set; } = string.Empty;
    public string RequestedTitle { get; set; } = string.Empty;
    /// <summary>Song, Movie or Programme. Decides the category (and so the Now/Next profile) of the airing.</summary>
    public string Kind { get; set; } = "Song";
    public string MediaPath { get => _mediaPath; set { if (Set(ref _mediaPath, value ?? string.Empty)) Raise(nameof(MediaText)); } }
    public ViewerRequestStatus Status { get => _status; set { if (Set(ref _status, value)) Raise(nameof(StatusText)); } }
    public DateTime? ScheduledFor { get => _scheduledFor; set { if (Set(ref _scheduledFor, value)) Raise(nameof(StatusText)); } }
    public DateTime? PlayedAt { get => _playedAt; set { if (Set(ref _playedAt, value)) Raise(nameof(StatusText)); } }

    [JsonIgnore] public string ViewerText => string.IsNullOrWhiteSpace(City) ? ViewerName : $"{ViewerName}, {City}";
    [JsonIgnore] public string MediaText => string.IsNullOrWhiteSpace(MediaPath) ? "— not matched —" : Path.GetFileName(MediaPath);
    [JsonIgnore] public string CreatedText => CreatedAt.ToString(CreatedAt.Date == DateTime.Today ? "HH:mm" : "dd MMM HH:mm");
    [JsonIgnore] public string StatusText => Status switch
    {
        ViewerRequestStatus.Scheduled when ScheduledFor is { } at => "SCHEDULED " + at.ToString(at.Date == DateTime.Today ? "HH:mm" : "dd MMM HH:mm"),
        ViewerRequestStatus.Scheduled => "SCHEDULED · NEXT",
        ViewerRequestStatus.Played when PlayedAt is { } at => "PLAYED " + at.ToString("HH:mm"),
        _ => Status.ToString().ToUpperInvariant()
    };

    public RequestTag ToTag() => new(Id, ViewerName.Trim(), City.Trim(), Message.Trim(), Source);

    /// <summary>Playlist category for the airing, so the matching Now/Next profile applies.</summary>
    public string Category => Kind switch
    {
        "Movie" => "Movie",
        "Programme" => "Program",
        _ => "Music"
    };
}

/// <summary>
/// Per-channel request list in the shared database (key per channel, like the schedule), bounded:
/// at most <see cref="MaxRequests"/> are kept, finished ones (played / rejected) are dropped first.
/// </summary>
public static class ViewerRequestStore
{
    public const int MaxRequests = 500;
    private static string Key(string? channelId) => "viewer-requests.v1:" + ChannelScheduleStore.Normalize(channelId);

    public static List<ViewerRequest> Load(string? channelId)
    {
        try { return new BroadcastDatabase().LoadState<List<ViewerRequest>?>(Key(channelId), null) ?? []; }
        catch { return []; }
    }

    public static void Save(string? channelId, IEnumerable<ViewerRequest> requests)
        => new BroadcastDatabase().SaveState(Key(channelId), Trim(requests));

    public static List<ViewerRequest> Trim(IEnumerable<ViewerRequest> requests)
    {
        var list = requests.ToList();
        if (list.Count <= MaxRequests) return list;
        var finished = list.Where(r => r.Status is ViewerRequestStatus.Played or ViewerRequestStatus.Rejected)
            .OrderBy(r => r.CreatedAt).Take(list.Count - MaxRequests).ToHashSet();
        list.RemoveAll(finished.Contains);
        // Still too many open requests: the oldest go.
        return list.OrderByDescending(r => r.CreatedAt).Take(MaxRequests).OrderBy(r => r.CreatedAt).ToList();
    }

    /// <summary>
    /// A one-off schedule entry that airs the request at <paramref name="at"/>. Request entries from
    /// past days are removed at the same time, so the schedule never grows with old requests.
    /// </summary>
    public static ChannelScheduleItem AddToSchedule(ChannelSchedule schedule, ViewerRequest request, DateTime at, double durationSeconds)
    {
        schedule.Items.RemoveAll(x => x.Request is not null && x.EndDate is { } end && end < DateTime.Today);
        var item = new ChannelScheduleItem
        {
            Kind = request.Kind == "Movie" ? ScheduleKind.Movie : ScheduleKind.Programme,
            Name = string.IsNullOrWhiteSpace(request.RequestedTitle) ? Path.GetFileNameWithoutExtension(request.MediaPath) : request.RequestedTitle.Trim(),
            Days = ScheduleDays.All,
            StartDate = at.Date,
            EndDate = at.Date,
            StartTime = at.TimeOfDay,
            Sources = [request.MediaPath],
            PlannedDurationSeconds = Math.Max(1, durationSeconds),
            Category = request.Category,
            Request = request.ToTag()
        };
        schedule.Items.Add(item);
        return item;
    }
}
