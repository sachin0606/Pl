using System.Text.Json;

namespace Kashtrix.Prompter;

public sealed record PresenterPromptProfile(
    string Presenter,
    double FontSize,
    double LineSpacing,
    double Speed,
    bool MirrorHorizontal,
    bool FlipVertical,
    bool Invert,
    bool RightToLeft,
    int SafeMargin,
    string Resolution,
    double FramesPerSecond,
    DateTime UpdatedUtc);

public static class PrompterProfileStore
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout", "prompter-presenter-profiles.json");

    public static IReadOnlyList<PresenterPromptProfile> LoadAll()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<List<PresenterPromptProfile>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch { return []; }
    }

    public static PresenterPromptProfile? Find(string? presenter) => string.IsNullOrWhiteSpace(presenter)
        ? null
        : LoadAll().FirstOrDefault(x => x.Presenter.Equals(presenter.Trim(), StringComparison.OrdinalIgnoreCase));

    public static void Save(PresenterPromptProfile profile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var rows = LoadAll().ToList();
        rows.RemoveAll(x => x.Presenter.Equals(profile.Presenter, StringComparison.OrdinalIgnoreCase));
        rows.Add(profile with { UpdatedUtc = DateTime.UtcNow });
        File.WriteAllText(FilePath, JsonSerializer.Serialize(rows.OrderBy(x => x.Presenter), new JsonSerializerOptions { WriteIndented = true }));
    }
}
