# 0001: Unity 6.3 LTS with URP

**Status:** accepted (2026-10-22)

## Context

The simulator has to look and feel realistic for people learning to run NYC Ferry boats. The main development machine is a Mac mini (Apple M1, 16 GB). Learners may use Macs or Windows PCs.

Epic lists M1 with 16 GB as the minimum for Unreal Engine 5. Nanite needs M2 or later, and users report editor frame rates of 8 to 25 fps on M1. Godot's ocean, lighting and real-world map streaming are immature. A browser build would cap visual and physics realism.

## Decision

- Use Unity 6.3 LTS (editor `6000.3.27f1` at setup) with the Universal Render Pipeline.
- Use Cesium for Unity for georeferencing and floating origin from the start.
- Pin the exact editor version in `unity/ProjectSettings/ProjectVersion.txt`.

## Consequences

- **Platforms:** the project runs on the dev machine and builds for macOS and Windows.
- **Renderer:** Unity's 2026 rendering strategy puts HDRP in maintenance and focuses on URP, so URP is the long-term choice. URP has no built-in ocean, so water needs a third-party asset or a custom shader. That decision comes in M2, and nothing gets bought without approval.
- **Language:** gameplay and bridge code is C#, the same language as the sim core (see 0002).
