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

## Status

Early foundations (milestone M0). The physics model behind `VesselDynamicsFactory` is currently a clearly labelled kinematic placeholder that only exists to exercise the plumbing.

## Disclaimer

Not affiliated with or endorsed by NYC Ferry, NYCEDC or Hornblower. Not for navigation. The NYC Ferry name and livery belong to their owners. Public release requires their permission.
