// ============================================================
// Kashtrix Entertainment Channel - Core Models
// Music (9x Music / B4U / Sony Mix)
// Movie (Sony Max / Star Gold / Zee Cinema)
// VOD On-Demand
// ============================================================
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BroadcastPlayout.Entertainment;

// ── Music Metadata ──────────────────────────────────────────
public sealed class MusicMetadata : INotifyPropertyChanged
{
    private string _songTitle   = string.Empty;
    private string _artist      = string.Empty;
    private string _album       = string.Empty;
    private string _albumArt    = string.Empty;
    private string _language    = "Hindi";
    private string _genre       = "Bollywood";
    private int    _year        = DateTime.Now.Year;
    private int    _starRating  = 3;
    private bool   _isNew       = false;
    private int    _playCount   = 0;
    private string _requestedBy = string.Empty;

    public Guid   Id           { get; set; } = Guid.NewGuid();
    public string SongTitle    { get => _songTitle;   set => Set(ref _songTitle,   value ?? string.Empty); }
    public string Artist       { get => _artist;      set => Set(ref _artist,      value ?? string.Empty); }
    public string Album        { get => _album;       set => Set(ref _album,       value ?? string.Empty); }
    public string AlbumArtPath { get => _albumArt;    set => Set(ref _albumArt,    value ?? string.Empty); }
    public string Language     { get => _language;    set => Set(ref _language,    value ?? "Hindi"); }
    public string Genre        { get => _genre;       set => Set(ref _genre,       value ?? "Bollywood"); }
    public int    Year         { get => _year;        set => Set(ref _year,        value); }
    public int    StarRating   { get => _starRating;  set => Set(ref _starRating,  Math.Clamp(value, 1, 5)); }
    public bool   IsNew        { get => _isNew;       set => Set(ref _isNew,       value); }
    public int    PlayCount    { get => _playCount;   set => Set(ref _playCount,   value < 0 ? 0 : value); }
    public string RequestedBy  { get => _requestedBy; set => Set(ref _requestedBy, value ?? string.Empty); }

    public string StarDisplay      => new string('*', StarRating).PadRight(5, '-');
    public string NowPlayingLine   => string.IsNullOrWhiteSpace(Artist) ? SongTitle : $"{SongTitle}  |  {Artist}";
    public string SubLine          => string.IsNullOrWhiteSpace(Album)  ? Language  : $"{Album}  |  {Language}  |  {Year}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T f, T v, [CallerMemberName] string? p = null)
    { if (EqualityComparer<T>.Default.Equals(f, v)) return false; f = v; PropertyChanged?.Invoke(this, new(p)); return true; }
}

// ── Movie Metadata ───────────────────────────────────────────
public sealed class MovieMetadata : INotifyPropertyChanged
{
    private string _title         = string.Empty;
    private string _language      = "Hindi";
    private string _genre         = "Action";
    private int    _year          = 2000;
    private int    _starRating    = 3;
    private string _cast          = string.Empty;
    private string _director      = string.Empty;
    private string _synopsis      = string.Empty;
    private string _posterPath    = string.Empty;
    private string _ageRating     = "U";
    private bool   _isSeries      = false;
    private string _seriesName    = string.Empty;
    private int    _episode       = 0;
    private int    _season        = 1;

    public Guid   Id            { get; set; } = Guid.NewGuid();
    public string Title         { get => _title;       set => Set(ref _title,       value ?? string.Empty); }
    public string Language      { get => _language;    set => Set(ref _language,    value ?? "Hindi"); }
    public string Genre         { get => _genre;       set => Set(ref _genre,       value ?? "Action"); }
    public int    Year          { get => _year;        set => Set(ref _year,        value); }
    public int    StarRating    { get => _starRating;  set => Set(ref _starRating,  Math.Clamp(value, 1, 5)); }
    public string Cast          { get => _cast;        set => Set(ref _cast,        value ?? string.Empty); }
    public string Director      { get => _director;    set => Set(ref _director,    value ?? string.Empty); }
    public string Synopsis      { get => _synopsis;    set => Set(ref _synopsis,    value ?? string.Empty); }
    public string PosterPath    { get => _posterPath;  set => Set(ref _posterPath,  value ?? string.Empty); }
    public string AgeRating     { get => _ageRating;   set => Set(ref _ageRating,   value ?? "U"); }
    public bool   IsSeries      { get => _isSeries;    set => Set(ref _isSeries,    value); }
    public string SeriesName    { get => _seriesName;  set => Set(ref _seriesName,  value ?? string.Empty); }
    public int    Episode       { get => _episode;     set => Set(ref _episode,     value); }
    public int    Season        { get => _season;      set => Set(ref _season,      value); }

    public string StarDisplay   => string.Concat(Enumerable.Repeat("*", StarRating)).PadRight(5, '-');
    public string DisplayTitle  => IsSeries && !string.IsNullOrWhiteSpace(SeriesName)
        ? $"{SeriesName}  S{Season:D2}E{Episode:D2}  |  {Title}" : Title;
    public string InfoLine      => $"{Genre}  |  {Language}  |  {Year}  |  [{AgeRating}]  {StarDisplay}";
    public string CastLine      => string.IsNullOrWhiteSpace(Director) ? Cast : $"Dir: {Director}   Cast: {Cast}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T f, T v, [CallerMemberName] string? p = null)
    { if (EqualityComparer<T>.Default.Equals(f, v)) return false; f = v; PropertyChanged?.Invoke(this, new(p)); return true; }
}

// ── Static Genre/Language Lists ──────────────────────────────
public static class MusicGenres
{
    public static readonly string[] All = {
        "Bollywood", "Punjabi", "South Indian", "Devotional",
        "Item Songs", "Retro Classic", "New Releases", "Sad Songs",
        "Romantic", "Dance / Party", "Ghazal / Sufi", "Folk",
        "English Pop", "Hip-Hop / Rap", "Indie"
    };
}

public static class MovieGenres
{
    public static readonly string[] All = {
        "Action", "Romance", "Comedy", "Drama", "Thriller",
        "Horror", "Family", "Sci-Fi", "Historical", "Mythology",
        "Biopic", "Crime", "Animation", "Documentary"
    };
}

public static class MediaLanguages
{
    public static readonly string[] All = {
        "Hindi", "Punjabi", "Tamil", "Telugu", "Malayalam",
        "Kannada", "Bengali", "Marathi", "Gujarati", "English",
        "Bhojpuri", "Haryanvi", "Rajasthani"
    };
}

// ── Time Band (Scheduled Programming) ───────────────────────
public sealed class TimeBand
{
    public string   Name        { get; set; } = string.Empty;
    public TimeSpan StartTime   { get; set; }
    public TimeSpan EndTime     { get; set; }
    public string   Theme       { get; set; } = string.Empty;
    public string   GenreFilter { get; set; } = string.Empty;
    public string   Language    { get; set; } = string.Empty;
    public bool     AutoFill    { get; set; } = true;
    public bool     IsActive    { get; set; } = true;

    public static readonly TimeBand[] DefaultMusicBands = {
        new() { Name="Bhakti Prabhat",    StartTime=TimeSpan.FromHours(5),  EndTime=TimeSpan.FromHours(8),  Theme="Devotional",        GenreFilter="Devotional" },
        new() { Name="Morning Fresh",     StartTime=TimeSpan.FromHours(8),  EndTime=TimeSpan.FromHours(12), Theme="New Releases",       GenreFilter="New Releases" },
        new() { Name="Afternoon Classic", StartTime=TimeSpan.FromHours(12), EndTime=TimeSpan.FromHours(16), Theme="Retro Classics",     GenreFilter="Retro Classic" },
        new() { Name="Evening Hits",      StartTime=TimeSpan.FromHours(16), EndTime=TimeSpan.FromHours(19), Theme="Bollywood Hits",     GenreFilter="Bollywood" },
        new() { Name="PRIME TIME",        StartTime=TimeSpan.FromHours(19), EndTime=TimeSpan.FromHours(23), Theme="Top Charts / New",   GenreFilter="New Releases" },
        new() { Name="Late Night Party",  StartTime=TimeSpan.FromHours(23), EndTime=TimeSpan.FromHours(5),  Theme="Dance / Party",      GenreFilter="Dance / Party" },
    };

    public static readonly TimeBand[] DefaultMovieBands = {
        new() { Name="Morning Movie",     StartTime=TimeSpan.FromHours(7),  EndTime=TimeSpan.FromHours(11), Theme="Family / Classic" },
        new() { Name="Afternoon Movie",   StartTime=TimeSpan.FromHours(11), EndTime=TimeSpan.FromHours(15), Theme="Comedy / Romance" },
        new() { Name="Evening Special",   StartTime=TimeSpan.FromHours(15), EndTime=TimeSpan.FromHours(19), Theme="Action / Thriller" },
        new() { Name="PRIME TIME 8PM",    StartTime=TimeSpan.FromHours(20), EndTime=TimeSpan.FromHours(24), Theme="Blockbuster" },
        new() { Name="Late Night",        StartTime=TimeSpan.FromHours(0),  EndTime=TimeSpan.FromHours(7),  Theme="Classic / Old" },
    };
}

// ── Viewer Request (WhatsApp / SMS) ─────────────────────────
public sealed class ViewerRequest : INotifyPropertyChanged
{
    private string _status = "Pending";

    public Guid     Id          { get; set; } = Guid.NewGuid();
    public DateTime ReceivedAt  { get; set; } = DateTime.Now;
    public string   Channel     { get; set; } = "WhatsApp";  // WhatsApp | SMS | Website
    public string   SenderName  { get; set; } = string.Empty;
    public string   SenderPhone { get; set; } = string.Empty;
    public string   RequestText { get; set; } = string.Empty;
    public string   SongOrMovie { get; set; } = string.Empty;
    public string   Dedication  { get; set; } = string.Empty;
    public string   City        { get; set; } = string.Empty;
    public string   Status      { get => _status; set => Set(ref _status, value); }
    public int      VoteCount   { get; set; } = 1;

    public string TimeDisplay => ReceivedAt.ToString("hh:mm tt");

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T f, T v, [CallerMemberName] string? p = null)
    { if (EqualityComparer<T>.Default.Equals(f, v)) return false; f = v; PropertyChanged?.Invoke(this, new(p)); return true; }
}

// ── Interstitial Item (Promo / Bumper / Channel ID) ──────────
public enum InterstitialKind { ChannelId, Promo, Bumper, Ad, Filler, StationBreak }

public sealed class InterstitialItem
{
    public Guid             Id         { get; set; } = Guid.NewGuid();
    public string           Title      { get; set; } = string.Empty;
    public string           FilePath   { get; set; } = string.Empty;
    public InterstitialKind Kind       { get; set; } = InterstitialKind.Promo;
    public TimeSpan         Duration   { get; set; }
    public int              PlayCount  { get; set; } = 0;
    public int              MaxRepeats { get; set; } = 3;
    public bool             IsActive   { get; set; } = true;
    public DateTime         LastPlayed { get; set; } = DateTime.MinValue;
    public string           KindDisplay => Kind.ToString();
}

// ── Interstitial Insertion Rule ──────────────────────────────
public sealed class InterstitialRule
{
    public Guid             Id           { get; set; } = Guid.NewGuid();
    public string           Name         { get; set; } = string.Empty;
    public InterstitialKind Kind         { get; set; } = InterstitialKind.ChannelId;
    public int              EveryNItems  { get; set; } = 0;
    public int              EveryMinutes { get; set; } = 30;
    public bool             UseItemCount { get; set; } = false;
    public bool             IsActive     { get; set; } = true;
    public List<Guid>       ItemPool     { get; set; } = new();
}

// ── Channel Mode ─────────────────────────────────────────────
public enum ChannelMode { Music, Movie, VOD, Mixed }

// ── Entertainment Channel Config ─────────────────────────────
public sealed class EntertainmentChannelConfig : INotifyPropertyChanged
{
    private string      _channelName     = "My Music Channel";
    private ChannelMode _mode            = ChannelMode.Music;
    private string      _logoPath        = string.Empty;
    private string      _accentColor     = "#E63946";
    private bool        _autoRepeatPrev  = true;
    private int         _sameArtistGap   = 3;
    private bool        _requestsEnabled = true;
    private string      _whatsAppNumber  = string.Empty;
    private bool        _showNowPlaying  = true;
    private bool        _showNextUp      = true;
    private bool        _showClock       = true;

    public string      ChannelName          { get => _channelName;     set => Set(ref _channelName,     value ?? "My Channel"); }
    public ChannelMode Mode                 { get => _mode;            set => Set(ref _mode,            value); }
    public string      LogoPath             { get => _logoPath;        set => Set(ref _logoPath,        value ?? string.Empty); }
    public string      AccentColor          { get => _accentColor;     set => Set(ref _accentColor,     value ?? "#E63946"); }
    public bool        AutoRepeatPrevention { get => _autoRepeatPrev;  set => Set(ref _autoRepeatPrev,  value); }
    public int         SameArtistGapCount   { get => _sameArtistGap;   set => Set(ref _sameArtistGap,   value < 1 ? 1 : value); }
    public bool        RequestsEnabled      { get => _requestsEnabled; set => Set(ref _requestsEnabled, value); }
    public string      WhatsAppNumber       { get => _whatsAppNumber;  set => Set(ref _whatsAppNumber,  value ?? string.Empty); }
    public bool        ShowNowPlayingCG     { get => _showNowPlaying;  set => Set(ref _showNowPlaying,  value); }
    public bool        ShowNextUpCG         { get => _showNextUp;      set => Set(ref _showNextUp,      value); }
    public bool        ShowClock            { get => _showClock;       set => Set(ref _showClock,       value); }

    public ObservableCollection<TimeBand>         TimeBands     { get; set; } = new();
    public ObservableCollection<InterstitialItem>  Interstitials { get; set; } = new();
    public ObservableCollection<InterstitialRule>  Rules         { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T f, T v, [CallerMemberName] string? p = null)
    { if (EqualityComparer<T>.Default.Equals(f, v)) return false; f = v; PropertyChanged?.Invoke(this, new(p)); return true; }
}
