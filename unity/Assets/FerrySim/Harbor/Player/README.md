# Harbor Player

`HarborPlayerSmokeRun` owns `-ferrysim-smoke` for a harbor-first player. It starts Astoria on a real environment/traffic day through the scene's existing catalog and launcher, waits for real tileset readiness, drives astern, captures both views, closes the recording and writes an explicit verdict. It requires packaged paths and unavailable repository fallback data. The reported maximum speed is sampled at rendered-frame polls; the legacy scalar drawn-water field is omitted because it is not measured for the spatial harbor surface.

The two materials in `Resources/` retain `FerrySim/SolidColour` and `FerrySim/HarborLabel`. Navigation creates its own materials through `Shader.Find`; these small source assets give the player explicit shader dependencies without changing the runtime navigation owner, scene or project settings. The signal-light shader already has its own existing Resources dependency, and traffic's URP Lit shader is retained by the vessel materials.

The player and test folders have their own assemblies. EditMode tests cover first-scene selection, installed path containment and a calibrated zero-allocation polling predicate. Build-area tests cover the retained shader dependencies and the approved folder-resolution regression. Installed-player smoke, screenshots and Windows follow-up commands are described in `docs/releasing.md` and the task report.
