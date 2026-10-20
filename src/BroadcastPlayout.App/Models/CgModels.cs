using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace BroadcastPlayout.Models;

public sealed class CgProject : INotifyPropertyChanged
{
    private string _name = "Untitled Graphic";
    private int _width = 1920;
    private int _height = 1080;
    private double _frameRate = 25.0;
    private double _durationSeconds = 10;
    private double _timelinePauseSeconds = -1;
    private bool _onAir;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool IsImported { get; set; }
    public string Category { get; set; } = string.Empty;
    public int Width { get => _width; set => Set(ref _width, Math.Clamp(value, 16, 16384)); }
    public int Height { get => _height; set => Set(ref _height, Math.Clamp(value, 16, 16384)); }
    public double FrameRate { get => _frameRate; set => Set(ref _frameRate, double.IsFinite(value) ? Math.Clamp(value, 1.0, 240.0) : 25.0); }
    public double DurationSeconds { get => _durationSeconds; set => Set(ref _durationSeconds, double.IsFinite(value) ? Math.Clamp(value, 0.1, 7200.0) : 10); }
    public double TimelinePauseSeconds { get => _timelinePauseSeconds; set => Set(ref _timelinePauseSeconds, double.IsFinite(value) ? value : -1); }
    public bool OnAir { get => _onAir; set => Set(ref _onAir, value); }
    public bool Loop { get; set; }
    public bool IsStopping { get; set; }
    public double StopRequestedTimelineSeconds { get; set; } = -1;
    /// <summary>Optional external mixer/controller layer. -1 means no routed layer identity.</summary>
    public int ExternalLayer { get; set; } = -1;
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public List<CgLayer> Layers { get; set; } = [];
    public List<CgHtmlSource> HtmlSources { get; set; } = [];
    public List<CgGroup> Groups { get; set; } = [];
    public List<CgDataSource> DataSources { get; set; } = [];
    public List<CgTimelineEvent> TimelineEvents { get; set; } = [];

    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class CgLayer : INotifyPropertyChanged
{
    private string _name = "Layer";
    private string _type = "Text";
    private string _text = "KASHTRIX";
    private string _source = string.Empty;
    private string _fill = "#FFFFFFFF";
    private string _background = "#00000000";
    private string _animationIn = "None";
    private string _animationOut = "None";
    private bool _visible = true;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Type { get => _type; set => Set(ref _type, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public string Source { get => _source; set => Set(ref _source, value); }
    // Per-media compositing mode. Non-media layers keep Normal; image/video/sequence layers
    // can use broadcast-style Photoshop/After Effects blend equations against layers below.
    public string BlendMode { get; set; } = "Normal"; // Normal + Photoshop/AE-style darken/lighten/contrast/difference/component blend families

    // Per-element fixed canvas mask. Motion/keyframes are evaluated normally, then the fully
    // rendered element is clipped in project/canvas coordinates. This means slide/push/zoom
    // animations can begin outside the mask while only pixels inside the mask become visible.
    public bool MaskEnabled { get; set; }
    public string MaskShape { get; set; } = "Rectangle"; // Rectangle, Rounded Rectangle, Ellipse
    public double MaskX { get; set; }
    public double MaskY { get; set; }
    public double MaskWidth { get; set; }
    public double MaskHeight { get; set; }
    public double MaskCornerRadius { get; set; }
    public double MaskFeather { get; set; }
    public bool MaskInvert { get; set; }
    // Semicolon-separated normalized points (x,y;x,y;...) authored by the pen tool.
    // ShapePathData is local to the layer bounds; MaskPathData is local to MaskX/Y/Width/Height.
    public string ShapePathData { get; set; } = string.Empty;
    public bool ShapePathClosed { get; set; } = true;
    public string MaskPathData { get; set; } = string.Empty;

    // When an Adobe After Effects project is imported, Kashtrix keeps the source project and
    // composition metadata while playing the rendered alpha image sequence on the native timeline.
    public string ExternalProjectSource { get; set; } = string.Empty;
    public string ExternalComposition { get; set; } = string.Empty;
    public string ExternalProjectKind { get; set; } = string.Empty; // AfterEffects

    // After Effects-style native precomposition. A PRECOMP layer behaves as one layer in the
    // parent composition while retaining a complete editable child composition. Double-clicking
    // the layer in CG Editor opens this nested project; rendering recurses through the child.
    public CgProject? Precomposition { get; set; }

    // Native video/live-source layer binding. The CG compositor can draw a file, URL/stream,
    // NDI sender, DirectShow/card/webcam, desktop capture, or a custom FFmpeg input inside
    // the authored layer rectangle exactly like any other CG element.
    public string VideoSourceKind { get; set; } = "File"; // File, URL, NDI, DirectShow, Screen, Custom
    public string VideoInputFormat { get; set; } = string.Empty;
    public string VideoInputOptions { get; set; } = string.Empty;
    public string VideoDevice { get; set; } = string.Empty;
    public string AudioDevice { get; set; } = string.Empty;
    public string AlternateAudioUrl { get; set; } = string.Empty;
    public bool VideoIsLiveSource { get; set; }
    public int VideoCaptureWidth { get; set; } = 1920;
    public int VideoCaptureHeight { get; set; } = 1080;
    public double VideoSourceFrameRate { get; set; } = 25.0;

    private double _x = 120;
    private double _y = 780;
    private double _width = 900;
    private double _height = 120;
    private double _fontSize = 52;
    private string _fontFamily = "Segoe UI";
    private double _startSeconds;
    private double _endSeconds = 10;
    private double _animationInSeconds = 0.0;
    private double _animationOutSeconds = 0.0;
    private double _speed = 120;
    private double _z;
    private double _scaleX = 1;
    private double _scaleY = 1;
    private double _scaleZ = 1;
    private double _rotationX;
    private double _rotationY;
    private double _rotation;
    private double _anchorX = 0.5;
    private double _anchorY = 0.5;
    private double _anchorZ;
    private double _skewX;
    private double _skewY;
    private double _perspective = 1200;
    private double _opacity = 1;
    private string _role = "Graphic";
    private string _shapeKind = "Rectangle";
    private double _cornerRadius;
    private double _cornerRadiusTopLeft;
    private double _cornerRadiusTopRight;
    private double _cornerRadiusBottomRight;
    private double _cornerRadiusBottomLeft;
    private bool _cornerRadiusAsPercent;
    private double _cornerRadiusTopLeftPercent;
    private double _cornerRadiusTopRightPercent;
    private double _cornerRadiusBottomRightPercent;
    private double _cornerRadiusBottomLeftPercent;
    private string _borderColor = "#00000000";
    private double _borderWidth;
    private bool _bold = true;
    private bool _italic;
    private bool _underline;
    private string _textAnimationPreset = "None";
    private string _textAnimationUnit = "Whole";
    private double _textAnimationDurationSeconds = 0.8;
    private double _textAnimationStaggerSeconds = 0.06;
    private double _textAnimationDelaySeconds = 0.0;
    private double _blurRadius;
    private string _timerMode = "None"; // None, CountDown, CountUp, CommercialBackIn
    private double _timerStartSeconds;
    private string _timerFormat = "mm:ss";
    private string _timerPrefix = string.Empty;
    private string _timerSuffix = string.Empty;

    private string _aspectRatio = "Free"; // Free, 16:9, 4:3
    public string AspectRatio { get => _aspectRatio; set => Set(ref _aspectRatio, value ?? "Free"); }

    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }
    public double Width { get => _width; set => Set(ref _width, value); }
    public double Height { get => _height; set => Set(ref _height, value); }
    public double FontSize { get => _fontSize; set => Set(ref _fontSize, value); }
    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value); }
    public string Fill { get => _fill; set => Set(ref _fill, value); }
    public string Background { get => _background; set => Set(ref _background, value); }
    public double StartSeconds { get => _startSeconds; set { if (Set(ref _startSeconds, value)) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationSeconds))); } } }
    public double EndSeconds { get => _endSeconds; set { if (Set(ref _endSeconds, value)) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationSeconds))); } } }
    public double DurationSeconds
    {
        get => Math.Max(0.1, EndSeconds - StartSeconds);
        set
        {
            var dur = Math.Max(0.1, value);
            EndSeconds = StartSeconds + dur;
        }
    }
    public string AnimationIn { get => _animationIn; set => Set(ref _animationIn, value); }
    public string AnimationOut { get => _animationOut; set => Set(ref _animationOut, value); }
    public double AnimationInSeconds { get => _animationInSeconds; set => Set(ref _animationInSeconds, value); }
    public double AnimationOutSeconds { get => _animationOutSeconds; set => Set(ref _animationOutSeconds, value); }
    public bool Visible { get => _visible; set => Set(ref _visible, value); }
    public double Speed { get => _speed; set => Set(ref _speed, value); }
    public double TickerSpeed { get => Speed; set => Speed = value; }
    // Native 2.5D transform. Rotation is the Z-axis rotation retained for backward compatibility.
    public double Z { get => _z; set => Set(ref _z, value); }
    public double ScaleX { get => _scaleX; set => Set(ref _scaleX, value); }
    public double ScaleY { get => _scaleY; set => Set(ref _scaleY, value); }
    public double ScaleZ { get => _scaleZ; set => Set(ref _scaleZ, value); }
    public double RotationX { get => _rotationX; set => Set(ref _rotationX, value); }
    public double RotationY { get => _rotationY; set => Set(ref _rotationY, value); }
    public double Rotation { get => _rotation; set => Set(ref _rotation, value); }
    public double AnchorX { get => _anchorX; set => Set(ref _anchorX, value); }
    public double AnchorY { get => _anchorY; set => Set(ref _anchorY, value); }
    public double AnchorZ { get => _anchorZ; set => Set(ref _anchorZ, value); }
    public double Perspective { get => _perspective; set => Set(ref _perspective, value); }
    public double SkewX { get => _skewX; set => Set(ref _skewX, value); }
    public double SkewY { get => _skewY; set => Set(ref _skewY, value); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, value); }

    // Grouping + keyframe animation. A layer can inherit an animated group transform and
    // can also own its own keyframes. Keyframes intentionally use generic motion values so
    // projects remain portable and can be rendered without a browser/third-party runtime.
    public Guid GroupId { get; set; } = Guid.Empty;
    public List<CgKeyframe> Keyframes { get; set; } = [];
    public string DefaultEase { get; set; } = "power2.out";
    public double KeyframeDelaySeconds { get; set; }
    public int KeyframeRepeat { get; set; }
    public bool KeyframeYoyo { get; set; }

    // Data binding. DataSourceId points to a project data source (URL/local JSON, XML/RSS,
    // XLSX/CSV, TXT). DataField accepts a dotted path such as headline.title.
    public Guid DataSourceId { get; set; } = Guid.Empty;
    public string DataField { get; set; } = string.Empty;
    public string DataFormat { get; set; } = "{0}";
    public string CalendarSystem { get; set; } = "AD"; // AD (Gregorian), BS (Bikram Sambat)
    public string TimeZoneId { get; set; } = string.Empty; // clock/date layers; empty = local, else Windows/IANA ID or UTC+hh:mm
    public double DataItemDurationSeconds { get; set; } = 4.0;
    public int DataItemOffset { get; set; }

    // Image-sequence controls. SequenceAdvanceDataItem resets the PNG sequence for each
    // data item, allowing e.g. frames 56..167 to play once per JSON row before advancing.
    public double SequenceFps { get; set; } = 25.0;
    public int SequenceStartFrame { get; set; }
    public int SequenceLoopStartFrame { get; set; } = -1;
    public int SequenceEndFrame { get; set; } = -1;
    public bool SequenceLoop { get; set; } = true;
    public bool SequenceHoldLastFrame { get; set; } = true;
    public bool SequenceAdvanceDataItem { get; set; }
    private int _sequenceOverlayIntervalItems = 1;
    public int SequenceOverlayIntervalItems { get => _sequenceOverlayIntervalItems; set => Set(ref _sequenceOverlayIntervalItems, Math.Max(1, value)); }
    private double _sequenceLoopStartSeconds = -1;
    public double SequenceLoopStartSeconds { get => _sequenceLoopStartSeconds; set => Set(ref _sequenceLoopStartSeconds, value); }
    private double _sequenceLoopEndSeconds = -1;
    public double SequenceLoopEndSeconds { get => _sequenceLoopEndSeconds; set => Set(ref _sequenceLoopEndSeconds, value); }

    // Dynamic Timer, Countdown (Decrement), CountUp (Increment), and Commercial Back In
    public string TimerMode { get => _timerMode; set => Set(ref _timerMode, value); }
    public double TimerStartSeconds { get => _timerStartSeconds; set => Set(ref _timerStartSeconds, value); }
    public string TimerFormat { get => _timerFormat; set => Set(ref _timerFormat, value); }
    public string TimerPrefix { get => _timerPrefix; set => Set(ref _timerPrefix, value); }
    public string TimerSuffix { get => _timerSuffix; set => Set(ref _timerSuffix, value); }
    public bool IsTimerLayer => Type.Equals("Timer", StringComparison.OrdinalIgnoreCase) ||
                                Type.Equals("Countdown", StringComparison.OrdinalIgnoreCase) ||
                                Type.Equals("BackIn", StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrWhiteSpace(TimerMode) && !TimerMode.Equals("None", StringComparison.OrdinalIgnoreCase));

    // Rich broadcast-CG styling / layout properties. These are intentionally generic so
    // projects remain portable and do not depend on a proprietary third-party CG format.
    public string Role { get => _role; set => Set(ref _role, value); } // Graphic, Logo, Shape, Data, Bug
    public string ShapeKind { get => _shapeKind; set => Set(ref _shapeKind, value); } // Rectangle, Ellipse, Line, L-Shape, U-Shape
    // Uniform radius is kept for backward compatibility. When any per-corner value is
    // non-zero the editor/compositor uses the four independent values instead.
    public double CornerRadius { get => _cornerRadius; set => Set(ref _cornerRadius, value); }
    public double CornerRadiusTopLeft { get => _cornerRadiusTopLeft; set => Set(ref _cornerRadiusTopLeft, value); }
    public double CornerRadiusTopRight { get => _cornerRadiusTopRight; set => Set(ref _cornerRadiusTopRight, value); }
    public double CornerRadiusBottomRight { get => _cornerRadiusBottomRight; set => Set(ref _cornerRadiusBottomRight, value); }
    public double CornerRadiusBottomLeft { get => _cornerRadiusBottomLeft; set => Set(ref _cornerRadiusBottomLeft, value); }
    // Optional percentage radii (0..50% of the shorter side), independently per corner.
    // Pixel radii remain supported for older projects when this switch is false.
    public bool CornerRadiusAsPercent { get => _cornerRadiusAsPercent; set => Set(ref _cornerRadiusAsPercent, value); }
    public double CornerRadiusTopLeftPercent { get => _cornerRadiusTopLeftPercent; set => Set(ref _cornerRadiusTopLeftPercent, value); }
    public double CornerRadiusTopRightPercent { get => _cornerRadiusTopRightPercent; set => Set(ref _cornerRadiusTopRightPercent, value); }
    public double CornerRadiusBottomRightPercent { get => _cornerRadiusBottomRightPercent; set => Set(ref _cornerRadiusBottomRightPercent, value); }
    public double CornerRadiusBottomLeftPercent { get => _cornerRadiusBottomLeftPercent; set => Set(ref _cornerRadiusBottomLeftPercent, value); }
    public string BorderColor { get => _borderColor; set => Set(ref _borderColor, value); }
    public double BorderWidth { get => _borderWidth; set => Set(ref _borderWidth, value); }
    private string _horizontalTextAlignment = "Left";
    public string HorizontalTextAlignment { get => _horizontalTextAlignment; set => Set(ref _horizontalTextAlignment, value ?? "Left"); } // Left, Center, Right
    private string _verticalTextAlignment = "Center";
    public string VerticalTextAlignment { get => _verticalTextAlignment; set => Set(ref _verticalTextAlignment, value ?? "Center"); } // Top, Center, Bottom
    public bool Bold { get => _bold; set => Set(ref _bold, value); }
    public bool Italic { get => _italic; set => Set(ref _italic, value); }
    public bool Underline { get => _underline; set => Set(ref _underline, value); }
    private bool _autoConvertToPreeti;
    public bool AutoConvertToPreeti { get => _autoConvertToPreeti; set => Set(ref _autoConvertToPreeti, value); }
    public string OutlineColor { get; set; } = "#FF000000";
    public double OutlineWidth { get; set; }
    private bool _shadowEnabled;
    public bool ShadowEnabled { get => _shadowEnabled; set => Set(ref _shadowEnabled, value); }
    public string ShadowColor { get; set; } = "#00000000";
    public double ShadowOffsetX { get; set; }
    public double ShadowOffsetY { get; set; }
    public double ShadowBlur { get; set; }
    public string GlowColor { get; set; } = "#80FFFFFF";
    public double GlowRadius { get; set; }
    public double MotionBlurX { get; set; }
    public double MotionBlurY { get; set; }
    public int MotionBlurSamples { get; set; }
    public double LetterSpacing { get; set; }
    public double LineSpacing { get; set; } = 1;
    public bool UseGradient { get; set; }
    public string GradientColor2 { get; set; } = "#FF303840";
    public double GradientAngle { get; set; } = 0;
    // Unlimited ordered gradient stops. Empty keeps backward-compatible primary/GradientColor2 behavior.
    public ObservableCollection<CgGradientStop> GradientStops { get; set; } = [];
    public bool GlossEnabled { get; set; }
    public double GlossOpacity { get; set; } = 0.22;
    public string TextAnimationPreset { get => _textAnimationPreset; set => Set(ref _textAnimationPreset, value); }
    public string TextAnimationUnit { get => _textAnimationUnit; set => Set(ref _textAnimationUnit, value); }
    public double TextAnimationDurationSeconds { get => _textAnimationDurationSeconds; set => Set(ref _textAnimationDurationSeconds, value); }
    public double TextAnimationStaggerSeconds { get => _textAnimationStaggerSeconds; set => Set(ref _textAnimationStaggerSeconds, value); }
    public double TextAnimationDelaySeconds { get => _textAnimationDelaySeconds; set => Set(ref _textAnimationDelaySeconds, value); }
    public double BlurRadius { get => _blurRadius; set => Set(ref _blurRadius, Math.Max(0, value)); }
    private bool _useBackground;
    public bool UseBackground { get => _useBackground; set => Set(ref _useBackground, value); }
    private string _animationGlowColor = "#80FFFF00";
    public string AnimationGlowColor { get => _animationGlowColor; set => Set(ref _animationGlowColor, value); }
    private double _animationGlowRadius = 0.0;
    public double AnimationGlowRadius { get => _animationGlowRadius; set => Set(ref _animationGlowRadius, Math.Max(0, value)); }
    private double _animationBlurRadius = 0.0;
    public double AnimationBlurRadius { get => _animationBlurRadius; set => Set(ref _animationBlurRadius, Math.Max(0, value)); }

    // Crawl / roll behavior. Push mode gives a broadcast-style item-to-item transition;
    // continuous mode remains the traditional ticker crawl.
    public string TickerMode { get; set; } = "Continuous"; // Continuous, Push
    public string TickerDirection { get; set; } = "Left"; // Left, Right, Up, Down
    public double TickerGap { get; set; } = 80;
    public bool TickerRepeat { get; set; } = true;
    private string _tickerSeparator = " • ";
    public string TickerSeparator { get => _tickerSeparator; set => Set(ref _tickerSeparator, value); }
    private string _tickerSeparatorLogo = string.Empty;
    public string TickerSeparatorLogo { get => _tickerSeparatorLogo; set => Set(ref _tickerSeparatorLogo, value); }
    private bool _tickerCategoriesEnabled;
    public bool TickerCategoriesEnabled { get => _tickerCategoriesEnabled; set => Set(ref _tickerCategoriesEnabled, value); }
    private string _tickerCategoryBadgeBackground = "#D32F2F";
    public string TickerCategoryBadgeBackground { get => _tickerCategoryBadgeBackground; set => Set(ref _tickerCategoryBadgeBackground, value); }

    private double _textAnimationScaleStart = 2.5;
    public double TextAnimationScaleStart { get => _textAnimationScaleStart; set => Set(ref _textAnimationScaleStart, value); }
    private double _textAnimationScaleEnd = 1.0;
    public double TextAnimationScaleEnd { get => _textAnimationScaleEnd; set => Set(ref _textAnimationScaleEnd, value); }

    private bool _timerAutoStop;
    public bool TimerAutoStop { get => _timerAutoStop; set => Set(ref _timerAutoStop, value); }

    // Shape-driven program squeeze. When enabled, the compositor scales the underlying
    // program picture into the target rectangle while the shape is on-air, then smoothly
    // restores it during the OUT animation.
    public bool SqueezeProgram { get; set; }
    // Both uses SqueezeX directly. Left means a left-side graphic reserves space and the
    // program picture keeps its right edge fixed. Right mirrors that behavior.
    public string SqueezeHorizontalMode { get; set; } = "Both"; // Both, Left, Right
    public double SqueezeX { get; set; } = 120;
    public double SqueezeY { get; set; } = 60;
    public double SqueezeWidth { get; set; } = 1500;
    public double SqueezeHeight { get; set; } = 840;
    public double SqueezeInSeconds { get; set; } = 0.6;
    public double SqueezeOutSeconds { get; set; } = 0.6;

    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class CgTimelineEvent : INotifyPropertyChanged
{
    private double _timeSeconds = 1.0;
    private string _type = "Pause"; // Pause, Hold, Resume, Trigger
    private string _label = "PAUSE";
    private bool _enabled = true;
    private string _resumeTrigger = "crawlComplete"; // crawlComplete, pushSequenceComplete, tickerComplete, manual

    public Guid Id { get; set; } = Guid.NewGuid();
    public double TimeSeconds { get => _timeSeconds; set => Set(ref _timeSeconds, Math.Max(0, value)); }
    public string Type { get => _type; set => Set(ref _type, value ?? "Pause"); }
    public string Label { get => _label; set => Set(ref _label, value ?? string.Empty); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string ResumeTrigger { get => _resumeTrigger; set => Set(ref _resumeTrigger, value ?? string.Empty); }

    public override string ToString() => $"{Label} @ {TimeSeconds:0.00}s ({ResumeTrigger})";

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class CgGradientStop : INotifyPropertyChanged
{
    private string _color = "#FFFFFFFF";
    private double _offset;
    public string Color { get => _color; set => Set(ref _color, string.IsNullOrWhiteSpace(value) ? "#FFFFFFFF" : value); }
    public double Offset { get => _offset; set => Set(ref _offset, double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}


public sealed class CgKeyframe : INotifyPropertyChanged
{
    private double _timeSeconds;
    private string _ease = "power2.out";
    private bool _hold;

    public Guid Id { get; set; } = Guid.NewGuid();
    public double TimeSeconds { get => _timeSeconds; set => Set(ref _timeSeconds, double.IsFinite(value) ? Math.Max(0, value) : 0); }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double RotationX { get; set; }
    public double RotationY { get; set; }
    public double Rotation { get; set; }
    public double Opacity { get; set; } = 1;
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;
    public double ScaleZ { get; set; } = 1;
    public double AnchorX { get; set; } = 0.5;
    public double AnchorY { get; set; } = 0.5;
    public double AnchorZ { get; set; }
    public double SkewX { get; set; }
    public double SkewY { get; set; }
    public double BlurRadius { get; set; }
    /// <summary>
    /// Optional visual-property snapshot used by CG Editor AUTO KEY. Motion values remain
    /// strongly typed above; this JSON carries style/text properties (fill, font, outline,
    /// gradient, etc.) so color/typography changes can be keyed without breaking older files.
    /// </summary>
    public string StyleJson { get; set; } = string.Empty;
    public string Ease { get => _ease; set => Set(ref _ease, string.IsNullOrWhiteSpace(value) ? "linear" : value); }
    public bool Hold { get => _hold; set => Set(ref _hold, value); }
    private string _targetProperty = string.Empty;
    /// <summary>
    /// Channel/property this keyframe specifically animates: Position, Scale, Rotation, Opacity, Effects, or empty/All.
    /// Moving a keyframe on a specific sub-track only moves keyframes targeting that property.
    /// </summary>
    public string TargetProperty { get => _targetProperty; set => Set(ref _targetProperty, value ?? string.Empty); }

    public override string ToString() => string.IsNullOrEmpty(TargetProperty) ? $"{TimeSeconds:0.000}s · {Ease}" : $"{TargetProperty} @ {TimeSeconds:0.000}s · {Ease}";
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class CgGroup : INotifyPropertyChanged
{
    private string _name = "Group";
    private bool _visible = true;
    private double _opacity = 1;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public Guid ParentGroupId { get; set; } = Guid.Empty;
    public bool Visible { get => _visible; set => Set(ref _visible, value); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1); }
    public double OriginX { get; set; } = 960;
    public double OriginY { get; set; } = 540;
    public bool IsPrecomposition { get; set; }
    public double StaggerSeconds { get; set; }
    public string StaggerFrom { get; set; } = "Start";
    public int KeyframeRepeat { get; set; }
    public bool KeyframeYoyo { get; set; }
    public List<CgKeyframe> Keyframes { get; set; } = [];

    public override string ToString() => Name;
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class CgDataSource : INotifyPropertyChanged
{
    private string _name = "Data Source";
    private string _sourceType = "LocalJson";
    private string _source = string.Empty;
    private string _selector = string.Empty;
    private double _refreshSeconds = 30;
    private string _status = "Not loaded";
    private string _cachedItemsJson = "[]";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public string SourceType { get => _sourceType; set => Set(ref _sourceType, value); }
    public string Source { get => _source; set => Set(ref _source, value); }
    public string Selector { get => _selector; set => Set(ref _selector, value); }
    public double RefreshSeconds { get => _refreshSeconds; set => Set(ref _refreshSeconds, Math.Clamp(value, 0, 86400)); }
    public bool FirstRowHeader { get; set; } = true;
    public string Delimiter { get; set; } = ",";
    // Network credential names are stored instead of secrets so CG projects can be shared safely.
    // For OpenWeather use OPENWEATHER_API_KEY (set by tools\Set-OpenWeather-Key.ps1).
    public string CredentialEnvironmentVariable { get; set; } = "OPENWEATHER_API_KEY";
    public string Units { get; set; } = "metric";
    public string Language { get; set; } = "en";
    public string Status { get => _status; set => Set(ref _status, value); }
    public string CachedItemsJson { get => _cachedItemsJson; set => Set(ref _cachedItemsJson, string.IsNullOrWhiteSpace(value) ? "[]" : value); }
    public DateTime LastRefreshUtc { get; set; }

    public override string ToString() => Name;
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}


public sealed class CgHtmlSource : INotifyPropertyChanged
{
    private string _name = "HTML Graphic";
    private string _source = string.Empty;
    private string _sourceKind = "File";
    private bool _visible = true;
    private bool _transparent = true;
    private bool _javascriptEnabled = true;
    private bool _keepAlive = true;
    private bool _reloadOnShow;
    private double _opacity = 1;
    private int _browserFps = 30;
    private string _dataJson = "{}";
    private string _status = "Not loaded";

    // Attachment 3 extended web layer configuration
    private bool _adobeFlash;
    private bool _mediaStream = true;
    private bool _javaScriptDialogs;
    private bool _webGL = true;
    private bool _webSecurity;
    private bool _interlacing;
    private bool _externalProcess = true;
    private bool _muteAudio = true;
    private string _transparencyMode = "Auto";
    private int _minPageWidth = 320;
    private int _maxPageWidth = 1920;
    private bool _scrollbars = true;
    private double _scrollSpeedVert;
    private double _scrollSpeedHoriz;
    private int _cropTop;
    private int _cropBottom;
    private int _cropLeft;
    private int _cropRight;
    private int _zoomPercent = 100;
    private int _alphaPercent = 100;
    private int _borderWidth;
    private string _borderColor = "white";
    private double _positionXPercent;
    private double _positionYPercent;
    private double _widthPercent = 100;
    private double _heightPercent;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Source { get => _source; set => Set(ref _source, value); }
    public string SourceKind { get => _sourceKind; set => Set(ref _sourceKind, value); }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1920;
    public double Height { get; set; } = 1080;
    public int CanvasWidth { get; set; } = 1920;
    public int CanvasHeight { get; set; } = 1080;
    public int BrowserFps { get => _browserFps; set => Set(ref _browserFps, Math.Clamp(value, 1, 60)); }
    public bool Visible { get => _visible; set => Set(ref _visible, value); }
    public bool Transparent { get => _transparent; set => Set(ref _transparent, value); }
    public bool JavaScriptEnabled { get => _javascriptEnabled; set => Set(ref _javascriptEnabled, value); }
    public bool KeepAlive { get => _keepAlive; set => Set(ref _keepAlive, value); }
    public bool ReloadOnShow { get => _reloadOnShow; set => Set(ref _reloadOnShow, value); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1); }
    public string DataJson { get => _dataJson; set => Set(ref _dataJson, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

    public bool AdobeFlash { get => _adobeFlash; set => Set(ref _adobeFlash, value); }
    public bool MediaStream { get => _mediaStream; set => Set(ref _mediaStream, value); }
    public bool JavaScriptDialogs { get => _javaScriptDialogs; set => Set(ref _javaScriptDialogs, value); }
    public bool WebGL { get => _webGL; set => Set(ref _webGL, value); }
    public bool WebSecurity { get => _webSecurity; set => Set(ref _webSecurity, value); }
    public bool Interlacing { get => _interlacing; set => Set(ref _interlacing, value); }
    public bool ExternalProcess { get => _externalProcess; set => Set(ref _externalProcess, value); }
    public bool MuteAudio { get => _muteAudio; set => Set(ref _muteAudio, value); }
    public string TransparencyMode { get => _transparencyMode; set => Set(ref _transparencyMode, value); }
    public int MinPageWidth { get => _minPageWidth; set => Set(ref _minPageWidth, value); }
    public int MaxPageWidth { get => _maxPageWidth; set => Set(ref _maxPageWidth, value); }
    public bool Scrollbars { get => _scrollbars; set => Set(ref _scrollbars, value); }
    public double ScrollSpeedVert { get => _scrollSpeedVert; set => Set(ref _scrollSpeedVert, value); }
    public double ScrollSpeedHoriz { get => _scrollSpeedHoriz; set => Set(ref _scrollSpeedHoriz, value); }
    public int CropTop { get => _cropTop; set => Set(ref _cropTop, value); }
    public int CropBottom { get => _cropBottom; set => Set(ref _cropBottom, value); }
    public int CropLeft { get => _cropLeft; set => Set(ref _cropLeft, value); }
    public int CropRight { get => _cropRight; set => Set(ref _cropRight, value); }
    public int ZoomPercent { get => _zoomPercent; set => Set(ref _zoomPercent, value); }
    public int AlphaPercent { get => _alphaPercent; set => Set(ref _alphaPercent, value); }
    public int BorderWidth { get => _borderWidth; set => Set(ref _borderWidth, value); }
    public string BorderColor { get => _borderColor; set => Set(ref _borderColor, value); }
    public double PositionXPercent { get => _positionXPercent; set => Set(ref _positionXPercent, value); }
    public double PositionYPercent { get => _positionYPercent; set => Set(ref _positionYPercent, value); }
    public double WidthPercent { get => _widthPercent; set => Set(ref _widthPercent, value); }
    public double HeightPercent { get => _heightPercent; set => Set(ref _heightPercent, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
