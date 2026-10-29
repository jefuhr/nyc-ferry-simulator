# Harbor Terrain

`HarborTileset` is a local-only Cesium adapter; `IHarborViewLayer` exposes render readiness without entering the physics or semantic query path. The scene configuration sets the material, cache allowance, required status and relative tileset path. All per-frame readiness sampling is allocation-free; inventory reading and hashing happen only at `Open`.

`HarborSurface.shader` consumes the sky's ambient globals and the directional sun, and uses URP's scene fog. Its depth pass supports the existing water depth tint. No tile collider or mesh cooking is enabled. See `Harbor/README.md` for integration and verification.
