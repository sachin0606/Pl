using System.Text.Json;
using System.Xml.Linq;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

public sealed class DemoIntegrationCheck
{
    public string Name { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public sealed class DemoIntegrationResult
{
    public bool Ok { get; set; }
    public string RundownId { get; set; } = string.Empty;
    public string RundownName { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public int CgTemplateCount { get; set; }
    public int StoryCount { get; set; }
    public int PlayoutItemCount { get; set; }
    public int MosCgItemCount { get; set; }
    public string MosXmlPath { get; set; } = string.Empty;
    public string LiveStoryId { get; set; } = string.Empty;
    public string LiveRundownItemId { get; set; } = string.Empty;
    public List<DemoIntegrationCheck> Checks { get; set; } = [];
}

/// <summary>
/// Deterministic demo loader and integration self-test used by DEMOLOAD.cmd and the API Gateway.
/// The self-test exercises the same persisted NRCS, MOS, CG, playout playlist and prompter state
/// used by the standalone operator applications without requiring an on-air PROGRAM action.
/// </summary>
public sealed class DemoIntegrationService
{
    public DemoIntegrationResult SeedAndValidate()
    {
        var result = new DemoIntegrationResult();
        try
        {
            var database = new BroadcastDatabase();
            var canonical = CgDemoFactory.CreateDefaults();
            Check(result, "CG catalog", CgDemoValidator.CatalogLooksHealthy(canonical), $"{canonical.Count} canonical editable template(s)");

            var canonicalNames = canonical.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existing = database.LoadState("cg-projects", new List<CgProject>());
            var custom = existing.Where(x => !canonicalNames.Contains(x.Name) && !IsGeneratedDemoName(x.Name)).ToList();
            custom.AddRange(canonical);
            database.SaveState("cg-projects", custom);
            database.SaveState("active-cg-id", canonical.FirstOrDefault()?.Id.ToString() ?? string.Empty);
            result.CgTemplateCount = canonical.Count;
            Check(result, "CG persistence", database.LoadState("cg-projects", new List<CgProject>()).Count >= canonical.Count, $"{canonical.Count} template(s) persisted while custom projects were preserved");

            var nrcs = new NrcsPlatformStore();
            var rundown = nrcs.RebuildProfessionalDemo();
            var stories = nrcs.ListStories(rundown.Id).Where(x => !x.IsSkipped).ToList();
            result.RundownId = rundown.Id;
            result.RundownName = rundown.Name;
            result.ChannelId = rundown.ChannelId;
            result.StoryCount = stories.Count;
            var requiredTypes = new[] { "ANCHOR", "PKG", "VO", "SOT", "LIVE", "CG" };
            Check(result, "NRCS demo rundown", stories.Count >= 12 && requiredTypes.All(t => stories.Any(s => s.StoryType.Equals(t, StringComparison.OrdinalIgnoreCase))), $"{stories.Count} stories; types: {string.Join(",", stories.Select(x => x.StoryType).Distinct(StringComparer.OrdinalIgnoreCase))}");
            var missingCgTemplates = stories.Where(x => !string.IsNullOrWhiteSpace(x.CgTemplate) && !canonicalNames.Contains(x.CgTemplate)).Select(x => x.CgTemplate).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Check(result, "NRCS CG template references", missingCgTemplates.Length == 0, missingCgTemplates.Length == 0 ? "All newsroom CG references resolve in the 200-template catalog" : "Missing: " + string.Join(",", missingCgTemplates));
            var missingMedia = stories.Where(x => !string.IsNullOrWhiteSpace(x.MediaPath) && !File.Exists(x.MediaPath)).Select(x => x.MediaPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Check(result, "NRCS demo media", missingMedia.Length == 0, missingMedia.Length == 0 ? "PKG/VO/SOT demo media paths resolve" : "Missing media: " + string.Join(",", missingMedia));

            var published = nrcs.PublishToPlayout(rundown.Id);
            var playlist = database.LoadPlaylist();
            result.PlayoutItemCount = published;
            result.MosCgItemCount = playlist.Count(x => x.EventType.Equals("MOS", StringComparison.OrdinalIgnoreCase) && x.MosAction.Equals("CG_PLAY", StringComparison.OrdinalIgnoreCase));
            Check(result, "NRCS to Playout", published > 0 && playlist.Count == published, $"{published} playlist events published");
            Check(result, "MOS CG_PLAY playlist", result.MosCgItemCount > 0, $"{result.MosCgItemCount} CG_PLAY MOS events in playout rundown");

            var mosPath = nrcs.PublishMosXml(rundown.Id);
            result.MosXmlPath = mosPath;
            var mosOk = ValidateMosXml(mosPath, stories.Count, out var mosDetail);
            Check(result, "MOS XML", mosOk, mosDetail);

            var liveStory = stories.FirstOrDefault(x => x.StoryType.Equals("LIVE", StringComparison.OrdinalIgnoreCase)) ?? stories.FirstOrDefault();
            if (liveStory is not null)
            {
                nrcs.SetLiveStory(rundown.Id, liveStory.Id, liveStory.RundownItemId, true);
                var live = nrcs.GetLiveState(rundown.Id);
                result.LiveStoryId = live?.StoryId ?? string.Empty;
                result.LiveRundownItemId = live?.RundownItemId ?? string.Empty;
                Check(result, "NRCS live state", live is not null && live.OnAir && live.StoryId == liveStory.Id && live.RundownItemId == liveStory.RundownItemId, live is null ? "No live state" : $"{liveStory.Slug} is shared as live story");
            }
            else Check(result, "NRCS live state", false, "No story available for live-state test");

            var prompterStories = nrcs.ListStories(rundown.Id);
            Check(result, "Prompter rundown", prompterStories.Count == stories.Count && prompterStories.All(x => !string.IsNullOrWhiteSpace(x.Body)), $"{prompterStories.Count} scripts available from shared NRCS database");

            TestCgCommandBus(result, canonical.First());
            TestPlatformCommandBus(result);

            result.Ok = result.Checks.All(x => x.Ok);
        }
        catch (Exception ex)
        {
            Check(result, "Integration exception", false, ex.GetBaseException().Message);
            result.Ok = false;
        }
        return result;
    }

    private static void TestCgCommandBus(DemoIntegrationResult result, CgProject project)
    {
        var channel = "KTX-DEMO-CG-SELFTEST";
        var inbox = LocalCgCommandBus.GetInbox(channel);
        CleanupDirectory(inbox);
        var delivered = LocalCgCommandBus.Publish(new[] { channel }, new CgRemoteCommand { Action = "PLAY", Bus = "PREVIEW", Layer = 90, Project = project });
        var file = Directory.Exists(inbox) ? Directory.EnumerateFiles(inbox, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
        CgRemoteCommand? command = null;
        var consumed = file is not null && LocalCgCommandBus.TryConsume(file, out command) && command is not null && command.Action == "PLAY" && command.Bus == "PREVIEW" && command.Project?.Name == project.Name;
        Check(result, "CG command bus", delivered == 1 && consumed, consumed ? "PREVIEW CG PLAY command round-trip passed" : "CG command transport round-trip failed");
        CleanupDirectory(inbox);
    }

    private static void TestPlatformCommandBus(DemoIntegrationResult result)
    {
        var channel = "KTX-DEMO-PLAYOUT-SELFTEST";
        var inbox = PlatformControlBus.Inbox(channel);
        CleanupDirectory(inbox);
        var request = new PlatformControlCommand { ChannelId = channel, Action = "reload_playlist" };
        var reply = PlatformControlBus.Submit(request);
        var file = Directory.Exists(inbox) ? Directory.EnumerateFiles(inbox, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
        PlatformControlCommand? command = null;
        var consumed = file is not null && PlatformControlBus.TryConsume(file, out command) && command is not null && command.Action == "reload_playlist";
        if (consumed && command is not null) PlatformControlBus.Complete(command, true, "OK", "Self-test completion");
        var replyOk = false;
        if (File.Exists(reply))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<PlatformControlResponse>(File.ReadAllText(reply));
                replyOk = parsed?.Ok == true && parsed.Code == "OK";
            }
            catch { }
            try { File.Delete(reply); } catch { }
        }
        Check(result, "Playout command bus", consumed && replyOk, consumed && replyOk ? "reload_playlist command/reply round-trip passed" : "Playout command transport round-trip failed");
        CleanupDirectory(inbox);
    }

    private static bool ValidateMosXml(string path, int expectedStories, out string detail)
    {
        detail = "MOS XML not created";
        if (!File.Exists(path)) return false;
        try
        {
            var doc = XDocument.Load(path);
            var root = doc.Root;
            var stories = doc.Descendants("story").ToList();
            var types = stories.Select(x => x.Element("storyType")?.Value ?? string.Empty).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hasCg = doc.Descendants("item").Any(x => string.Equals((string?)x.Attribute("type"), "CG", StringComparison.OrdinalIgnoreCase) && x.Element("template") is not null && x.Element("layer") is not null && x.Element("data") is not null);
            var durations = stories.All(x => int.TryParse(x.Element("storyDuration")?.Value, out var seconds) && seconds > 0);
            var ok = root?.Name.LocalName == "mos" && stories.Count == expectedStories && hasCg && durations && new[] { "PKG", "VO", "SOT", "LIVE" }.All(types.Contains);
            detail = $"{stories.Count} MOS stories; CG item={(hasCg ? "yes" : "no")}; durations={(durations ? "valid" : "invalid")}";
            return ok;
        }
        catch (Exception ex)
        {
            detail = ex.GetBaseException().Message;
            return false;
        }
    }

    private static bool IsGeneratedDemoName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.StartsWith("Demo ", StringComparison.OrdinalIgnoreCase);
    }

    private static void CleanupDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private static void Check(DemoIntegrationResult result, string name, bool ok, string detail)
    {
        result.Checks.Add(new DemoIntegrationCheck { Name = name, Ok = ok, Detail = detail });
    }
}
