# Kashtrix Broadcast Playout System

**Professional Broadcast Playout & CG System** — A complete, production-grade TV broadcast playout engine with character generator, output engine, scheduling, MAM, NRCS integration, and multi-protocol streaming.

## System Architecture

| Module | Description |
|--------|-------------|
| **BroadcastPlayout.App** | Core playout engine — video rendering, CG compositor, channel controller |
| **Kashtrix.OutputEngine** | Multi-protocol output — DeckLink, NDI, Matrox, AJA, DVB-TS UDP, SRT, RTMP, HLS, DASH |
| **Kashtrix.CGEditor** | Visual CG template editor — timeline, layers, animations, data bindings |
| **Kashtrix.CGController** | Live CG control panel — on-air graphics management |
| **Kashtrix.ChannelController** | Channel-level playout automation |
| **Kashtrix.Scheduler** | Broadcast scheduling & as-run logging |
| **Kashtrix.PlaylistEditor** | Playlist management & rundown editor |
| **Kashtrix.MAM** | Media Asset Management |
| **Kashtrix.NRCS** | Newsroom Computer System integration (MOS 2.8.4) |
| **Kashtrix.FileManager** | Media file browser & ingest |
| **Kashtrix.IngestServer** | SRT/NDI/SDI ingest capture |
| **Kashtrix.Multiview** | Multi-source monitoring wall |
| **Kashtrix.Prompter** | Teleprompter for live broadcasts |
| **Kashtrix.QCController** | Quality Control & compliance monitoring |
| **Kashtrix.HAController** | High Availability failover controller |
| **Kashtrix.Settings** | System-wide configuration |

## Output Engine Features

- **Hardware**: Blackmagic DeckLink, Matrox DSX, AJA Kona/Corvid, NDI 6
- **Streaming**: RTMP (YouTube, Facebook, Twitch, TikTok, Custom), SRT, HLS, DASH/MPD, RTSP
- **Transport**: DVB-TS UDP Multicast & Unicast with full SI/PSI compliance
- **Standards**: DVB SI/PSI (PAT, PMT, SDT, NIT, TDT), SCTE-35 splice triggers, MOS 2.8.4
- **GPU Encoding**: NVENC H.264/H.265, Intel QSV, AMD AMF, Software x264/x265

## Build

```bash
dotnet build BroadcastPlayout.sln --configuration Release
```

## Requirements

- .NET 8.0+ (Windows)
- WPF Runtime
- Blackmagic Desktop Video SDK (for DeckLink output)

## License

Proprietary — Kashtrix Broadcast Systems
