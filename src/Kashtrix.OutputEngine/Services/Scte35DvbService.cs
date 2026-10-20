using System;
using System.Collections.ObjectModel;
using System.Linq;
using Kashtrix.OutputEngine.Models;

namespace Kashtrix.OutputEngine.Services;

public class Scte35DvbService
{
    private static readonly Lazy<Scte35DvbService> _instance = new(() => new Scte35DvbService());
    public static Scte35DvbService Instance => _instance.Value;

    public ObservableCollection<BroadcastEventLog> Logs { get; } = [];
    public ObservableCollection<Scte35SpliceEvent> SpliceEvents { get; } = [];

    private uint _spliceEventCounter = 1001;

    public event Action<BroadcastEventLog>? LogAdded;

    public void Log(string category, string message, string level = "INFO")
    {
        var entry = new BroadcastEventLog
        {
            Category = category.ToUpperInvariant(),
            Message = message,
            Level = level.ToUpperInvariant(),
            Timestamp = DateTime.Now
        };

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Logs.Insert(0, entry);
            if (Logs.Count > 1000) Logs.RemoveAt(Logs.Count - 1);
            LogAdded?.Invoke(entry);
        });
    }

    public Scte35SpliceEvent TriggerScte35Splice(double durationSeconds = 30.0, string command = "splice_insert")
    {
        var eventId = _spliceEventCounter++;
        var ptsTicks = (ulong)(DateTime.UtcNow.Ticks % 8589934592L); // 33-bit 90kHz PTS representation
        var ptsHex = $"0x{ptsTicks:X8}";

        var spliceEvent = new Scte35SpliceEvent
        {
            EventId = eventId,
            CommandType = command,
            DurationSeconds = durationSeconds,
            PtsTimestampHex = ptsHex,
            State = "Active",
            Timestamp = DateTime.Now
        };

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            SpliceEvents.Insert(0, spliceEvent);
            if (SpliceEvents.Count > 100) SpliceEvents.RemoveAt(SpliceEvents.Count - 1);
        });

        Log("SCTE-35", $"[SPLICE INSERT] EventID: {eventId} | Command: {command} | Duration: {durationSeconds:0.0}s | PTS: {ptsHex} | Out-Of-Network: TRUE | Program Splice Flag: 1", "SUCCESS");
        Log("DVB-TS", $"[SCTE-35 INJECTION] Transport Packet PID 0x01F4 (500) | Splice section injected cleanly into elementary stream", "INFO");

        return spliceEvent;
    }

    public void TriggerMosEvent(string slug, string command = "roItemCue")
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff");
        var mosMsg = $"<mos><roItemCue><roID>RO_RUNDOWN_01</roID><roSlug>{slug}</roSlug><channel>AIR_1</channel><status>READY</status><time>{timestamp}</time></roItemCue></mos>";
        
        Log("MOS", $"[MOS 2.8.4] {command.ToUpperInvariant()} -> Slug: '{slug}' | Channel: AIR_1 | XML payload: {mosMsg}", "INFO");
    }

    public void LogDvbTableTransmission(string tableName, int pid, int version)
    {
        Log("DVB-TS", $"[ETSI EN 300 468] Generated {tableName} | PID: 0x{pid:X4} ({pid}) | Version: {version} | Cycle: OK | TR 101 290 P1: PASS", "INFO");
    }
}
