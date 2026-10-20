using System.Text.Json;
using System.Xml.Linq;
using BroadcastPlayout.Models;
using Microsoft.Data.Sqlite;

namespace BroadcastPlayout.Services;

public sealed record NrcsRundown(string Id, string Name, string ChannelId, DateTime AirDateUtc, string Status)
{
    public string Studio { get; init; } = "STUDIO-A";
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
}

public sealed record NrcsStory(string Id, string RundownId, int Sequence, string Slug, string Presenter, int DurationSeconds, string Body, string MediaPath, string CgTemplate, int CgLayer, string CgDataJson, DateTime UpdatedUtc)
{
    public string RundownItemId { get; init; } = "";
    public string Writer { get; init; } = "";
    public string Editor { get; init; } = "";
    public string Status { get; init; } = "DRAFT";
    public string Priority { get; init; } = "NORMAL";
    public string Category { get; init; } = "NEWS";
    public string Language { get; init; } = "en";
    public string Notes { get; init; } = "";
    public string StoryType { get; init; } = "PKG";
    public string Segment { get; init; } = "A";
    public string StartMode { get; init; } = "FOLLOW";
    public string ItemStatus { get; init; } = "READY";
    public bool IsSkipped { get; init; }
    public bool IsLocked { get; init; }
    public bool IsLive { get; init; }
    public DateTime PlannedStartUtc { get; init; }
    public DateTime PlannedEndUtc { get; init; }
}

public sealed record NrcsAssignment(string Id, string Title, string Angle, string Assignee, string Desk, string Location, string Status, string Priority, DateTime DueUtc, string LinkedStoryId, string Notes, DateTime UpdatedUtc);
public sealed record NrcsStoryVersion(long VersionId, string StoryId, DateTime CreatedUtc, string Slug, string Body, string Writer, string Editor, string Status, string SnapshotJson);
public sealed record NrcsLiveState(string RundownId, string StoryId, string RundownItemId, bool OnAir, DateTime UpdatedUtc);

public sealed class NrcsPlatformStore
{
    public static string DatabasePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout", "platform.db");
    private string ConnectionString => $"Data Source={DatabasePath};Mode=ReadWriteCreate;Cache=Shared";

    public NrcsPlatformStore()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS nrcs_rundowns(
              id TEXT PRIMARY KEY, name TEXT NOT NULL, channel_id TEXT NOT NULL,
              air_date_utc TEXT NOT NULL, status TEXT NOT NULL, updated_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS nrcs_stories(
              id TEXT PRIMARY KEY, rundown_id TEXT NOT NULL, seq INTEGER NOT NULL,
              slug TEXT NOT NULL, presenter TEXT NOT NULL, duration_seconds INTEGER NOT NULL,
              body TEXT NOT NULL, media_path TEXT NOT NULL, cg_template TEXT NOT NULL,
              cg_layer INTEGER NOT NULL, cg_data_json TEXT NOT NULL, updated_utc TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_nrcs_stories_rundown_seq ON nrcs_stories(rundown_id,seq);

            CREATE TABLE IF NOT EXISTS nrcs_story_bank(
              id TEXT PRIMARY KEY, slug TEXT NOT NULL, presenter TEXT NOT NULL,
              duration_seconds INTEGER NOT NULL, body TEXT NOT NULL, media_path TEXT NOT NULL,
              cg_template TEXT NOT NULL, cg_layer INTEGER NOT NULL, cg_data_json TEXT NOT NULL,
              writer TEXT NOT NULL DEFAULT '', editor TEXT NOT NULL DEFAULT '',
              status TEXT NOT NULL DEFAULT 'DRAFT', priority TEXT NOT NULL DEFAULT 'NORMAL',
              category TEXT NOT NULL DEFAULT 'NEWS', language TEXT NOT NULL DEFAULT 'en',
              notes TEXT NOT NULL DEFAULT '', story_type TEXT NOT NULL DEFAULT 'PKG', updated_utc TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_nrcs_story_bank_slug ON nrcs_story_bank(slug);
            CREATE INDEX IF NOT EXISTS ix_nrcs_story_bank_status ON nrcs_story_bank(status);

            CREATE TABLE IF NOT EXISTS nrcs_rundown_items(
              id TEXT PRIMARY KEY, rundown_id TEXT NOT NULL, story_id TEXT NOT NULL,
              seq INTEGER NOT NULL, segment TEXT NOT NULL DEFAULT 'A',
              start_mode TEXT NOT NULL DEFAULT 'FOLLOW', item_status TEXT NOT NULL DEFAULT 'READY',
              is_skipped INTEGER NOT NULL DEFAULT 0, is_locked INTEGER NOT NULL DEFAULT 0,
              updated_utc TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_nrcs_rundown_items_ro_seq ON nrcs_rundown_items(rundown_id,seq);
            CREATE INDEX IF NOT EXISTS ix_nrcs_rundown_items_story ON nrcs_rundown_items(story_id);

            CREATE TABLE IF NOT EXISTS nrcs_assignments(
              id TEXT PRIMARY KEY, title TEXT NOT NULL, angle TEXT NOT NULL DEFAULT '',
              assignee TEXT NOT NULL DEFAULT '', desk TEXT NOT NULL DEFAULT 'NEWS',
              location TEXT NOT NULL DEFAULT '', status TEXT NOT NULL DEFAULT 'PLANNED',
              priority TEXT NOT NULL DEFAULT 'NORMAL', due_utc TEXT NOT NULL,
              linked_story_id TEXT NOT NULL DEFAULT '', notes TEXT NOT NULL DEFAULT '',
              updated_utc TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_nrcs_assignments_due ON nrcs_assignments(due_utc);

            CREATE TABLE IF NOT EXISTS nrcs_story_versions(
              version_id INTEGER PRIMARY KEY AUTOINCREMENT, story_id TEXT NOT NULL,
              created_utc TEXT NOT NULL, slug TEXT NOT NULL, body TEXT NOT NULL,
              writer TEXT NOT NULL DEFAULT '', editor TEXT NOT NULL DEFAULT '',
              status TEXT NOT NULL DEFAULT 'DRAFT', snapshot_json TEXT NOT NULL DEFAULT '{}');
            CREATE INDEX IF NOT EXISTS ix_nrcs_story_versions_story ON nrcs_story_versions(story_id,version_id DESC);

            CREATE TABLE IF NOT EXISTS nrcs_live_state(
              rundown_id TEXT PRIMARY KEY, story_id TEXT NOT NULL DEFAULT '',
              rundown_item_id TEXT NOT NULL DEFAULT '', on_air INTEGER NOT NULL DEFAULT 0,
              updated_utc TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        TryAlter(c, "ALTER TABLE nrcs_story_bank ADD COLUMN story_type TEXT NOT NULL DEFAULT 'PKG'");
        MigrateLegacyStories(c);
    }

    private SqliteConnection Open() { var c = new SqliteConnection(ConnectionString); c.Open(); return c; }

    private static void MigrateLegacyStories(SqliteConnection c)
    {
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO nrcs_story_bank(id,slug,presenter,duration_seconds,body,media_path,cg_template,cg_layer,cg_data_json,updated_utc)
            SELECT id,slug,presenter,duration_seconds,body,media_path,cg_template,cg_layer,cg_data_json,updated_utc FROM nrcs_stories;
            INSERT OR IGNORE INTO nrcs_rundown_items(id,rundown_id,story_id,seq,segment,start_mode,item_status,is_skipped,is_locked,updated_utc)
            SELECT id,rundown_id,id,seq,'A','FOLLOW','READY',0,0,updated_utc FROM nrcs_stories;
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public IReadOnlyList<NrcsRundown> ListRundowns()
    {
        var list = new List<NrcsRundown>(); using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,channel_id,air_date_utc,status,updated_utc FROM nrcs_rundowns ORDER BY air_date_utc DESC,name";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), ParseUtc(r.GetString(3)), r.GetString(4)) { UpdatedUtc = ParseUtc(r.GetString(5)) });
        return list;
    }

    public NrcsRundown CreateRundown(string name, string channelId, DateTime airDateUtc)
    {
        var row = new NrcsRundown(Guid.NewGuid().ToString("N"), string.IsNullOrWhiteSpace(name) ? "New Rundown" : name.Trim(), string.IsNullOrWhiteSpace(channelId) ? "KTX-PLAYOUT-01" : channelId.Trim(), airDateUtc.ToUniversalTime(), "DRAFT");
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO nrcs_rundowns(id,name,channel_id,air_date_utc,status,updated_utc) VALUES($id,$n,$ch,$air,$s,$u)";
        cmd.Parameters.AddWithValue("$id", row.Id); cmd.Parameters.AddWithValue("$n", row.Name); cmd.Parameters.AddWithValue("$ch", row.ChannelId); cmd.Parameters.AddWithValue("$air", row.AirDateUtc.ToString("O")); cmd.Parameters.AddWithValue("$s", row.Status); cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
        return row;
    }

    public void SetRundownStatus(string id, string status)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE nrcs_rundowns SET status=$s,updated_utc=$u WHERE id=$id";
        cmd.Parameters.AddWithValue("$s", Normalize(status, "DRAFT")); cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<NrcsStory> ListStoryBank(string? search = null)
    {
        var list = new List<NrcsStory>(); using var c = Open(); using var cmd = c.CreateCommand();
        var hasSearch = !string.IsNullOrWhiteSpace(search);
        cmd.CommandText = """
            SELECT id,slug,presenter,duration_seconds,body,media_path,cg_template,cg_layer,cg_data_json,
                   writer,editor,status,priority,category,language,notes,story_type,updated_utc
            FROM nrcs_story_bank
            """ + (hasSearch ? " WHERE slug LIKE $q OR body LIKE $q OR presenter LIKE $q OR writer LIKE $q OR category LIKE $q " : "") + " ORDER BY updated_utc DESC,slug";
        if (hasSearch) cmd.Parameters.AddWithValue("$q", "%" + search!.Trim() + "%");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadBankStory(r));
        return list;
    }

    public NrcsStory? GetStory(string storyId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id,slug,presenter,duration_seconds,body,media_path,cg_template,cg_layer,cg_data_json,
                   writer,editor,status,priority,category,language,notes,story_type,updated_utc
            FROM nrcs_story_bank WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", storyId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadBankStory(r) : null;
    }

    public NrcsStory CreateStoryDraft(string? slug = null)
    {
        var count = ListStoryBank().Count + 1;
        var now = DateTime.UtcNow;
        var story = new NrcsStory(Guid.NewGuid().ToString("N"), "", 0, string.IsNullOrWhiteSpace(slug) ? $"STORY {count:000}" : slug.Trim(), "", 30, "", "", "", 20, "{}", now)
        {
            Status = "DRAFT", Priority = "NORMAL", Category = "NEWS", Language = "en"
        };
        SaveStory(story, createVersion: true);
        return story;
    }

    public NrcsStory CreateStory(string rundownId)
    {
        var story = CreateStoryDraft();
        var itemId = AddStoryToRundown(story.Id, rundownId);
        return ListStories(rundownId).First(x => x.RundownItemId == itemId);
    }

    public void SaveStory(NrcsStory s, bool createVersion = true)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
              INSERT INTO nrcs_story_bank(id,slug,presenter,duration_seconds,body,media_path,cg_template,cg_layer,cg_data_json,writer,editor,status,priority,category,language,notes,story_type,updated_utc)
              VALUES($id,$slug,$p,$d,$b,$m,$cg,$l,$j,$w,$e,$st,$pr,$cat,$lang,$notes,$type,$u)
              ON CONFLICT(id) DO UPDATE SET slug=$slug,presenter=$p,duration_seconds=$d,body=$b,media_path=$m,cg_template=$cg,cg_layer=$l,cg_data_json=$j,writer=$w,editor=$e,status=$st,priority=$pr,category=$cat,language=$lang,notes=$notes,story_type=$type,updated_utc=$u
              """;
            cmd.Parameters.AddWithValue("$id", s.Id); cmd.Parameters.AddWithValue("$slug", s.Slug ?? ""); cmd.Parameters.AddWithValue("$p", s.Presenter ?? ""); cmd.Parameters.AddWithValue("$d", Math.Max(1, s.DurationSeconds)); cmd.Parameters.AddWithValue("$b", s.Body ?? ""); cmd.Parameters.AddWithValue("$m", s.MediaPath ?? ""); cmd.Parameters.AddWithValue("$cg", s.CgTemplate ?? ""); cmd.Parameters.AddWithValue("$l", Math.Clamp(s.CgLayer, 0, 999)); cmd.Parameters.AddWithValue("$j", ValidJson(s.CgDataJson)); cmd.Parameters.AddWithValue("$w", s.Writer ?? ""); cmd.Parameters.AddWithValue("$e", s.Editor ?? ""); cmd.Parameters.AddWithValue("$st", Normalize(s.Status, "DRAFT")); cmd.Parameters.AddWithValue("$pr", Normalize(s.Priority, "NORMAL")); cmd.Parameters.AddWithValue("$cat", Normalize(s.Category, "NEWS")); cmd.Parameters.AddWithValue("$lang", Normalize(s.Language, "en")); cmd.Parameters.AddWithValue("$notes", s.Notes ?? ""); cmd.Parameters.AddWithValue("$type", Normalize(s.StoryType, "PKG")); cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
        }

        if (!string.IsNullOrWhiteSpace(s.RundownId))
        {
            var itemId = string.IsNullOrWhiteSpace(s.RundownItemId) ? s.Id : s.RundownItemId;
            using var item = c.CreateCommand();
            item.Transaction = tx;
            item.CommandText = """
                INSERT INTO nrcs_rundown_items(id,rundown_id,story_id,seq,segment,start_mode,item_status,is_skipped,is_locked,updated_utc)
                VALUES($id,$r,$story,$seq,$seg,$start,$st,$skip,$lock,$u)
                ON CONFLICT(id) DO UPDATE SET seq=$seq,segment=$seg,start_mode=$start,item_status=$st,is_skipped=$skip,is_locked=$lock,updated_utc=$u
                """;
            item.Parameters.AddWithValue("$id", itemId); item.Parameters.AddWithValue("$r", s.RundownId); item.Parameters.AddWithValue("$story", s.Id); item.Parameters.AddWithValue("$seq", Math.Max(1, s.Sequence)); item.Parameters.AddWithValue("$seg", Normalize(s.Segment, "A")); item.Parameters.AddWithValue("$start", Normalize(s.StartMode, "FOLLOW")); item.Parameters.AddWithValue("$st", Normalize(s.ItemStatus, "READY")); item.Parameters.AddWithValue("$skip", s.IsSkipped ? 1 : 0); item.Parameters.AddWithValue("$lock", s.IsLocked ? 1 : 0); item.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); item.ExecuteNonQuery();

            using var legacy = c.CreateCommand();
            legacy.Transaction = tx;
            legacy.CommandText = """
                INSERT INTO nrcs_stories(id,rundown_id,seq,slug,presenter,duration_seconds,body,media_path,cg_template,cg_layer,cg_data_json,updated_utc)
                VALUES($id,$r,$seq,$slug,$p,$d,$b,$m,$cg,$l,$j,$u)
                ON CONFLICT(id) DO UPDATE SET rundown_id=$r,seq=$seq,slug=$slug,presenter=$p,duration_seconds=$d,body=$b,media_path=$m,cg_template=$cg,cg_layer=$l,cg_data_json=$j,updated_utc=$u
                """;
            legacy.Parameters.AddWithValue("$id", s.Id); legacy.Parameters.AddWithValue("$r", s.RundownId); legacy.Parameters.AddWithValue("$seq", Math.Max(1, s.Sequence)); legacy.Parameters.AddWithValue("$slug", s.Slug ?? ""); legacy.Parameters.AddWithValue("$p", s.Presenter ?? ""); legacy.Parameters.AddWithValue("$d", Math.Max(1, s.DurationSeconds)); legacy.Parameters.AddWithValue("$b", s.Body ?? ""); legacy.Parameters.AddWithValue("$m", s.MediaPath ?? ""); legacy.Parameters.AddWithValue("$cg", s.CgTemplate ?? ""); legacy.Parameters.AddWithValue("$l", Math.Clamp(s.CgLayer, 0, 999)); legacy.Parameters.AddWithValue("$j", ValidJson(s.CgDataJson)); legacy.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); legacy.ExecuteNonQuery();
        }

        if (createVersion)
        {
            using var v = c.CreateCommand();
            v.Transaction = tx;
            v.CommandText = "INSERT INTO nrcs_story_versions(story_id,created_utc,slug,body,writer,editor,status,snapshot_json) VALUES($id,$u,$slug,$body,$w,$e,$st,$snap)";
            v.Parameters.AddWithValue("$id", s.Id); v.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); v.Parameters.AddWithValue("$slug", s.Slug ?? ""); v.Parameters.AddWithValue("$body", s.Body ?? ""); v.Parameters.AddWithValue("$w", s.Writer ?? ""); v.Parameters.AddWithValue("$e", s.Editor ?? ""); v.Parameters.AddWithValue("$st", Normalize(s.Status, "DRAFT"));
            v.Parameters.AddWithValue("$snap", JsonSerializer.Serialize(new { s.Slug, s.Presenter, s.DurationSeconds, s.Body, s.MediaPath, s.CgTemplate, s.CgLayer, s.CgDataJson, s.Writer, s.Editor, s.Status, s.Priority, s.Category, s.Language, s.Notes, s.StoryType }));
            v.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void DeleteStory(string id)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        foreach (var sql in new[] { "DELETE FROM nrcs_live_state WHERE story_id=$id", "DELETE FROM nrcs_rundown_items WHERE story_id=$id", "DELETE FROM nrcs_story_versions WHERE story_id=$id", "DELETE FROM nrcs_story_bank WHERE id=$id", "DELETE FROM nrcs_stories WHERE id=$id" })
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<NrcsStory> ListStories(string rundownId)
    {
        var rundown = ListRundowns().FirstOrDefault(x => x.Id == rundownId);
        var live = GetLiveState(rundownId);
        var list = new List<NrcsStory>(); using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT b.id,i.id,i.seq,b.slug,b.presenter,b.duration_seconds,b.body,b.media_path,b.cg_template,b.cg_layer,b.cg_data_json,
                   b.writer,b.editor,b.status,b.priority,b.category,b.language,b.notes,b.story_type,b.updated_utc,
                   i.segment,i.start_mode,i.item_status,i.is_skipped,i.is_locked
            FROM nrcs_rundown_items i JOIN nrcs_story_bank b ON b.id=i.story_id
            WHERE i.rundown_id=$r ORDER BY i.seq,i.id
            """;
        cmd.Parameters.AddWithValue("$r", rundownId); using var r = cmd.ExecuteReader();
        var cumulative = 0;
        while (r.Read())
        {
            var story = new NrcsStory(r.GetString(0), rundownId, r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetInt32(9), r.GetString(10), ParseUtc(r.GetString(19)))
            {
                RundownItemId = r.GetString(1), Writer = r.GetString(11), Editor = r.GetString(12), Status = r.GetString(13), Priority = r.GetString(14), Category = r.GetString(15), Language = r.GetString(16), Notes = r.GetString(17), StoryType = r.GetString(18),
                Segment = r.GetString(20), StartMode = r.GetString(21), ItemStatus = r.GetString(22), IsSkipped = r.GetInt32(23) != 0, IsLocked = r.GetInt32(24) != 0,
                IsLive = live is not null && live.OnAir && live.RundownItemId == r.GetString(1),
                PlannedStartUtc = (rundown?.AirDateUtc ?? DateTime.UtcNow).AddSeconds(cumulative),
                PlannedEndUtc = (rundown?.AirDateUtc ?? DateTime.UtcNow).AddSeconds(cumulative + (r.GetInt32(23) != 0 ? 0 : Math.Max(1, r.GetInt32(5))))
            };
            list.Add(story);
            if (!story.IsSkipped) cumulative += Math.Max(1, story.DurationSeconds);
        }
        return list;
    }

    public string AddStoryToRundown(string storyId, string rundownId)
    {
        if (GetStory(storyId) is null) throw new InvalidOperationException("Story not found.");
        var seq = ListStories(rundownId).Count + 1;
        var id = Guid.NewGuid().ToString("N");
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO nrcs_rundown_items(id,rundown_id,story_id,seq,segment,start_mode,item_status,is_skipped,is_locked,updated_utc) VALUES($id,$r,$s,$seq,'A','FOLLOW','READY',0,0,$u)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$r", rundownId); cmd.Parameters.AddWithValue("$s", storyId); cmd.Parameters.AddWithValue("$seq", seq); cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
        return id;
    }

    public void RemoveRundownItem(string itemId)
    {
        using var c = Open();
        string rundownId = "";
        using (var q = c.CreateCommand()) { q.CommandText = "SELECT rundown_id FROM nrcs_rundown_items WHERE id=$id"; q.Parameters.AddWithValue("$id", itemId); rundownId = Convert.ToString(q.ExecuteScalar()) ?? ""; }
        using (var live = c.CreateCommand()) { live.CommandText = "DELETE FROM nrcs_live_state WHERE rundown_item_id=$id"; live.Parameters.AddWithValue("$id", itemId); live.ExecuteNonQuery(); }
        using (var cmd = c.CreateCommand()) { cmd.CommandText = "DELETE FROM nrcs_rundown_items WHERE id=$id"; cmd.Parameters.AddWithValue("$id", itemId); cmd.ExecuteNonQuery(); }
        if (!string.IsNullOrWhiteSpace(rundownId)) Resequence(c, rundownId);
    }

    public void MoveRundownItem(string rundownId, string itemId, int delta)
    {
        var rows = ListStories(rundownId).ToList(); var index = rows.FindIndex(x => x.RundownItemId == itemId); if (index < 0) return;
        var target = Math.Clamp(index + Math.Sign(delta), 0, rows.Count - 1); if (target == index) return;
        (rows[index], rows[target]) = (rows[target], rows[index]);
        using var c = Open(); using var tx = c.BeginTransaction();
        for (var i = 0; i < rows.Count; i++) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "UPDATE nrcs_rundown_items SET seq=$seq,updated_utc=$u WHERE id=$id"; cmd.Parameters.AddWithValue("$seq", i + 1); cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$id", rows[i].RundownItemId); cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    public void UpdateRundownItem(NrcsStory story)
    {
        if (string.IsNullOrWhiteSpace(story.RundownItemId)) return;
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE nrcs_rundown_items SET segment=$seg,start_mode=$start,item_status=$st,is_skipped=$skip,is_locked=$lock,updated_utc=$u WHERE id=$id";
        cmd.Parameters.AddWithValue("$seg", Normalize(story.Segment, "A")); cmd.Parameters.AddWithValue("$start", Normalize(story.StartMode, "FOLLOW")); cmd.Parameters.AddWithValue("$st", Normalize(story.ItemStatus, "READY")); cmd.Parameters.AddWithValue("$skip", story.IsSkipped ? 1 : 0); cmd.Parameters.AddWithValue("$lock", story.IsLocked ? 1 : 0); cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$id", story.RundownItemId); cmd.ExecuteNonQuery();
        if (story.IsSkipped)
        {
            using var live = c.CreateCommand();
            live.CommandText = "UPDATE nrcs_live_state SET story_id='',rundown_item_id='',on_air=0,updated_utc=$u WHERE rundown_item_id=$id";
            live.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); live.Parameters.AddWithValue("$id", story.RundownItemId); live.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<NrcsStoryVersion> ListStoryVersions(string storyId, int max = 30)
    {
        var list = new List<NrcsStoryVersion>(); using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT version_id,story_id,created_utc,slug,body,writer,editor,status,snapshot_json FROM nrcs_story_versions WHERE story_id=$id ORDER BY version_id DESC LIMIT $max"; cmd.Parameters.AddWithValue("$id", storyId); cmd.Parameters.AddWithValue("$max", Math.Clamp(max, 1, 200));
        using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(new(r.GetInt64(0), r.GetString(1), ParseUtc(r.GetString(2)), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8)));
        return list;
    }

    public IReadOnlyList<NrcsAssignment> ListAssignments()
    {
        var list = new List<NrcsAssignment>(); using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT id,title,angle,assignee,desk,location,status,priority,due_utc,linked_story_id,notes,updated_utc FROM nrcs_assignments ORDER BY due_utc,status,title";
        using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), ParseUtc(r.GetString(8)), r.GetString(9), r.GetString(10), ParseUtc(r.GetString(11)))); return list;
    }

    public NrcsAssignment CreateAssignment()
    {
        var now = DateTime.UtcNow; var a = new NrcsAssignment(Guid.NewGuid().ToString("N"), "NEW ASSIGNMENT", "", "", "NEWS", "", "PLANNED", "NORMAL", now.AddHours(2), "", "", now); SaveAssignment(a); return a;
    }

    public void SaveAssignment(NrcsAssignment a)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nrcs_assignments(id,title,angle,assignee,desk,location,status,priority,due_utc,linked_story_id,notes,updated_utc)
            VALUES($id,$t,$a,$as,$d,$l,$s,$p,$due,$story,$n,$u)
            ON CONFLICT(id) DO UPDATE SET title=$t,angle=$a,assignee=$as,desk=$d,location=$l,status=$s,priority=$p,due_utc=$due,linked_story_id=$story,notes=$n,updated_utc=$u
            """;
        cmd.Parameters.AddWithValue("$id", a.Id); cmd.Parameters.AddWithValue("$t", a.Title ?? ""); cmd.Parameters.AddWithValue("$a", a.Angle ?? ""); cmd.Parameters.AddWithValue("$as", a.Assignee ?? ""); cmd.Parameters.AddWithValue("$d", Normalize(a.Desk, "NEWS")); cmd.Parameters.AddWithValue("$l", a.Location ?? ""); cmd.Parameters.AddWithValue("$s", Normalize(a.Status, "PLANNED")); cmd.Parameters.AddWithValue("$p", Normalize(a.Priority, "NORMAL")); cmd.Parameters.AddWithValue("$due", a.DueUtc.ToUniversalTime().ToString("O")); cmd.Parameters.AddWithValue("$story", a.LinkedStoryId ?? ""); cmd.Parameters.AddWithValue("$n", a.Notes ?? ""); cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }

    public void DeleteAssignment(string id) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM nrcs_assignments WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }

    public NrcsStory CreateStoryFromAssignment(NrcsAssignment assignment)
    {
        var story = CreateStoryDraft(assignment.Title) with { Notes = assignment.Angle, Priority = assignment.Priority, Category = assignment.Desk, Status = "DRAFT" };
        SaveStory(story);
        SaveAssignment(assignment with { LinkedStoryId = story.Id, Status = "IN PROGRESS", UpdatedUtc = DateTime.UtcNow });
        return story;
    }

    public NrcsLiveState? GetLiveState(string rundownId)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT rundown_id,story_id,rundown_item_id,on_air,updated_utc FROM nrcs_live_state WHERE rundown_id=$r"; cmd.Parameters.AddWithValue("$r", rundownId); using var r = cmd.ExecuteReader(); return r.Read() ? new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3) != 0, ParseUtc(r.GetString(4))) : null;
    }

    public void SetLiveStory(string rundownId, string storyId, string itemId, bool onAir = true)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = """
            INSERT INTO nrcs_live_state(rundown_id,story_id,rundown_item_id,on_air,updated_utc) VALUES($r,$s,$i,$o,$u)
            ON CONFLICT(rundown_id) DO UPDATE SET story_id=$s,rundown_item_id=$i,on_air=$o,updated_utc=$u
            """; cmd.Parameters.AddWithValue("$r", rundownId); cmd.Parameters.AddWithValue("$s", storyId ?? ""); cmd.Parameters.AddWithValue("$i", itemId ?? ""); cmd.Parameters.AddWithValue("$o", onAir ? 1 : 0); cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }

    public int PublishToPlayout(string rundownId)
    {
        var rundown = ListRundowns().FirstOrDefault(x => x.Id == rundownId) ?? throw new InvalidOperationException("Rundown not found.");
        var items = new List<PlaylistItem>(); var seq = 1;
        foreach (var story in ListStories(rundownId).Where(x => !x.IsSkipped))
        {
            if (!string.IsNullOrWhiteSpace(story.MediaPath))
            {
                var dur = TimeSpan.FromSeconds(Math.Max(1, story.DurationSeconds));
                items.Add(new PlaylistItem { Sequence = seq++, EventType = story.StoryType is "LIVE" or "REMOTE" ? "LIVE" : story.StoryType is "VO" ? "VO" : story.StoryType is "SOT" ? "SOT" : "CLIP", Category = "News", Title = story.Slug, FilePath = story.MediaPath, InPoint = TimeSpan.Zero, OutPoint = dur, SourceDuration = dur, Status = "Ready", MosItemId = story.Id, MosTitle = story.Slug, Notes = $"NRCS {story.Id} · {story.Presenter} · {story.StoryType} · {story.StartMode}" });
            }
            if (!string.IsNullOrWhiteSpace(story.CgTemplate))
            {
                items.Add(new PlaylistItem { Sequence = seq++, EventType = "MOS", Category = "Graphics", Title = $"CG · {story.CgTemplate}", MosAction = "CG_PLAY", MosItemId = story.Id, MosTitle = story.CgTemplate, MosLayer = story.CgLayer, MosDataJson = ValidJson(story.CgDataJson), Notes = $"NRCS CG · layer {story.CgLayer}", InPoint = TimeSpan.Zero, OutPoint = TimeSpan.FromMilliseconds(100), SourceDuration = TimeSpan.FromMilliseconds(100), Status = "Ready" });
            }
        }
        new BroadcastDatabase().SavePlaylist(items);
        _ = PlatformControlBus.Submit(new PlatformControlCommand { ChannelId = rundown.ChannelId, Action = "reload_playlist" });
        SetRundownStatus(rundownId, "PUBLISHED");
        return items.Count;
    }

    public string PublishMosXml(string rundownId)
    {
        var rundown = ListRundowns().FirstOrDefault(x => x.Id == rundownId) ?? throw new InvalidOperationException("Rundown not found.");
        var stories = ListStories(rundownId).Where(x => !x.IsSkipped).OrderBy(x => x.Sequence).ToList();
        var totalDuration = stories.Sum(x => Math.Max(1, x.DurationSeconds));
        var ro = new XElement("roCreate",
            new XElement("roID", rundown.Id),
            new XElement("roSlug", rundown.Name),
            new XElement("roChannel", rundown.ChannelId),
            new XElement("roEdStart", rundown.AirDateUtc.ToString("O")),
            new XElement("roEdDur", totalDuration),
            new XElement("roStatus", rundown.Status),
            new XElement("storyList", stories.Select(s => new XElement("story",
                new XElement("storyID", s.Id),
                new XElement("storySlug", s.Slug),
                new XElement("storyNum", s.Sequence),
                new XElement("storyAbstract", FirstLine(s.Body)),
                new XElement("storyBody", s.Body),
                new XElement("storyPresenter", s.Presenter),
                new XElement("storyWriter", s.Writer),
                new XElement("storyEditor", s.Editor),
                new XElement("storyStatus", s.Status),
                new XElement("storyType", s.StoryType),
                new XElement("storyDuration", s.DurationSeconds),
                new XElement("itemList",
                    string.IsNullOrWhiteSpace(s.MediaPath) ? null : new XElement("item",
                        new XAttribute("type", s.StoryType),
                        new XElement("itemID", $"{s.Id}-MEDIA"),
                        new XElement("objID", Path.GetFileNameWithoutExtension(s.MediaPath)),
                        new XElement("objSlug", s.Slug + " MEDIA"),
                        new XElement("mosAbstract", $"{s.StoryType} media for {s.Slug}"),
                        new XElement("objPath", s.MediaPath),
                        new XElement("duration", s.DurationSeconds)),
                    string.IsNullOrWhiteSpace(s.CgTemplate) ? null : new XElement("item",
                        new XAttribute("type", "CG"),
                        new XElement("itemID", $"{s.Id}-CG"),
                        new XElement("objID", s.CgTemplate),
                        new XElement("objSlug", s.Slug + " GRAPHIC"),
                        new XElement("mosAbstract", $"CG template {s.CgTemplate} on layer {s.CgLayer}"),
                        new XElement("template", s.CgTemplate),
                        new XElement("layer", s.CgLayer),
                        new XElement("data", ValidJson(s.CgDataJson))))))));
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), new XElement("mos",
            new XElement("mosID", "Kashtrix.NRCS"),
            new XElement("ncsID", "Kashtrix.NRCS"),
            new XElement("messageID", Guid.NewGuid().ToString("N")),
            new XElement("messageTimestamp", DateTime.UtcNow.ToString("O")),
            ro));
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout", "MOS", "Outbox");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{rundown.Id}.xml");
        doc.Save(path);
        return path;
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var line = text.Replace("\r", " ").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        return line.Length <= 180 ? line : line[..177] + "...";
    }

    public async Task<(string Path, bool Sent, string Message)> PublishMosAsync(string rundownId, CancellationToken ct = default)
    {
        var path = PublishMosXml(rundownId); var profile = AutomationGatewayProfileStore.Load();
        try { using var gateway = new AutomationGatewayService(); await gateway.SendMosAsync(profile.NcsHost, profile.MosLowerPort, File.ReadAllText(path), ct).ConfigureAwait(false); return (path, true, $"Sent to {profile.NcsHost}:{profile.MosLowerPort}"); }
        catch (Exception ex) { return (path, false, ex.GetBaseException().Message); }
    }

    public (int Stories, int Ready, int Skipped, int DurationSeconds) GetRundownSummary(string rundownId)
    {
        var rows = ListStories(rundownId); return (rows.Count, rows.Count(x => !x.IsSkipped && (x.Status == "READY" || x.Status == "APPROVED")), rows.Count(x => x.IsSkipped), rows.Where(x => !x.IsSkipped).Sum(x => Math.Max(1, x.DurationSeconds)));
    }

    private static NrcsStory ReadBankStory(SqliteDataReader r) => new(r.GetString(0), "", 0, r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt32(7), r.GetString(8), ParseUtc(r.GetString(17)))
    {
        Writer = r.GetString(9), Editor = r.GetString(10), Status = r.GetString(11), Priority = r.GetString(12), Category = r.GetString(13), Language = r.GetString(14), Notes = r.GetString(15), StoryType = r.GetString(16)
    };

    private static void Resequence(SqliteConnection c, string rundownId)
    {
        var ids = new List<string>(); using (var q = c.CreateCommand()) { q.CommandText = "SELECT id FROM nrcs_rundown_items WHERE rundown_id=$r ORDER BY seq,id"; q.Parameters.AddWithValue("$r", rundownId); using var r = q.ExecuteReader(); while (r.Read()) ids.Add(r.GetString(0)); }
        for (var i = 0; i < ids.Count; i++) { using var u = c.CreateCommand(); u.CommandText = "UPDATE nrcs_rundown_items SET seq=$s WHERE id=$id"; u.Parameters.AddWithValue("$s", i + 1); u.Parameters.AddWithValue("$id", ids[i]); u.ExecuteNonQuery(); }
    }


    public NrcsRundown SeedProfessionalDemo()
    {
        const string demoName = "KASHTRIX DEMO NEWS · 18:00";
        var existing = ListRundowns().FirstOrDefault(x => x.Name == demoName);
        if (existing is not null) return existing;
        var rundown = CreateRundown(demoName, "KTX-PLAYOUT-01", DateTime.UtcNow.Date.AddHours(18));
        var demoMedia = ResolveDemoMediaPath();
        var rows = new[]
        {
            ("OPEN · HEADLINES","ANCHOR",55,"Good evening and welcome to Kashtrix Demo News. Tonight, our lead story looks at how city authorities are preparing for a major week of transport and public-service activity across the capital. We will also bring you a live report from the city center, a detailed business update, international headlines, sport, entertainment and the latest weather forecast.\n\nFirst, the headlines. Officials say additional traffic management teams will be deployed from early morning, with public transport operators asked to add capacity on the busiest corridors. In business, regional markets ended mixed as investors assessed new earnings guidance. And in sport, preparations continue for this weekend's national league fixtures.\n\nStay with us for the full stories, live analysis and graphics throughout the bulletin.","","News · Crystal Headline Lower Third",20,"{\"headline\":\"Kashtrix Demo News\",\"subheadline\":\"Full integrated newsroom bulletin\"}"),
            ("TOP STORY · PACKAGE · CITY PLAN","PKG",105,"Our top story tonight: city authorities have announced a coordinated operations plan covering traffic, emergency access, public transport and information services for the coming week. The plan follows several days of consultation with transport operators, police, emergency services and local ward offices.\n\nOfficials say the objective is to reduce congestion during peak periods while keeping priority routes open for ambulances and other essential vehicles. Temporary traffic-control points will be introduced at selected junctions, and bus operators have been asked to increase frequency where passenger demand is expected to rise.\n\nResidents are being advised to allow extra travel time and to check official notices before starting longer journeys. Our report includes reaction from commuters, transport representatives and city officials.\n\n[CUE PKG] The package runs approximately one minute and forty-five seconds. On return, the anchor will introduce the live reporter for the latest conditions from the city center.",demoMedia,"Newsroom · Topic Bug",21,"{\"topic\":\"TOP STORY\",\"strap\":\"CITY OPERATIONS PLAN\"}"),
            ("CITY UPDATE · LIVE REPORTER","LIVE",85,"We can now go live to our reporter in the city center.\n\nReporter: The evening traffic flow is moving steadily at the moment, but officials here say the busiest period is expected tomorrow morning. Crews have already marked several temporary loading zones and information boards are being installed near major intersections. Police say the changes are designed to keep through-traffic moving while protecting pedestrian access around the busiest public areas.\n\nWe spoke with commuters who welcomed the additional buses but said clear information will be essential if routes change at short notice. The city says updates will be posted through official channels throughout the day.\n\nAnchor question: What should people watch for first thing tomorrow?\n\nReporter answer: The main advice is to check route notices before leaving home, avoid unnecessary stops near the controlled junctions and allow additional travel time during the morning peak.\n\nBack to you in the studio.","","Newsroom · Reporter Two Box",22,"{\"reporter\":\"Aarav Sharma\",\"location\":\"KATHMANDU CITY CENTER\",\"status\":\"LIVE\"}"),
            ("PUBLIC SERVICE · VO","VO",50,"Authorities have also issued a public-service reminder for residents using municipal offices this week. Several counters will open earlier than usual, and selected services will continue through the lunch period to reduce queues.\n\nPeople are encouraged to bring complete identification and application documents before arriving. Officials say incomplete paperwork remains one of the main causes of repeat visits.\n\nThe city has also expanded its telephone information line and online appointment system. These pictures show staff preparing the additional counters earlier today.",demoMedia,"Newsroom · Live Locator",23,"{\"location\":\"CIVIC SERVICE CENTER\",\"status\":\"EXTENDED HOURS\"}"),
            ("INTERVIEW · TRANSPORT","SOT",60,"We asked a transport representative whether operators have enough vehicles and staff to meet the expected increase in passenger demand.\n\n[SOT IN] We have adjusted the roster for the morning and evening peaks, and reserve vehicles will be available if particular routes become crowded. Our control room will monitor passenger loads and work with the city traffic team throughout the day.\n\n[SOT OUT]\n\nThe representative added that passengers should use designated stops and avoid boarding outside marked areas, where temporary traffic controls may make stopping unsafe.",demoMedia,"Newsroom · Quote Fullframe",24,"{\"quote\":\"Reserve vehicles will be available during the peak periods\",\"source\":\"CITY TRANSPORT OPERATIONS\"}"),
            ("BUSINESS · MARKETS","CG",55,"Turning to business, regional markets finished the session mixed. Technology and consumer shares gained in several markets while energy stocks were softer. Analysts said investors remained focused on company guidance and the outlook for interest rates.\n\nIn the domestic market, banking and telecommunications shares were among the most actively traded. The currency remained within its recent range and commodity prices were little changed in late trading.\n\nOur market graphic summarizes the main indicators. The figures in this demonstration are sample newsroom data and are intended to show the CG and MOS workflow.","","Business · KPI Dashboard",25,"{\"title\":\"MARKET UPDATE\",\"index\":\"18,420\",\"change\":\"+0.6%\",\"volume\":\"4.31M\"}"),
            ("INTERNATIONAL · ROUNDUP","ANCHOR",65,"Internationally, diplomatic talks continued today on several regional security and trade issues. Officials from participating governments said technical teams would remain in session overnight, with further meetings expected tomorrow.\n\nElsewhere, severe weather disrupted transport in parts of the region, leading to flight delays and temporary road closures. Emergency agencies advised travelers to confirm schedules before departure.\n\nAnd in technology, several major companies announced new investments in data centers and cloud infrastructure, citing continued demand for digital services and artificial-intelligence workloads.","","News · Top Stories Stack",26,"{\"headline\":\"INTERNATIONAL ROUNDUP\",\"item1\":\"Diplomatic talks continue\",\"item2\":\"Weather disrupts travel\",\"item3\":\"New technology investment\"}"),
            ("SPORT · LEAGUE PREVIEW","PKG",75,"In sport, teams are completing final preparations for this weekend's national league fixtures. Coaches say the congested schedule will test squad depth, with several clubs expected to rotate players.\n\nThe defending champions trained behind closed doors this afternoon while their opponents held an open session for supporters. Both managers emphasized discipline and set-piece organization ahead of the match.\n\nOur sports team also looks at the latest cricket preparations and the upcoming basketball schedule.\n\n[CUE SPORTS PKG]",demoMedia,"Sports · Fixture Card",27,"{\"home\":\"KATHMANDU\",\"away\":\"LALITPUR\",\"time\":\"18:30\",\"venue\":\"NATIONAL STADIUM\"}"),
            ("ENTERTAINMENT · CULTURE","VO",50,"A new cultural program opened this evening with music, film and visual-art events scheduled across several venues. Organizers say the program is designed to give emerging artists more opportunities to present work alongside established performers.\n\nThe opening event featured live music and a short film showcase. Additional performances and workshops will continue through the weekend, with several free daytime sessions for students and families.\n\nOrganizers expect strong attendance and recommend advance booking for the smaller venues.",demoMedia,"Entertainment · Weekend Guide",28,"{\"title\":\"WEEKEND CULTURE GUIDE\",\"line1\":\"Music · Film · Art\",\"line2\":\"Events through Sunday\"}"),
            ("WEATHER · FORECAST","PKG",70,"Now the weather. Conditions remain generally settled in the valley this evening, with some cloud developing overnight. Temperatures will ease after sunset before rising again during the late morning.\n\nTomorrow is expected to begin mostly dry, although isolated showers are possible over higher ground later in the day. Winds should remain light to moderate. Visibility may briefly reduce around dawn in low-lying areas.\n\nThe extended outlook keeps similar conditions for the next several days, with a gradual increase in afternoon cloud. Our forecast graphic shows sample temperatures and conditions for the demonstration.",demoMedia,"Weather · Crystal Current Conditions",29,"{\"city\":\"KATHMANDU\",\"temp\":\"23°\",\"condition\":\"PARTLY CLOUDY\",\"high\":\"27°\",\"low\":\"16°\"}"),
            ("COMING UP · PROMO","PROMO",30,"Still ahead after the break: a closer look at tomorrow's transport changes, reaction from commuters, and the latest sports fixtures. We will also show you how the Kashtrix newsroom can attach graphics, media and structured MOS data to each story before publishing the rundown to playout.","","Promo · Coming Soon",30,"{\"title\":\"COMING UP\",\"line1\":\"Transport update\",\"line2\":\"Sport · Weather · Technology\"}"),
            ("CLOSE","ANCHOR",35,"Those are the main stories from Kashtrix Demo News. The complete rundown you have just seen was created in the NRCS, published to the playout database, shared with the prompter and exported as MOS XML with media and CG items.\n\nOur next bulletin is scheduled for the top of the hour. You can continue to follow updates through the newsroom workflow and channel controller.\n\nFrom the entire team, thank you for watching and good night.","","Newsroom · Timeline Card",31,"{\"title\":\"KASHTRIX DEMO NEWS\",\"subtitle\":\"END OF BULLETIN\"}")
        };
        foreach (var row in rows)
        {
            var story = CreateStoryDraft(row.Item1) with { Presenter="DEMO ANCHOR", Writer="News Desk", Editor="Output Editor", Status="READY", Priority="NORMAL", Category="NEWS", Language="en", StoryType=row.Item2, DurationSeconds=row.Item3, Body=row.Item4, MediaPath=row.Item5, CgTemplate=row.Item6, CgLayer=row.Item7, CgDataJson=row.Item8, Notes="FIX48 professional demo · MOS/CG/Playout/Prompter compatible" };
            SaveStory(story); AddStoryToRundown(story.Id, rundown.Id);
        }
        var assignment = CreateAssignment() with { Title="Kashtrix Demo News", Angle="Validate full newsroom → MOS/CG → playout → prompter workflow", Assignee="Demo Producer", Desk="NEWS", Location="STUDIO-A", Status="READY", Priority="HIGH", DueUtc=DateTime.UtcNow.AddMinutes(30), Notes="Use LOAD PROFESSIONAL DEMO, publish to Playout and MOS, then open Prompter." };
        SaveAssignment(assignment);
        return rundown;
    }


    public NrcsRundown RebuildProfessionalDemo()
    {
        const string demoName = "KASHTRIX DEMO NEWS · 18:00";
        var existing = ListRundowns().FirstOrDefault(x => x.Name == demoName);
        if (existing is not null)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            var storyIds = new List<string>();
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT story_id FROM nrcs_rundown_items WHERE rundown_id=$r";
                q.Parameters.AddWithValue("$r", existing.Id);
                using var reader = q.ExecuteReader();
                while (reader.Read()) storyIds.Add(reader.GetString(0));
            }

            foreach (var sql in new[]
            {
                "DELETE FROM nrcs_live_state WHERE rundown_id=$r",
                "DELETE FROM nrcs_rundown_items WHERE rundown_id=$r",
                "DELETE FROM nrcs_stories WHERE rundown_id=$r",
                "DELETE FROM nrcs_rundowns WHERE id=$r"
            })
            {
                using var d = c.CreateCommand();
                d.Transaction = tx;
                d.CommandText = sql;
                d.Parameters.AddWithValue("$r", existing.Id);
                d.ExecuteNonQuery();
            }

            foreach (var storyId in storyIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var refs = c.CreateCommand();
                refs.Transaction = tx;
                refs.CommandText = "SELECT COUNT(*) FROM nrcs_rundown_items WHERE story_id=$id";
                refs.Parameters.AddWithValue("$id", storyId);
                var referenceCount = Convert.ToInt32(refs.ExecuteScalar());
                if (referenceCount != 0) continue;

                using var versions = c.CreateCommand();
                versions.Transaction = tx;
                versions.CommandText = "DELETE FROM nrcs_story_versions WHERE story_id=$id";
                versions.Parameters.AddWithValue("$id", storyId);
                versions.ExecuteNonQuery();

                using var story = c.CreateCommand();
                story.Transaction = tx;
                story.CommandText = "DELETE FROM nrcs_story_bank WHERE id=$id";
                story.Parameters.AddWithValue("$id", storyId);
                story.ExecuteNonQuery();
            }

            using (var assignments = c.CreateCommand())
            {
                assignments.Transaction = tx;
                assignments.CommandText = "DELETE FROM nrcs_assignments WHERE title='Kashtrix Demo News'";
                assignments.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return SeedProfessionalDemo();
    }

    private static string ResolveDemoMediaPath()
    {
        var relative = Path.Combine("demos", "gfx", "video", "kashtrix-video-layer.mp4");
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, relative),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "BroadcastPlayout.App", relative)
        };

        try
        {
            var marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout", "suite-root.txt");
            if (File.Exists(marker))
            {
                var suiteRoot = File.ReadAllText(marker).Trim();
                if (!string.IsNullOrWhiteSpace(suiteRoot))
                    candidates.Add(Path.Combine(suiteRoot, "src", "BroadcastPlayout.App", relative));
            }
        }
        catch { }

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static void TryAlter(SqliteConnection c,string sql){try{using var cmd=c.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();}catch(SqliteException ex) when(ex.Message.Contains("duplicate column",StringComparison.OrdinalIgnoreCase)){} }

    private static DateTime ParseUtc(string value) => DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : DateTime.UtcNow;
    private static string Normalize(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static string ValidJson(string? value) { var text = string.IsNullOrWhiteSpace(value) ? "{}" : value.Trim(); try { using var _ = JsonDocument.Parse(text); return text; } catch { return "{}"; } }
}
