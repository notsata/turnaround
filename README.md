# TURNAROUND

A 1–4 player co-op airport ground-crew game. Barely competent ramp agents race the clock to turn an
AeroJet RJ-200 around: chocks, cones, GPU, baggage, the belt loader, the pushback tug, and an ever-growing
list of safety violations. Between flights you run a small ground-handling company that's mostly in debt.

## Play

1. Download **TURNAROUND.zip** from the [latest release](https://github.com/notsata/turnaround/releases/latest).
2. Right-click it → **Extract All** (running it from inside the zip fails with "failed to load mono").
3. Run `TURNAROUND\TURNAROUND.exe`.

The game updates itself: when a new release is out, the main menu downloads it and shows **RESTART TO UPDATE**.
Everyone in a session needs the same version.

The same build is also in this repo under [`Builds/Windows`](Builds/Windows).

### Playing together

- One person clicks **HOST GAME**; everyone else clicks **JOIN GAME** and types the host's address.
- Same PC: `127.0.0.1`. Same home network: the host's `192.168.x.x`. Over the internet: the host's public IP,
  with **UDP port 7777** forwarded to the host's PC (or use Tailscale and the host's `100.x.x.x` address).

## Develop

Unity 6000.2.10f1, Netcode for GameObjects 2.13. The scene, prefabs and materials are generated from code:
**TURNAROUND → Rebuild Prototype** in the Editor (or `-executeMethod TurnaroundBuilder.Build` in batch mode).

- `-host -autoturn` plays a whole turnaround (plus every random event) by itself, for testing.
- `.\release.ps1 1.2.0 -Notes "..."` builds, zips, commits the build, tags and publishes a GitHub release.
