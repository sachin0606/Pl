KASHTRIX PLAYOUT STUDIO - FIX49H
================================
Windows x64 broadcast automation suite targeting .NET 10 / WPF with FFmpeg,
NDI, SQLite, Chromium HTML graphics and optional professional hardware I/O.

Applications: Playout, CG Editor, Scheduler, Playlist Editor, Settings,
Multiview, Channel Controller, CG Controller, File Manager, QC Controller,
Ingest Server, MAM, NRCS, Prompter, HA Controller and API Gateway.

FIX49H is a consolidated Windows build-debug / verifier-consistency recovery on top of FIX49G.
The application/runtime source from FIX49G is preserved. The verifier is updated so historical
regression checks validate the current editor architecture instead of retired implementation
strings.

Build-debug fixes in FIX49H:
- FIX48J no longer requires the retired LayerList.UnselectAll call. It validates the current
  editor-owned selection set and ListBox visual synchronization instead.
- FIX49A no longer requires the retired CTRL/SHIFT MULTI-SELECT label. It validates the actual
  _selectedLayerIds / ToggleLayerSelection / LayerList_PreviewMouseLeftButtonDown path.
- FIX49H adds a self-consistency guard that rejects those retired verifier signatures if they
  are accidentally reintroduced.
- The FIX49G structural draggable-playhead checks remain in FIX17D and FIX48O.

Retained runtime behavior:
- exact Ctrl+click timeline multi-selection used by Group and Precompose;
- nested PRECOMP edit/navigation;
- real timeline +/- pixels-per-second zoom;
- selected-channel Program raster/cadence (1080i50, 1080p25/50, etc.);
- FIX49D UI/button and Program smoothness changes.

Use START-HERE.txt and SETUP-AND-BUILD.cmd for the first build.
