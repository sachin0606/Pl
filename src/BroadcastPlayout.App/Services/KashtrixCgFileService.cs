using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

/// <summary>
/// Proprietary encrypted binary file storage for Kashtrix CG compositions (.kcg, .kashtrixcg).
/// Stores compressed, encrypted composition data with header verification to ensure compositions
/// cannot be viewed or modified in generic text editors (Notepad, VS Code, etc.).
/// </summary>
public static class KashtrixCgFileService
{
    public const string DefaultExtension = ".kcg";
    public const string SecondaryExtension = ".kashtrixcg";
    public const string FileFilter = "Kashtrix CG File (*.kcg;*.kashtrixcg)|*.kcg;*.kashtrixcg|Legacy CGX Composition (*.cgx)|*.cgx|JSON Composition (*.json)|*.json|All Files (*.*)|*.*";

    // Magic Header: "KCG1" + Version 0x01
    private static readonly byte[] MagicHeader = [0x4B, 0x43, 0x47, 0x31, 0x01];

    // Proprietary 32-byte key stream salt for obfuscation/encryption
    private static readonly byte[] KeySalt =
    [
        0x5F, 0xA3, 0x19, 0x8C, 0x7E, 0x44, 0xB2, 0x90,
        0x33, 0xD1, 0x6A, 0xF5, 0x82, 0x1B, 0x4D, 0x99,
        0xC4, 0x2A, 0x71, 0xE8, 0x0F, 0x58, 0xB6, 0x3D,
        0x9E, 0x12, 0x85, 0x6F, 0x27, 0xDC, 0x4B, 0x78
    ];

    public static void SaveComposition(string filePath, CgProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var json = JsonSerializer.Serialize(project, new JsonSerializerOptions { WriteIndented = false });
        var plainBytes = Encoding.UTF8.GetBytes(json);

        // 1. Deflate compression
        byte[] compressed;
        using (var mem = new MemoryStream())
        {
            using (var deflate = new DeflateStream(mem, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(plainBytes, 0, plainBytes.Length);
            }
            compressed = mem.ToArray();
        }

        // 2. Proprietary rolling cipher encryption
        var encrypted = new byte[compressed.Length];
        var state = 0x5A;
        for (var i = 0; i < compressed.Length; i++)
        {
            var keyByte = KeySalt[i % KeySalt.Length];
            var b = compressed[i];
            var cipher = (byte)(b ^ keyByte ^ state ^ ((i * 37) & 0xFF));
            state = (state + cipher + 17) & 0xFF;
            encrypted[i] = cipher;
        }

        // 3. Write binary container [MagicHeader][Checksum 4 bytes][Payload]
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(MagicHeader, 0, MagicHeader.Length);

        // Simple rolling checksum of plain text
        var checksum = 0;
        foreach (var b in plainBytes) checksum = (checksum * 31 + b) & 0x7FFFFFFF;
        var checkBytes = BitConverter.GetBytes(checksum);
        fs.Write(checkBytes, 0, checkBytes.Length);

        fs.Write(encrypted, 0, encrypted.Length);
    }

    public static CgProject LoadComposition(string filePath)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("Composition file not found", filePath);
        var raw = File.ReadAllBytes(filePath);
        if (raw.Length == 0) throw new InvalidDataException("Composition file is empty.");

        // Check if proprietary binary format
        var isKcg = raw.Length > (MagicHeader.Length + 4) &&
                    raw[0] == MagicHeader[0] &&
                    raw[1] == MagicHeader[1] &&
                    raw[2] == MagicHeader[2] &&
                    raw[3] == MagicHeader[3] &&
                    raw[4] == MagicHeader[4];

        if (isKcg)
        {
            var offset = MagicHeader.Length + 4; // Skip magic + 4 byte checksum
            var cipherLen = raw.Length - offset;
            var decrypted = new byte[cipherLen];
            var state = 0x5A;

            for (var i = 0; i < cipherLen; i++)
            {
                var cipher = raw[offset + i];
                var keyByte = KeySalt[i % KeySalt.Length];
                var plain = (byte)(cipher ^ keyByte ^ state ^ ((i * 37) & 0xFF));
                state = (state + cipher + 17) & 0xFF;
                decrypted[i] = plain;
            }

            using var mem = new MemoryStream(decrypted);
            using var deflate = new DeflateStream(mem, CompressionMode.Decompress);
            using var reader = new StreamReader(deflate, Encoding.UTF8);
            var json = reader.ReadToEnd();
            var project = JsonSerializer.Deserialize<CgProject>(json);
            return project ?? throw new InvalidDataException("Deserialization of Kashtrix CG file produced null.");
        }

        // Backwards compatibility fallback for plain JSON or legacy .cgx
        var text = Encoding.UTF8.GetString(raw).TrimStart();
        if (text.StartsWith('{') || text.StartsWith('['))
        {
            var project = JsonSerializer.Deserialize<CgProject>(text);
            return project ?? throw new InvalidDataException("Legacy composition data is invalid.");
        }

        throw new InvalidDataException("Unsupported or corrupted Kashtrix CG file format.");
    }
}
