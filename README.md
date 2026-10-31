# NYC Ferry Simulator

A realistic ship-handling simulator for people learning to operate NYC Ferry boats. It starts with the T-class (85 ft, Subchapter T) and K-class (97 ft, Subchapter K) catamarans in the waters they actually run in: real geography, depths, aids to navigation and tidal currents.

## Layout

```
docs/                     conventions, decision records, research, crew reference kit
core/FerrySim.Core/       engine-agnostic C# sim core (also a Unity local package)
core/dotnet/              .NET projects that compile the core for tests and the headless CLI
vessels/                  vessel parameter files with sources and confidence for every value
data-pipeline/            Python (uv) pipeline for GTFS, NOAA charts, depths, currents and AIS
data/manifests/           committed manifests; raw and processed data stay out of git
unity/                    Unity 6.3 LTS project (URP)
scripts/check.sh          runs every automated check and writes results to artifacts/
scripts/release.sh        builds, packages and smoke-tests the macOS and Windows players
scripts/record-windows-smoke.sh  records a release's Windows smoke run on a PC
```

Start with [docs/conventions.md](docs/conventions.md). It defines the units, frames and sign conventions every part of the simulator shares.

## Requirements

- .NET SDK 10 (`brew install dotnet`)
- Unity Hub with Unity `6000.3.27f1` (Unity 6.3 LTS)
- git-lfs (`brew install git-lfs`, then `git lfs install --local`)
- uv, for the data pipeline

## Checks

```
dotnet test core/dotnet --logger "trx;LogFilePrefix=core" --results-directory artifacts/core
```

## Releases

Releases are a universal macOS app and a Windows x64 player, published as prereleases on this repository's Releases page. [docs/releasing.md](docs/releasing.md) covers building one and installing it.

## Status

The current release covers the whole New York Harbor from the Upper Bay up the East River to Astoria and Soundview, with 24 landings (every route stop except Rockaway). It has:
- the T-class and K-class on the twin-screw dynamics, with the crew-reported 20° rudder and turning circle;
- real tide, currents (the station blend), wind and depth for 28 days, with the matching real AIS traffic;
- streamed terrain and an LoD1 skyline, night lights and fog;
- radar and a chartplotter;
- landing contact physics, record and replay, and end-to-end journey tests.

Not done yet:
- crew calibration of most handling parameters (most are still estimated);
- three landing faces that wait for crew confirmation (St. George, East 90th St, Sunset Park/BAT);
- Rockaway, waves and wakes;
- VHF and audio;
- the trainer: lessons, scoring and instructor mode.

## Disclaimer

This is an unofficial training project. It is not affiliated with or endorsed by NYC Ferry, NYCEDC or Hornblower, and it uses none of their logos or livery. It is not for navigation: depths, currents, traffic and handling are approximations and must never be used to plan or conduct a real voyage.
