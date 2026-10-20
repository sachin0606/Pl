using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

/// <summary>
/// Factory versions of the two music package graphics. They are ordinary CG projects: they appear in
/// the CG Designer, can be restyled there, and the playout uses the edited copy by name.
/// Layout is 1920x1080, bottom-left above the ticker, as on typical Indian music channels.
/// </summary>
public static class MusicPackageTemplates
{
    private const string Font = "Bahnschrift";

    public static IEnumerable<CgProject> Create() => [CreateInfo(), CreateBox()];

    /// <summary>Song title (grows upwards to two lines), rolling credits, and the song progress bar.</summary>
    public static CgProject CreateInfo()
    {
        var p = new CgProject { Id = new Guid("5a1d0c4e-7b1f-4d6a-9a51-6d0c2f1e0a01"), Name = MusicPackageSettings.DefaultInfoTemplate, Category = "Music", Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = 3 };
        Add(p, new CgLayer
        {
            Name = "Song Title", Type = "Text", Text = "{NowTitle:upper}", X = 135, Y = 839, Width = 265, Height = 58,
            FontFamily = Font, FontSize = 23, Bold = true, Fill = "#EBF2F2F2", VerticalTextAlignment = "Bottom",
            ShadowEnabled = true, ShadowColor = "#99000000", ShadowOffsetX = 1.5, ShadowOffsetY = 1.5,
            AnimationIn = "Fade", AnimationOut = "Fade", AnimationInSeconds = .5, AnimationOutSeconds = .4
        });
        Add(p, new CgLayer
        {
            Name = "Credits", Type = "Credits", X = 135, Y = 897, Width = 640, Height = 48,
            Text = "{NowFilm:upper}\nSinger : {NowSinger}\nLyrics : {NowLyrics}\nMusic : {NowMusic}\n{NowLabel:upper}",
            FontFamily = Font, FontSize = 19, Bold = true, Fill = "#D9E8E8E8", DataItemDurationSeconds = 3.5,
            ShadowEnabled = true, ShadowColor = "#99000000", ShadowOffsetX = 1.5, ShadowOffsetY = 1.5,
            AnimationIn = "Fade", AnimationOut = "Fade", AnimationInSeconds = .5, AnimationOutSeconds = .4
        });
        Add(p, new CgLayer
        {
            Name = "Progress", Type = "Progress", X = 142, Y = 952, Width = 530, Height = 18,
            Fill = "#F2FFFFFF", Background = "#FF2F8CFF", BorderColor = "#FFBFE0FF", BorderWidth = 1.5,
            AnimationIn = "Wipe Left", AnimationOut = "Fade", AnimationInSeconds = .6, AnimationOutSeconds = .4
        });
        return p;
    }

    /// <summary>Blue 3D frame with the NEXT / LATER tag and the upcoming song playing inside.</summary>
    public static CgProject CreateBox()
    {
        var p = new CgProject { Id = new Guid("5a1d0c4e-7b1f-4d6a-9a51-6d0c2f1e0a02"), Name = MusicPackageSettings.DefaultBoxTemplate, Category = "Music", Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = 2 };
        const string inAnim = "Slide Right";
        Add(p, new CgLayer
        {
            Name = "Frame Side", Type = "Shape", ShapeKind = "Custom Path", ShapePathData = "0,0.12;1,0;1,1;0,0.86",
            X = 0, Y = 592, Width = 150, Height = 270, UseGradient = true, Background = "#FF1565C0", GradientColor2 = "#FF64B5F6", GradientAngle = 0,
            AnimationIn = inAnim, AnimationOut = "Fade", AnimationInSeconds = .45, AnimationOutSeconds = .35
        });
        Add(p, new CgLayer
        {
            Name = "Frame Base", Type = "Shape", ShapeKind = "Custom Path", ShapePathData = "0,0;0.92,0;1,1;0,1",
            X = 0, Y = 790, Width = 372, Height = 72, UseGradient = true, Background = "#FF1E88E5", GradientColor2 = "#FF64B5F6", GradientAngle = 90,
            AnimationIn = inAnim, AnimationOut = "Fade", AnimationInSeconds = .45, AnimationOutSeconds = .35
        });
        Add(p, new CgLayer
        {
            Name = "Tag Plate", Type = "Shape", ShapeKind = "Rectangle", X = 145, Y = 519, Width = 245, Height = 48,
            UseGradient = true, Background = "#FF1E88E5", GradientColor2 = "#001E88E5", GradientAngle = 0,
            AnimationIn = "Wipe Left", AnimationOut = "Fade", AnimationInSeconds = .5, AnimationOutSeconds = .3
        });
        Add(p, new CgLayer
        {
            Name = "Tag", Type = "Text", Text = "{BoxTag}", X = 152, Y = 519, Width = 230, Height = 48,
            FontFamily = Font, FontSize = 34, Bold = true, Fill = "#FFFFFFFF",
            AnimationIn = "Fade", AnimationOut = "Fade", AnimationInSeconds = .5, AnimationOutSeconds = .3
        });
        Add(p, new CgLayer
        {
            Name = "Video Border", Type = "Shape", ShapeKind = "Rectangle", X = 141, Y = 575, Width = 333, Height = 203,
            Background = "#FFF5F9FF",
            AnimationIn = inAnim, AnimationOut = "Fade", AnimationInSeconds = .45, AnimationOutSeconds = .35
        });
        Add(p, new CgLayer
        {
            Name = "Next Video", Type = "UpNextVideo", X = 145, Y = 579, Width = 325, Height = 195, Background = "#FF2196F3",
            AnimationIn = inAnim, AnimationOut = "Fade", AnimationInSeconds = .45, AnimationOutSeconds = .35
        });
        Add(p, new CgLayer
        {
            Name = "Caption Shade", Type = "Shape", ShapeKind = "Rectangle", X = 145, Y = 712, Width = 325, Height = 62,
            UseGradient = true, Background = "#00000000", GradientColor2 = "#B4000000", GradientAngle = 90,
            AnimationIn = "Fade", AnimationOut = "Fade", AnimationInSeconds = .5, AnimationOutSeconds = .3
        });
        Add(p, new CgLayer
        {
            Name = "Next Title", Type = "Text", Text = "{BoxTitle:upper}", X = 156, Y = 718, Width = 305, Height = 26,
            FontFamily = Font, FontSize = 16, Bold = true, Fill = "#FFFFFFFF",
            ShadowEnabled = true, ShadowColor = "#B0000000", ShadowOffsetX = 1, ShadowOffsetY = 1,
            AnimationIn = "Fade", AnimationOut = "Fade", AnimationInSeconds = .5, AnimationOutSeconds = .3
        });
        Add(p, new CgLayer
        {
            Name = "Next Film", Type = "Text", Text = "{BoxFilm}", X = 156, Y = 742, Width = 305, Height = 26,
            FontFamily = Font, FontSize = 17, Bold = true, Fill = "#FFF2F2F2",
            ShadowEnabled = true, ShadowColor = "#B0000000", ShadowOffsetX = 1, ShadowOffsetY = 1,
            AnimationIn = "Fade", AnimationOut = "Fade", AnimationInSeconds = .5, AnimationOutSeconds = .3
        });
        return p;
    }

    private static void Add(CgProject project, CgLayer layer)
    {
        layer.StartSeconds = 0;
        layer.EndSeconds = project.DurationSeconds;
        project.Layers.Add(layer);
    }
}
