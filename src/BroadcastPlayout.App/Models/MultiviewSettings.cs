using System;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace BroadcastPlayout.Models;

public sealed class MultiviewSettings
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kashtrix",
        "multiview-settings.json");

    // SMTP / Email Alerts Integration
    public bool EnableEmailAlerts { get; set; } = false;
    public string SmtpServer { get; set; } = "smtp.office365.com";
    public int SmtpPort { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string SmtpUsername { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public string SenderEmail { get; set; } = "alerts@broadcasthost.tv";
    public string RecipientEmail { get; set; } = "noc@broadcasthost.tv";
    public bool AlertOnBlackFrame { get; set; } = true;
    public bool AlertOnFreeze { get; set; } = true;
    public bool AlertOnAudioSilence { get; set; } = true;
    public bool AlertOnScte35 { get; set; } = true;
    public bool AlertOnQualityDrop { get; set; } = true;
    public bool EnableDesktopNotifications { get; set; } = true;

    // Broadcast Quality Control (QC) Thresholds
    public double QualityThresholdPercent { get; set; } = 85.0;
    public double MinBitrateMbps { get; set; } = 4.5;
    public bool EnableScte35Detection { get; set; } = true;
    public double BlackFrameThresholdSeconds { get; set; } = 2.0;
    public double FreezeThresholdSeconds { get; set; } = 3.0;
    public double AudioSilenceThresholdSeconds { get; set; } = 3.0;
    public double AudioLowThresholdDbfs { get; set; } = -42.0;
    public double AudioLoudnessTargetLufs { get; set; } = -23.0; // EBU R128 / ATSC A/85
    public double AudioLoudnessToleranceLu { get; set; } = 1.0;
    public byte BlackLumaThreshold { get; set; } = 12;
    public double SignalLossThresholdSeconds { get; set; } = 2.0;
    public double AlertCooldownSeconds { get; set; } = 60.0;

    public Guid ActiveProfileId { get; set; }
    public List<MultiviewMonitoringProfile> Profiles { get; set; } = [];

    public static MultiviewSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                var loaded = JsonSerializer.Deserialize<MultiviewSettings>(json);
                if (loaded != null)
                {
                    loaded.EnsureProfiles();
                    return loaded;
                }
            }
        }
        catch { }
        var settings = new MultiviewSettings();
        settings.EnsureProfiles();
        return settings;
    }

    public void Save()
    {
        try
        {
            EnsureProfiles();
            var dir = Path.GetDirectoryName(SettingsFile);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch { }
    }

    public void EnsureProfiles()
    {
        Profiles ??= [];
        if (Profiles.Count == 0)
        {
            var initial = CaptureProfile("Broadcast Default");
            Profiles.Add(initial);
            ActiveProfileId = initial.Id;
        }
        var active = Profiles.FirstOrDefault(x => x.Id == ActiveProfileId) ?? Profiles[0];
        ActiveProfileId = active.Id;
        ApplyProfile(active);
    }

    public MultiviewMonitoringProfile CaptureProfile(string name, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = string.IsNullOrWhiteSpace(name) ? "Monitoring Profile" : name.Trim(),
        EnableDesktopNotifications = EnableDesktopNotifications,
        EnableEmailAlerts = EnableEmailAlerts,
        AlertOnBlackFrame = AlertOnBlackFrame,
        AlertOnFreeze = AlertOnFreeze,
        AlertOnAudioSilence = AlertOnAudioSilence,
        AlertOnScte35 = AlertOnScte35,
        AlertOnQualityDrop = AlertOnQualityDrop,
        QualityThresholdPercent = QualityThresholdPercent,
        MinBitrateMbps = MinBitrateMbps,
        EnableScte35Detection = EnableScte35Detection,
        BlackFrameThresholdSeconds = BlackFrameThresholdSeconds,
        BlackLumaThreshold = BlackLumaThreshold,
        FreezeThresholdSeconds = FreezeThresholdSeconds,
        SignalLossThresholdSeconds = SignalLossThresholdSeconds,
        AudioSilenceThresholdSeconds = AudioSilenceThresholdSeconds,
        AudioLowThresholdDbfs = AudioLowThresholdDbfs,
        AudioLoudnessTargetLufs = AudioLoudnessTargetLufs,
        AudioLoudnessToleranceLu = AudioLoudnessToleranceLu,
        AlertCooldownSeconds = AlertCooldownSeconds
    };

    public void ApplyProfile(MultiviewMonitoringProfile profile)
    {
        EnableDesktopNotifications = profile.EnableDesktopNotifications;
        EnableEmailAlerts = profile.EnableEmailAlerts;
        AlertOnBlackFrame = profile.AlertOnBlackFrame;
        AlertOnFreeze = profile.AlertOnFreeze;
        AlertOnAudioSilence = profile.AlertOnAudioSilence;
        AlertOnScte35 = profile.AlertOnScte35;
        AlertOnQualityDrop = profile.AlertOnQualityDrop;
        QualityThresholdPercent = profile.QualityThresholdPercent;
        MinBitrateMbps = profile.MinBitrateMbps;
        EnableScte35Detection = profile.EnableScte35Detection;
        BlackFrameThresholdSeconds = profile.BlackFrameThresholdSeconds;
        BlackLumaThreshold = profile.BlackLumaThreshold;
        FreezeThresholdSeconds = profile.FreezeThresholdSeconds;
        SignalLossThresholdSeconds = profile.SignalLossThresholdSeconds;
        AudioSilenceThresholdSeconds = profile.AudioSilenceThresholdSeconds;
        AudioLowThresholdDbfs = profile.AudioLowThresholdDbfs;
        AudioLoudnessTargetLufs = profile.AudioLoudnessTargetLufs;
        AudioLoudnessToleranceLu = profile.AudioLoudnessToleranceLu;
        AlertCooldownSeconds = profile.AlertCooldownSeconds;
    }

    public MultiviewMonitoringProfile SaveProfile(string name, Guid? id = null)
    {
        var profile = CaptureProfile(name, id);
        var index = Profiles.FindIndex(x => x.Id == profile.Id);
        if (index >= 0) Profiles[index] = profile; else Profiles.Add(profile);
        return profile;
    }

    public void ActivateProfile(MultiviewMonitoringProfile profile)
    {
        ActiveProfileId = profile.Id;
        ApplyProfile(profile);
    }

    public async Task<(bool success, string message)> SendTestAlertEmailAsync()
    {
        if (string.IsNullOrWhiteSpace(SmtpServer))
            return (false, "SMTP Server host cannot be empty.");
        if (string.IsNullOrWhiteSpace(RecipientEmail))
            return (false, "Recipient email address cannot be empty.");

        try
        {
            using var client = new SmtpClient(SmtpServer, SmtpPort)
            {
                EnableSsl = EnableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Timeout = 10000
            };

            if (!string.IsNullOrWhiteSpace(SmtpUsername))
            {
                client.Credentials = new NetworkCredential(SmtpUsername, SmtpPassword);
            }

            var from = string.IsNullOrWhiteSpace(SenderEmail) ? "alerts@kashtrix.broadcasthost.tv" : SenderEmail.Trim();
            using var msg = new MailMessage(from, RecipientEmail.Trim())
            {
                Subject = "[KASHTRIX MULTIVIEW] Broadcast QC & Alert System Test",
                Body = $"Kashtrix Multiviewer QC Alert System Test\n\n" +
                       $"Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n" +
                       $"Quality Threshold: {QualityThresholdPercent}%\n" +
                       $"Min Bitrate: {MinBitrateMbps:0.0} Mbps\n" +
                       $"Target Loudness: {AudioLoudnessTargetLufs:0.0} LUFS (±{AudioLoudnessToleranceLu:0.0} LU)\n" +
                       $"Black Frame Alert: {BlackFrameThresholdSeconds:0.0}s\n" +
                       $"Video Freeze Alert: {FreezeThresholdSeconds:0.0}s\n" +
                       $"Audio Silence Alert: {AudioSilenceThresholdSeconds:0.0}s\n" +
                       $"SCTE-35 Monitoring: {(EnableScte35Detection ? "ENABLED" : "DISABLED")}\n\n" +
                       $"This is an automated test from Kashtrix Broadcast Multiviewer."
            };

            await client.SendMailAsync(msg);
            return (true, $"Test alert email sent successfully to {RecipientEmail}");
        }
        catch (Exception ex)
        {
            return (false, $"SMTP send failure: {ex.Message}");
        }
    }

    public async Task<(bool success, string message)> SendAlertEmailAsync(string subject, string body)
    {
        if (!EnableEmailAlerts) return (false, "Email alerts are disabled in the active profile.");
        if (string.IsNullOrWhiteSpace(SmtpServer) || string.IsNullOrWhiteSpace(RecipientEmail))
            return (false, "SMTP server or recipient is not configured.");
        try
        {
            using var client = new SmtpClient(SmtpServer, SmtpPort)
            {
                EnableSsl = EnableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Timeout = 10000
            };
            if (!string.IsNullOrWhiteSpace(SmtpUsername))
                client.Credentials = new NetworkCredential(SmtpUsername, SmtpPassword);
            var from = string.IsNullOrWhiteSpace(SenderEmail) ? "alerts@kashtrix.broadcasthost.tv" : SenderEmail.Trim();
            using var message = new MailMessage(from, RecipientEmail.Trim()) { Subject = subject, Body = body };
            await client.SendMailAsync(message);
            return (true, $"Alert sent to {RecipientEmail}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}

public sealed class MultiviewMonitoringProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Broadcast Default";
    public bool EnableDesktopNotifications { get; set; } = true;
    public bool EnableEmailAlerts { get; set; }
    public bool AlertOnBlackFrame { get; set; } = true;
    public bool AlertOnFreeze { get; set; } = true;
    public bool AlertOnAudioSilence { get; set; } = true;
    public bool AlertOnScte35 { get; set; } = true;
    public bool AlertOnQualityDrop { get; set; } = true;
    public double QualityThresholdPercent { get; set; } = 85;
    public double MinBitrateMbps { get; set; } = 4.5;
    public bool EnableScte35Detection { get; set; } = true;
    public double BlackFrameThresholdSeconds { get; set; } = 2;
    public byte BlackLumaThreshold { get; set; } = 12;
    public double FreezeThresholdSeconds { get; set; } = 3;
    public double SignalLossThresholdSeconds { get; set; } = 2;
    public double AudioSilenceThresholdSeconds { get; set; } = 3;
    public double AudioLowThresholdDbfs { get; set; } = -42;
    public double AudioLoudnessTargetLufs { get; set; } = -23;
    public double AudioLoudnessToleranceLu { get; set; } = 1;
    public double AlertCooldownSeconds { get; set; } = 60;
}
