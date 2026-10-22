# 0002: Engine-agnostic C# simulation core

**Status:** accepted (2026-10-22)

## Context

Handling realism (twin-screw maneuvering, currents, landings) is the heart of the simulator. It has to be:
- testable quickly and repeatably without opening the Unity editor
- deterministic, so replays and debriefs are trustworthy
- easy for both humans and coding agents to work on

## Decision

**Location:**
- All physics, the control model and environment interfaces live in `core/FerrySim.Core/`, a Unity local package with an asmdef that has no engine references.
- Unity compiles it through `unity/Packages/manifest.json` (`file:../../core/FerrySim.Core`).
- The .NET SDK compiles the same files through `core/dotnet/FerrySim.Core/FerrySim.Core.csproj`. `EnableDefaultCompileItems` is off, so `bin/` and `obj/` never land inside the package.

**Language and runtime:**
- `netstandard2.1` at `LangVersion 9.0`, matching Unity 6's compiler.
- Doubles throughout.
- A fixed 100 Hz step, with Unity rendering interpolated state.

**Callers:** callers depend only on `Contracts/` (`ISimulationFactory`, `ISimulation`, `VesselState`, `ControlInputs`, `IEnvironment`, `VesselParameters`).

**Conformance tests:** the tests in `core/dotnet/FerrySim.Core.Tests/Contracts/` define the behavior every model must keep:
- sign conventions
- current applied in the right frame
- determinism
- no blow-ups

## Consequences

- `dotnet test` exercises the physics in seconds, and the headless CLI can run standard maneuvers and calibration in the loop.
- Unity-specific code (rendering, input devices, UI) stays thin and cannot change the physics.
- Moving to another engine later would mean rewriting only the front end. The core would need a C ABI or a port if that engine isn't .NET-based.
