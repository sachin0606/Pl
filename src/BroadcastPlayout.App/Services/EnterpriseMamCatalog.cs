using System.Security.Cryptography;
using BroadcastPlayout.Models;
using Microsoft.Data.Sqlite;

namespace BroadcastPlayout.Services;

/// <summary>
/// Persistent MAM catalog used by Ingest, MAM, Playout and the API gateway. The database stores
/// stable asset GUIDs independently of filenames, version history, master/proxy/archive locations,
/// SHA-256 integrity, approval/rights workflow and an immutable audit trail.
/// </summary>
public sealed class EnterpriseMamCatalog
{
    public sealed class Asset
    {
        public Guid AssetId { get; set; }
        public Guid? MasterAssetId { get; set; }
        public int Version { get; set; } = 1;
        public string Path { get; set; } = "";
        public string ProxyPath { get; set; } = "";
        public string ArchivePath { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long SizeBytes { get; set; }
        public DateTime ModifiedUtc { get; set; }
        public string Title { get; set; } = "";
        public string ApprovalState { get; set; } = "Draft";
        public DateTime? RightsStartUtc { get; set; }
        public DateTime? RightsEndUtc { get; set; }
        public string QcStatus { get; set; } = "Not checked";
        public string StorageState { get; set; } = "Online";
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public int DuplicateCount { get; set; }
        public string AssetIdText => AssetId.ToString("D");
        public string RightsSummary => RightsStartUtc is null && RightsEndUtc is null ? "UNRESTRICTED" : $"{RightsStartUtc?.ToLocalTime():yyyy-MM-dd} → {RightsEndUtc?.ToLocalTime():yyyy-MM-dd}";
        public string IntegritySummary => string.IsNullOrWhiteSpace(Sha256) ? "NOT VERIFIED" : $"SHA-256 {Sha256[..Math.Min(16, Sha256.Length)]}… · {DuplicateCount} duplicate(s)";
    }

    public sealed record HistoryEntry(DateTime Utc, string Action, string Detail, string Actor);

    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KashtrixPlayout", "mam.db");

    public static string ResolveDatabasePath()
    {
        try
        {
            var configured = SettingsStore.Load().MamDatabasePath;
            return string.IsNullOrWhiteSpace(configured) ? DefaultDatabasePath : Path.GetFullPath(configured);
        }
        catch { return DefaultDatabasePath; }
    }

    private readonly string _databasePath;
    private string ConnectionString => $"Data Source={_databasePath};Mode=ReadWriteCreate;Cache=Shared";

    public EnterpriseMamCatalog(string? databasePath = null)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath) ? ResolveDatabasePath() : Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS assets(
                asset_id TEXT PRIMARY KEY,
                master_asset_id TEXT NULL,
                version INTEGER NOT NULL DEFAULT 1,
                path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                proxy_path TEXT NOT NULL DEFAULT '',
                archive_path TEXT NOT NULL DEFAULT '',
                sha256 TEXT NOT NULL DEFAULT '',
                size_bytes INTEGER NOT NULL DEFAULT 0,
                modified_utc TEXT NOT NULL DEFAULT '',
                title TEXT NOT NULL DEFAULT '',
                approval_state TEXT NOT NULL DEFAULT 'Draft',
                rights_start_utc TEXT NULL,
                rights_end_utc TEXT NULL,
                qc_status TEXT NOT NULL DEFAULT 'Not checked',
                storage_state TEXT NOT NULL DEFAULT 'Online',
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_assets_sha256 ON assets(sha256);
            CREATE INDEX IF NOT EXISTS ix_assets_master ON assets(master_asset_id);
            CREATE TABLE IF NOT EXISTS asset_locations(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                asset_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                uri TEXT NOT NULL,
                online INTEGER NOT NULL DEFAULT 1,
                updated_utc TEXT NOT NULL,
                UNIQUE(asset_id, kind, uri),
                FOREIGN KEY(asset_id) REFERENCES assets(asset_id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS asset_versions(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                asset_id TEXT NOT NULL,
                version INTEGER NOT NULL,
                path TEXT NOT NULL,
                sha256 TEXT NOT NULL DEFAULT '',
                size_bytes INTEGER NOT NULL DEFAULT 0,
                modified_utc TEXT NOT NULL DEFAULT '',
                created_utc TEXT NOT NULL,
                UNIQUE(asset_id, version),
                FOREIGN KEY(asset_id) REFERENCES assets(asset_id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS asset_history(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                asset_id TEXT NOT NULL,
                utc TEXT NOT NULL,
                action TEXT NOT NULL,
                detail TEXT NOT NULL DEFAULT '',
                actor TEXT NOT NULL DEFAULT 'Kashtrix',
                FOREIGN KEY(asset_id) REFERENCES assets(asset_id) ON DELETE CASCADE
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(ConnectionString);
        c.Open();
        return c;
    }

    public Asset RegisterFile(string path, MediaAssetMetadata? metadata = null, string actor = "MAM scan")
    {
        path = NormalizeExistingPath(path);
        var info = new FileInfo(path);
        var now = DateTime.UtcNow;
        using var c = Open();
        using var tx = c.BeginTransaction();
        var existing = GetByPath(c, path, tx);
        if (existing is null)
        {
            var asset = new Asset
            {
                AssetId = Guid.NewGuid(), Path = path, Version = 1, SizeBytes = info.Length,
                ModifiedUtc = info.LastWriteTimeUtc, Title = metadata?.Title ?? Path.GetFileNameWithoutExtension(path),
                CreatedUtc = now, UpdatedUtc = now
            };
            InsertAsset(c, tx, asset);
            InsertVersion(c, tx, asset);
            UpsertLocation(c, tx, asset.AssetId, "Master", path, true);
            AddHistory(c, tx, asset.AssetId, "REGISTER", $"Registered master file {path}", actor);
            tx.Commit();
            return asset;
        }

        var changed = existing.SizeBytes != info.Length || Math.Abs((existing.ModifiedUtc - info.LastWriteTimeUtc).TotalSeconds) > 1;
        if (changed)
        {
            existing.Version = Math.Max(1, existing.Version + 1);
            existing.SizeBytes = info.Length;
            existing.ModifiedUtc = info.LastWriteTimeUtc;
            existing.Sha256 = "";
            existing.UpdatedUtc = now;
            UpdateAssetCore(c, tx, existing);
            InsertVersion(c, tx, existing);
            AddHistory(c, tx, existing.AssetId, "NEW_VERSION", $"Version {existing.Version} detected at {path}", actor);
        }
        else if (metadata is not null && !string.IsNullOrWhiteSpace(metadata.Title) && !metadata.Title.Equals(existing.Title, StringComparison.Ordinal))
        {
            existing.Title = metadata.Title;
            existing.UpdatedUtc = now;
            UpdateAssetCore(c, tx, existing);
        }
        UpsertLocation(c, tx, existing.AssetId, "Master", path, true);
        tx.Commit();
        return existing;
    }

    public async Task<Asset> RegisterAndVerifyAsync(string path, MediaAssetMetadata? metadata = null, string actor = "Kashtrix", CancellationToken cancellationToken = default)
    {
        var asset = RegisterFile(path, metadata, actor);
        return await VerifyIntegrityAsync(asset.AssetId, cancellationToken).ConfigureAwait(false);
    }

    public Asset? GetByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { path = Path.GetFullPath(path); } catch { }
        using var c = Open();
        return GetByPath(c, path, null);
    }

    public Asset? Get(Guid assetId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = AssetSelect + " WHERE asset_id=$id";
        cmd.Parameters.AddWithValue("$id", assetId.ToString("D"));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadAsset(r, c) : null;
    }

    public IReadOnlyList<Asset> List(int limit = 5000, string? search = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = AssetSelect + (string.IsNullOrWhiteSpace(search) ? "" : " WHERE title LIKE $q OR path LIKE $q OR sha256 LIKE $q") + " ORDER BY updated_utc DESC LIMIT $limit";
        if (!string.IsNullOrWhiteSpace(search)) cmd.Parameters.AddWithValue("$q", "%" + search.Trim() + "%");
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100000));
        using var r = cmd.ExecuteReader();
        var raw = new List<Asset>();
        while (r.Read()) raw.Add(ReadAssetWithoutDuplicates(r));
        r.Close();
        foreach (var a in raw) a.DuplicateCount = CountDuplicates(c, a.Sha256, a.AssetId);
        return raw;
    }

    public void UpdateWorkflow(Asset asset, string actor = "MAM operator")
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        asset.ApprovalState = string.IsNullOrWhiteSpace(asset.ApprovalState) ? "Draft" : asset.ApprovalState.Trim();
        asset.UpdatedUtc = DateTime.UtcNow;
        UpdateAssetCore(c, tx, asset);
        AddHistory(c, tx, asset.AssetId, "WORKFLOW", $"Approval={asset.ApprovalState}; rights={asset.RightsStartUtc:o}..{asset.RightsEndUtc:o}; QC={asset.QcStatus}", actor);
        tx.Commit();
    }

    public void SetQc(Guid assetId, string status, string detail = "", string actor = "QC")
    {
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? "Not checked" : status.Trim();
        var normalizedDetail = string.IsNullOrWhiteSpace(detail) ? normalizedStatus : normalizedStatus + " - " + detail.Trim();
        using var c = Open(); using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE assets SET qc_status=$s,updated_utc=$u WHERE asset_id=$id";
        cmd.Parameters.AddWithValue("$s", normalizedStatus); cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$id", assetId.ToString("D")); cmd.ExecuteNonQuery();
        AddHistory(c, tx, assetId, "QC", normalizedDetail, actor); tx.Commit();
    }

    public void LinkProxy(Guid assetId, string proxyPath, string actor = "Proxy generator")
    {
        proxyPath = NormalizeExistingPath(proxyPath);
        using var c = Open(); using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE assets SET proxy_path=$p,updated_utc=$u WHERE asset_id=$id";
        cmd.Parameters.AddWithValue("$p", proxyPath); cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$id", assetId.ToString("D")); cmd.ExecuteNonQuery();
        UpsertLocation(c, tx, assetId, "Proxy", proxyPath, true);
        AddHistory(c, tx, assetId, "PROXY_LINK", proxyPath, actor); tx.Commit();
    }

    public void LinkMaster(Guid proxyAssetId, Guid masterAssetId, string actor = "MAM operator")
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE assets SET master_asset_id=$master,updated_utc=$u WHERE asset_id=$id";
        cmd.Parameters.AddWithValue("$master", masterAssetId.ToString("D")); cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$id", proxyAssetId.ToString("D")); cmd.ExecuteNonQuery();
        AddHistory(c, tx, proxyAssetId, "MASTER_LINK", masterAssetId.ToString("D"), actor); tx.Commit();
    }

    public async Task<Asset> VerifyIntegrityAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        var asset = Get(assetId) ?? throw new InvalidOperationException("MAM asset not found.");
        if (!File.Exists(asset.Path)) throw new FileNotFoundException("Master file is offline.", asset.Path);
        var checksum = await ComputeSha256Async(asset.Path, cancellationToken).ConfigureAwait(false);
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx; cmd.CommandText = "UPDATE assets SET sha256=$h,updated_utc=$u WHERE asset_id=$id";
            cmd.Parameters.AddWithValue("$h", checksum); cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$id", assetId.ToString("D")); cmd.ExecuteNonQuery();
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx; cmd.CommandText = "UPDATE asset_versions SET sha256=$h WHERE asset_id=$id AND version=$v";
            cmd.Parameters.AddWithValue("$h", checksum); cmd.Parameters.AddWithValue("$id", assetId.ToString("D")); cmd.Parameters.AddWithValue("$v", asset.Version); cmd.ExecuteNonQuery();
        }
        AddHistory(c, tx, assetId, "VERIFY_SHA256", checksum, "Integrity service"); tx.Commit();
        return Get(assetId)!;
    }

    public async Task<string> ArchiveAsync(Guid assetId, string? archiveRoot = null, CancellationToken cancellationToken = default)
    {
        var asset = Get(assetId) ?? throw new InvalidOperationException("MAM asset not found.");
        if (!File.Exists(asset.Path)) throw new FileNotFoundException("Master file is offline.", asset.Path);
        if (string.IsNullOrWhiteSpace(archiveRoot))
        {
            archiveRoot = SettingsStore.Load().MamArchiveRoot;
            if (string.IsNullOrWhiteSpace(archiveRoot)) archiveRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Kashtrix Archive");
        }
        var folder = Path.Combine(Path.GetFullPath(archiveRoot), asset.AssetId.ToString("N"));
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, Path.GetFileName(asset.Path));
        await CopyFileAsync(asset.Path, destination, cancellationToken).ConfigureAwait(false);
        var checksum = await ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(asset.Sha256) && !asset.Sha256.Equals(checksum, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Archive checksum does not match the verified master checksum.");
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx; cmd.CommandText = "UPDATE assets SET archive_path=$p,storage_state='Online + Archive',updated_utc=$u WHERE asset_id=$id";
            cmd.Parameters.AddWithValue("$p", destination); cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$id", assetId.ToString("D")); cmd.ExecuteNonQuery();
        }
        UpsertLocation(c, tx, assetId, "Archive", destination, true); AddHistory(c, tx, assetId, "ARCHIVE", destination, "Archive service"); tx.Commit();
        return destination;
    }

    public async Task<string> RestoreAsync(Guid assetId, string? targetPath = null, CancellationToken cancellationToken = default)
    {
        var asset = Get(assetId) ?? throw new InvalidOperationException("MAM asset not found.");
        if (string.IsNullOrWhiteSpace(asset.ArchivePath) || !File.Exists(asset.ArchivePath)) throw new FileNotFoundException("Archive copy is offline.", asset.ArchivePath);
        targetPath = string.IsNullOrWhiteSpace(targetPath) ? asset.Path : Path.GetFullPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await CopyFileAsync(asset.ArchivePath, targetPath, cancellationToken).ConfigureAwait(false);
        using var c = Open(); using var tx = c.BeginTransaction();
        UpsertLocation(c, tx, assetId, "Master", targetPath, true); AddHistory(c, tx, assetId, "RESTORE", targetPath, "Restore service"); tx.Commit();
        return targetPath;
    }

    public IReadOnlyList<HistoryEntry> History(Guid assetId, int limit = 200)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT utc,action,detail,actor FROM asset_history WHERE asset_id=$id ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$id", assetId.ToString("D")); cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        using var r = cmd.ExecuteReader(); var result = new List<HistoryEntry>();
        while (r.Read()) result.Add(new HistoryEntry(ParseUtc(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3)));
        return result;
    }

    public void AssertMayUse(string path, DateTime? utc = null)
    {
        var asset = GetByPath(path); if (asset is null) return;
        var now = utc ?? DateTime.UtcNow;
        if (asset.RightsStartUtc is DateTime start && now < start) throw new InvalidOperationException($"MAM rights window has not started for '{asset.Title}' (starts {start.ToLocalTime():yyyy-MM-dd HH:mm}).");
        if (asset.RightsEndUtc is DateTime end && now > end) throw new InvalidOperationException($"MAM rights expired for '{asset.Title}' ({end.ToLocalTime():yyyy-MM-dd HH:mm}).");
        if (asset.ApprovalState.Equals("Rejected", StringComparison.OrdinalIgnoreCase) || asset.ApprovalState.Equals("Blocked", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"MAM workflow blocks '{asset.Title}' because approval state is {asset.ApprovalState}.");
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            sha.TransformBlock(buffer, 0, read, null, 0);
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static async Task CopyFileAsync(string source, string target, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 1024 * 1024, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private const string AssetSelect = "SELECT asset_id,master_asset_id,version,path,proxy_path,archive_path,sha256,size_bytes,modified_utc,title,approval_state,rights_start_utc,rights_end_utc,qc_status,storage_state,created_utc,updated_utc FROM assets";

    private static Asset? GetByPath(SqliteConnection c, string path, SqliteTransaction? tx)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = AssetSelect + " WHERE path=$p"; cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader(); if (!r.Read()) return null; var a = ReadAssetWithoutDuplicates(r); r.Close(); a.DuplicateCount = CountDuplicates(c, a.Sha256, a.AssetId, tx); return a;
    }

    private static Asset ReadAsset(SqliteDataReader r, SqliteConnection c) { var a = ReadAssetWithoutDuplicates(r); r.Close(); a.DuplicateCount = CountDuplicates(c, a.Sha256, a.AssetId); return a; }
    private static Asset ReadAssetWithoutDuplicates(SqliteDataReader r) => new()
    {
        AssetId = Guid.Parse(r.GetString(0)), MasterAssetId = r.IsDBNull(1) || string.IsNullOrWhiteSpace(r.GetString(1)) ? null : Guid.Parse(r.GetString(1)),
        Version = r.GetInt32(2), Path = r.GetString(3), ProxyPath = r.GetString(4), ArchivePath = r.GetString(5), Sha256 = r.GetString(6), SizeBytes = r.GetInt64(7),
        ModifiedUtc = ParseUtc(r.GetString(8)), Title = r.GetString(9), ApprovalState = r.GetString(10), RightsStartUtc = r.IsDBNull(11) ? null : ParseNullableUtc(r.GetString(11)), RightsEndUtc = r.IsDBNull(12) ? null : ParseNullableUtc(r.GetString(12)),
        QcStatus = r.GetString(13), StorageState = r.GetString(14), CreatedUtc = ParseUtc(r.GetString(15)), UpdatedUtc = ParseUtc(r.GetString(16))
    };

    private static int CountDuplicates(SqliteConnection c, string sha, Guid id, SqliteTransaction? tx = null)
    {
        if (string.IsNullOrWhiteSpace(sha)) return 0; using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM assets WHERE sha256=$h AND asset_id<>$id"; cmd.Parameters.AddWithValue("$h", sha); cmd.Parameters.AddWithValue("$id", id.ToString("D")); return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void InsertAsset(SqliteConnection c, SqliteTransaction tx, Asset a)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """INSERT INTO assets(asset_id,master_asset_id,version,path,proxy_path,archive_path,sha256,size_bytes,modified_utc,title,approval_state,rights_start_utc,rights_end_utc,qc_status,storage_state,created_utc,updated_utc) VALUES($id,$master,$v,$path,$proxy,$archive,$sha,$size,$modified,$title,$approval,$rs,$re,$qc,$storage,$created,$updated)""";
        BindAsset(cmd, a); cmd.ExecuteNonQuery();
    }

    private static void UpdateAssetCore(SqliteConnection c, SqliteTransaction tx, Asset a)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """UPDATE assets SET master_asset_id=$master,version=$v,path=$path,proxy_path=$proxy,archive_path=$archive,sha256=$sha,size_bytes=$size,modified_utc=$modified,title=$title,approval_state=$approval,rights_start_utc=$rs,rights_end_utc=$re,qc_status=$qc,storage_state=$storage,updated_utc=$updated WHERE asset_id=$id""";
        BindAsset(cmd, a); cmd.ExecuteNonQuery();
    }

    private static void BindAsset(SqliteCommand cmd, Asset a)
    {
        cmd.Parameters.AddWithValue("$id", a.AssetId.ToString("D")); cmd.Parameters.AddWithValue("$master", a.MasterAssetId?.ToString("D") ?? (object)DBNull.Value); cmd.Parameters.AddWithValue("$v", a.Version);
        cmd.Parameters.AddWithValue("$path", a.Path); cmd.Parameters.AddWithValue("$proxy", a.ProxyPath ?? ""); cmd.Parameters.AddWithValue("$archive", a.ArchivePath ?? ""); cmd.Parameters.AddWithValue("$sha", a.Sha256 ?? "");
        cmd.Parameters.AddWithValue("$size", a.SizeBytes); cmd.Parameters.AddWithValue("$modified", a.ModifiedUtc.ToString("O")); cmd.Parameters.AddWithValue("$title", a.Title ?? ""); cmd.Parameters.AddWithValue("$approval", a.ApprovalState ?? "Draft");
        cmd.Parameters.AddWithValue("$rs", a.RightsStartUtc?.ToString("O") ?? (object)DBNull.Value); cmd.Parameters.AddWithValue("$re", a.RightsEndUtc?.ToString("O") ?? (object)DBNull.Value); cmd.Parameters.AddWithValue("$qc", a.QcStatus ?? "Not checked"); cmd.Parameters.AddWithValue("$storage", a.StorageState ?? "Online");
        cmd.Parameters.AddWithValue("$created", a.CreatedUtc == default ? DateTime.UtcNow.ToString("O") : a.CreatedUtc.ToString("O")); cmd.Parameters.AddWithValue("$updated", a.UpdatedUtc == default ? DateTime.UtcNow.ToString("O") : a.UpdatedUtc.ToString("O"));
    }

    private static void InsertVersion(SqliteConnection c, SqliteTransaction tx, Asset a)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT OR IGNORE INTO asset_versions(asset_id,version,path,sha256,size_bytes,modified_utc,created_utc) VALUES($id,$v,$p,$h,$s,$m,$u)";
        cmd.Parameters.AddWithValue("$id", a.AssetId.ToString("D")); cmd.Parameters.AddWithValue("$v", a.Version); cmd.Parameters.AddWithValue("$p", a.Path); cmd.Parameters.AddWithValue("$h", a.Sha256 ?? ""); cmd.Parameters.AddWithValue("$s", a.SizeBytes); cmd.Parameters.AddWithValue("$m", a.ModifiedUtc.ToString("O")); cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }

    private static void UpsertLocation(SqliteConnection c, SqliteTransaction tx, Guid id, string kind, string uri, bool online)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO asset_locations(asset_id,kind,uri,online,updated_utc) VALUES($id,$kind,$uri,$online,$u) ON CONFLICT(asset_id,kind,uri) DO UPDATE SET online=$online,updated_utc=$u";
        cmd.Parameters.AddWithValue("$id", id.ToString("D")); cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$uri", uri); cmd.Parameters.AddWithValue("$online", online ? 1 : 0); cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }

    private static void AddHistory(SqliteConnection c, SqliteTransaction tx, Guid id, string action, string detail, string actor)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO asset_history(asset_id,utc,action,detail,actor) VALUES($id,$u,$a,$d,$actor)";
        cmd.Parameters.AddWithValue("$id", id.ToString("D")); cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$a", action); cmd.Parameters.AddWithValue("$d", detail ?? ""); cmd.Parameters.AddWithValue("$actor", actor ?? "Kashtrix"); cmd.ExecuteNonQuery();
    }

    private static string NormalizeExistingPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Media path is required.", nameof(path));
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("Media file was not found.", path); return path;
    }
    private static DateTime ParseUtc(string value) => DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : DateTime.MinValue;
    private static DateTime? ParseNullableUtc(string value) => string.IsNullOrWhiteSpace(value) ? null : ParseUtc(value);
}
