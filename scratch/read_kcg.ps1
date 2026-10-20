$file = (Get-ChildItem -Path ".\cg-demo" -Filter "*Prime*Breaking News Live*.kcg").FullName
$raw = [System.IO.File]::ReadAllBytes($file)

# KeySalt
$keySalt = [byte[]]@(
    0x5F, 0xA3, 0x19, 0x8C, 0x7E, 0x44, 0xB2, 0x90,
    0x33, 0xD1, 0x6A, 0xF5, 0x82, 0x1B, 0x4D, 0x99,
    0xC4, 0x2A, 0x71, 0xE8, 0x0F, 0x58, 0xB6, 0x3D,
    0x9E, 0x12, 0x85, 0x6F, 0x27, 0xDC, 0x4B, 0x78
)

$offset = 5 + 4
$cipherLen = $raw.Length - $offset
$decrypted = New-Object byte[] $cipherLen
$state = 0x5A

for ($i = 0; $i -lt $cipherLen; $i++) {
    $cipher = $raw[$offset + $i]
    $keyByte = $keySalt[$i % $keySalt.Length]
    $plain = [byte](($cipher -bxor $keyByte -bxor $state -bxor (($i * 37) -band 0xFF)) -band 0xFF)
    $state = ($state + $cipher + 17) -band 0xFF
    $decrypted[$i] = $plain
}

$mem = New-Object System.IO.MemoryStream(,$decrypted)
$deflate = New-Object System.IO.Compression.DeflateStream($mem, [System.IO.Compression.CompressionMode]::Decompress)
$reader = New-Object System.IO.StreamReader($deflate, [System.Text.Encoding]::UTF8)
$json = $reader.ReadToEnd()

$obj = $json | ConvertFrom-Json
$obj.Layers | Select-Object Name, Type, Source, StartSeconds, EndSeconds, SequenceFps, SequenceStartFrame, SequenceEndFrame, SequenceLoop, SequenceHoldLastFrame, SequenceAdvanceDataItem | Format-List
