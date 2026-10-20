using System.IO;
using System.Text.Json;
using BroadcastPlayout.Models;
using Microsoft.Data.Sqlite;

namespace BroadcastPlayout.Services;

/// <summary>Local SQLite store for rundown recovery, sources, outputs, schedules and CG state.</summary>
public sealed class BroadcastDatabase
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public static string DatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KashtrixPlayout", "kashtrix.db");

    private string ConnectionString => $"Data Source={DatabasePath};Mode=ReadWriteCreate;Cache=Shared";

    public BroadcastDatabase()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS playlist (
                id TEXT PRIMARY KEY,
                seq INTEGER NOT NULL,
                event_type TEXT NOT NULL,
                title TEXT NOT NULL,
                file_path TEXT NOT NULL,
                in_ticks INTEGER NOT NULL,
                out_ticks INTEGER NOT NULL,
                codec TEXT NOT NULL,
                video_format TEXT NOT NULL,
                status TEXT NOT NULL,
                source_kind TEXT NOT NULL DEFAULT 'File',
                input_format TEXT NOT NULL DEFAULT '',
                input_options TEXT NOT NULL DEFAULT '',
                video_device TEXT NOT NULL DEFAULT '',
                audio_device TEXT NOT NULL DEFAULT '',
                source_duration_ticks INTEGER NOT NULL DEFAULT 0,
                source_fps REAL NOT NULL DEFAULT 25,
                is_live INTEGER NOT NULL DEFAULT 0,
                capture_width INTEGER NOT NULL DEFAULT 1920,
                capture_height INTEGER NOT NULL DEFAULT 1080,
                block_name TEXT NOT NULL DEFAULT '',
                audio_gain_percent INTEGER NOT NULL DEFAULT 100,
                audio_muted INTEGER NOT NULL DEFAULT 0,
                item_json TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS state (
                key TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        // Safe migration for databases created by previous Kashtrix builds.
        EnsureColumn(connection, "playlist", "source_kind", "TEXT NOT NULL DEFAULT 'File'");
        EnsureColumn(connection, "playlist", "input_format", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "playlist", "input_options", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "playlist", "video_device", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "playlist", "audio_device", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "playlist", "source_duration_ticks", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "playlist", "source_fps", "REAL NOT NULL DEFAULT 25");
        EnsureColumn(connection, "playlist", "is_live", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "playlist", "capture_width", "INTEGER NOT NULL DEFAULT 1920");
        EnsureColumn(connection, "playlist", "capture_height", "INTEGER NOT NULL DEFAULT 1080");
        EnsureColumn(connection, "playlist", "block_name", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "playlist", "audio_gain_percent", "INTEGER NOT NULL DEFAULT 100");
        EnsureColumn(connection, "playlist", "audio_muted", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "playlist", "item_json", "TEXT NOT NULL DEFAULT ''");
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var reader = check.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    public List<PlaylistItem> LoadPlaylist()
    {
        var result = new List<PlaylistItem>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, seq, event_type, title, file_path, in_ticks, out_ticks, codec, video_format, status,
                   source_kind, input_format, input_options, video_device, audio_device,
                   source_duration_ticks, source_fps, is_live, capture_width, capture_height, block_name, audio_gain_percent, audio_muted, item_json
            FROM playlist ORDER BY seq
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = Guid.TryParse(reader.GetString(0), out var parsed) ? parsed : Guid.NewGuid();
            var inPoint = TimeSpan.FromTicks(reader.GetInt64(5));
            var outPoint = TimeSpan.FromTicks(reader.GetInt64(6));
            var sourceDurationTicks = reader.GetInt64(15);
            var legacy = new PlaylistItem
            {
                Id = id,
                Sequence = reader.GetInt32(1),
                EventType = reader.GetString(2),
                Title = reader.GetString(3),
                FilePath = reader.GetString(4),
                InPoint = inPoint,
                OutPoint = outPoint,
                Codec = reader.GetString(7),
                VideoFormat = reader.GetString(8),
                Status = reader.GetString(9) is "On Air" or "Cued" ? "Ready" : reader.GetString(9),
                SourceKind = reader.GetString(10),
                InputFormat = reader.GetString(11),
                InputOptions = reader.GetString(12),
                VideoDevice = reader.GetString(13),
                AudioDevice = reader.GetString(14),
                SourceDuration = sourceDurationTicks > 0 ? TimeSpan.FromTicks(sourceDurationTicks) : outPoint,
                SourceFrameRate = Convert.ToDouble(reader.GetValue(16), System.Globalization.CultureInfo.InvariantCulture),
                IsLiveSource = reader.GetInt64(17) != 0,
                CaptureWidth = reader.GetInt32(18),
                CaptureHeight = reader.GetInt32(19),
                BlockName = reader.FieldCount > 20 ? reader.GetString(20) : string.Empty,
                AudioGainPercent = reader.FieldCount > 21 ? reader.GetInt32(21) : 100,
                AudioMuted = reader.FieldCount > 22 && reader.GetInt64(22) != 0
            };

            // FIX27 stores the complete PlaylistItem JSON alongside the stable legacy columns.
            // This preserves per-item audio routing/delay/track, video/aspect/interlace/decoder,
            // EPG metadata, row colors, thumbnails and multipart trims across application restarts.
            PlaylistItem restored = legacy;
            if (reader.FieldCount > 23 && !reader.IsDBNull(23))
            {
                var json = reader.GetString(23);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    try { restored = JsonSerializer.Deserialize<PlaylistItem>(json, Json) ?? legacy; } catch { restored = legacy; }
                }
            }
            if (restored.Status is "On Air" or "Cued") restored.Status = "Ready";
            result.Add(restored);
        }
        return result;
    }

    public void SavePlaylist(IEnumerable<PlaylistItem> items)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM playlist";
            clear.ExecuteNonQuery();
        }

        foreach (var item in items)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO playlist(id, seq, event_type, title, file_path, in_ticks, out_ticks, codec, video_format, status,
                                     source_kind, input_format, input_options, video_device, audio_device,
                                     source_duration_ticks, source_fps, is_live, capture_width, capture_height, block_name, audio_gain_percent, audio_muted, item_json)
                VALUES($id,$seq,$event,$title,$file,$in,$out,$codec,$format,$status,
                       $sourcekind,$inputformat,$inputoptions,$videodevice,$audiodevice,$sourceduration,$sourcefps,$islive,$capturewidth,$captureheight,$blockname,$audiogain,$audiomuted,$itemjson)
                """;
            command.Parameters.AddWithValue("$id", item.Id.ToString("D"));
            command.Parameters.AddWithValue("$seq", item.Sequence);
            command.Parameters.AddWithValue("$event", item.EventType ?? "CLIP");
            command.Parameters.AddWithValue("$title", item.Title ?? "");
            command.Parameters.AddWithValue("$file", item.FilePath ?? "");
            command.Parameters.AddWithValue("$in", item.InPoint.Ticks);
            command.Parameters.AddWithValue("$out", item.OutPoint.Ticks);
            command.Parameters.AddWithValue("$codec", item.Codec ?? "");
            command.Parameters.AddWithValue("$format", item.VideoFormat ?? "");
            command.Parameters.AddWithValue("$status", item.Status ?? "Ready");
            command.Parameters.AddWithValue("$sourcekind", item.SourceKind ?? "File");
            command.Parameters.AddWithValue("$inputformat", item.InputFormat ?? "");
            command.Parameters.AddWithValue("$inputoptions", item.InputOptions ?? "");
            command.Parameters.AddWithValue("$videodevice", item.VideoDevice ?? "");
            command.Parameters.AddWithValue("$audiodevice", item.AudioDevice ?? "");
            command.Parameters.AddWithValue("$sourceduration", item.SourceDuration.Ticks);
            command.Parameters.AddWithValue("$sourcefps", item.SourceFrameRate);
            command.Parameters.AddWithValue("$islive", item.IsLiveSource ? 1 : 0);
            command.Parameters.AddWithValue("$capturewidth", item.CaptureWidth);
            command.Parameters.AddWithValue("$captureheight", item.CaptureHeight);
            command.Parameters.AddWithValue("$blockname", item.BlockName ?? "");
            command.Parameters.AddWithValue("$audiogain", item.AudioGainPercent);
            command.Parameters.AddWithValue("$audiomuted", item.AudioMuted ? 1 : 0);
            command.Parameters.AddWithValue("$itemjson", JsonSerializer.Serialize(item, Json));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public T LoadState<T>(string key, T fallback)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM state WHERE key=$key";
            command.Parameters.AddWithValue("$key", key);
            var value = command.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            return JsonSerializer.Deserialize<T>(value, Json) ?? fallback;
        }
        catch { return fallback; }
    }

    public void SaveState<T>(string key, T value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO state(key,json,updated_utc) VALUES($key,$json,$utc)
            ON CONFLICT(key) DO UPDATE SET json=excluded.json, updated_utc=excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, Json));
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}
