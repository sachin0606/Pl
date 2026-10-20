$code = @'
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

public class CgReader {
    private static readonly byte[] KeySalt = new byte[] {
        0x5F, 0xA3, 0x19, 0x8C, 0x7E, 0x44, 0xB2, 0x90,
        0x33, 0xD1, 0x6A, 0xF5, 0x82, 0x1B, 0x4D, 0x99,
        0xC4, 0x2A, 0x71, 0xE8, 0x0F, 0x58, 0xB6, 0x3D,
        0x9E, 0x12, 0x85, 0x6F, 0x27, 0xDC, 0x4B, 0x78
    };

    public static string ReadKcg(string filePath) {
        var raw = File.ReadAllBytes(filePath);
        int offset = 5 + 4;
        int cipherLen = raw.Length - offset;
        byte[] decrypted = new byte[cipherLen];
        int state = 0x5A;
        for (int i = 0; i < cipherLen; i++) {
            byte cipher = raw[offset + i];
            byte keyByte = KeySalt[i % KeySalt.Length];
            byte plain = (byte)(cipher ^ keyByte ^ state ^ ((i * 37) & 0xFF));
            state = (state + cipher + 17) & 0xFF;
            decrypted[i] = plain;
        }
        using (var mem = new MemoryStream(decrypted))
        using (var deflate = new DeflateStream(mem, CompressionMode.Decompress))
        using (var reader = new StreamReader(deflate, Encoding.UTF8)) {
            return reader.ReadToEnd();
        }
    }
}
'@
Add-Type -TypeDefinition $code
$f3 = (Get-ChildItem cg-demo -Filter "*Category Animated Ticker*.kcg" | Select-Object -First 1).FullName
Write-Host "File 3: $f3"
$json3 = [CgReader]::ReadKcg($f3)
[IO.File]::WriteAllText("scratch\category_ticker.json", $json3, [System.Text.Encoding]::UTF8)
Write-Host "Decoded Category Ticker successfully"
