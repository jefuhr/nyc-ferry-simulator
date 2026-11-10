# Prompts

Owner: **keybind-popups** (v0.5). Assembly `FerrySim.Unity.Prompts` (namespace `FerrySim.Prompts`): the keybind popups, small plates that hover over the real controls in the 3D wheelhouse and show the key or button that works each one. Built with UI Toolkit only; colours come from `UI/Display/InstrumentPalette.cs`, text uses the panel theme's font.

## What it shows

For every control in the wheelhouse control map (`controls-v1.json`, contract C3) that has `actions`, a plate with:

- the binding text of the **first action in `actions` that has a binding in the active scheme** (`IBindingService.DisplayString`), in large type, and
- the control's `label` under it in smaller type.

A control with no bound action in that scheme shows nothing. The plate sits just above the control (below it when there is no room above, at the top edge of the view) with a short leader and a dot on the control's pivot.

**The active scheme** is the scheme of the device last used: a button pressed on a keyboard or mouse (`Keyboard`; mouse buttons share it), a gamepad (`Gamepad`) or a joystick (`Joystick`), taken from the Input System (`InputSystem.onAnyButtonPress`). The binding service does not track one. Moving an axis alone does not switch the scheme. Before any input it is `Keyboard`. `KeybindPopups.ActiveScheme` can also be set from code.

**Text** is rebuilt only on `IBindingService.BindingsChanged` (so a rebind shows at once), on a scheme change and on `Bind` (a new interior). Never per frame.

## When it shows

`Mode` is the C5 setting `hud.keybindHints` as the UI's own `FerrySim.UI.KeybindHintMode` (`SettingsStore.KeybindHints`): `Off`, `Hold` (only while `Interface/KeybindHints` is held, backquote by default) or `Always`. On top of that, popups show only when all of these hold:

- the camera is in the wheelhouse view, the lean-in view included (the camera's `CameraRig.CurrentMode`; without a rig, whether its `WheelhouseCameraController` is enabled; a camera with neither counts as a wheelhouse camera, for tools and tests);
- `Suppressed` is false (the composition code sets it while the options menu or the scenario picker is open);
- there is an interior with a control map (`Bind` was given one and it has not been destroyed).

The hold key is read from the driver's own copy of the actions asset, registered with the binding service (`InputBindingService.CreateInstance`, or `Register` of a copy of `FerrySimInput/BindingDefaults` for any other `IBindingService`), so a rebind of the hints key reaches it.

## Placement

Each frame, in `LateUpdate` after the cameras (`[DefaultExecutionOrder(100)]`, cameras run at −100):

- every mapped mount is projected through the camera into panel pixels. A control behind the camera, off screen, or whose face points away from the eye (its C2 `normal`, in the interior's frame) is hidden;
- emphasis falls off with distance (full up to 1.5 m, weakest from 6 m) and with distance from the view centre (full within 15% of the half diagonal): the plate shrinks to 0.8 and the leader fades to 0.4. **The plate itself stays opaque.** Any translucency over a bright window takes the day captions below WCAG AA (at 0.95 opacity over white, `TextDim` falls to about 4.3:1), so the fade is carried by size and the leader;
- `KeybindDeclutter` places the plates so none overlap: nearest the view centre first, each at home (centred above its control) or, if that would overlap a plate already placed or cover its pivot dot, at one of six nearby spots one plate width or height away, or dropped. At most 12 are shown, the 12 nearest the centre. Keeping clear of earlier pivots matters in the column of horn and alarm buttons: without it a plate sat on its neighbour's dot and the reader could not tell which control it belonged to;
- hysteresis keeps it steady: a plate shown last frame ranks as if 3% of the half diagonal nearer the centre (6% if it was at home), so plates swap or a newcomer displaces one at the cap only when the order changes clearly; and a spot a plate held last frame only needs to be free, while any other spot needs 4 px of clear space, so a plate at the edge of a collision does not flip. A still camera gives exactly the same result every frame;
- plates move with `style.translate` and `style.scale`, the leader with `translate`, `rotate` and `scale`, all on `UsageHints.DynamicTransform` elements; `left` and `top` are set once, never per frame, and a style is written only when its value changes.

The update allocates nothing in steady state and runs inside the profiler marker `FerrySim.KeybindPopups.Update` (budget 0.15 ms at the 95th percentile with every mapped control in view).

## Look

At 1280×720 reference pixels: key text 18 px bold, label 12 px, on a plate of the palette's `HudBacking` (opaque) with a 1 px `Bezel` border and 4 px corners. Key text is `Text`; the label is `TextDim` by day and `Text` at night (the night `TextDim` is under 3:1 on black). The leader and the dot are `Needle` with a `HudBacking` edge so they read over both the console and a bright window. Day text meets WCAG AA on the plate, night text the night rule (3:1), over white and black backdrops and over the real wheelhouse by day and night (`KeybindPopupsPlayModeTests`). Original styling, no key-cap artwork.

## Using it from the composition code

```csharp
// once, beside the wheelhouse UI
var popups = host.AddComponent<KeybindPopups>();
popups.AttachTo(wheelhouseUI.Document);                 // the layer is added last, so it draws over the panel and HUD
// at run setup and after every class switch (the control map belongs to one interior instance)
popups.Bind(cameraRig.GetComponent<Camera>(), runner.GetComponentInChildren<WheelhouseControlsView>(), InputBindingService.Instance);
// from the saved setting, now and on view.Settings.HudChanged
popups.Mode = view.Settings.KeybindHints;
// while the options menu or the scenario picker is open: now and on OptionsVisibilityChanged and PickerVisibilityChanged
popups.Suppressed = view.OptionsVisible || view.PickerVisible;
// follow the palette (no change event: setting it every frame costs nothing when unchanged)
popups.Palette = view.Palette;
```

`Unbind()` lets go of everything (also done on destroy). Members: `Layer` (the `KeybindPopupLayer`), `Mode`, `Suppressed`, `Palette`, `ActiveScheme`, `Camera`, `AttachTo(UIDocument)`, `AttachTo(VisualElement)`, `Bind(Camera, Transform, ControlMapFile, IBindingService)`, `Bind(Camera, WheelhouseControlsView, IBindingService)`, `Unbind()`. `KeybindPopupLayer.TryGetState` and `GetState` say what each popup shows and where.

## Tests

- `Tests/EditMode` (`FerrySim.Unity.Prompts.Tests.EditMode`): the layer against a fake binding service and a stand-in interior of plain transforms parsed through the real `ControlMapFile.Parse` (text, first bound action, rebind, unbound, rebuild triggers, modes, hold, suppression, no interior, behind, off screen, facing away, placement, no overlap, the cap of 12, stability, strength, no focus or picking, zero allocation with the camera turning); the declutter solver alone (hysteresis at a collision edge, at the cap, between two plates, 300 random arrangements); and the driver with the real binding service and virtual devices (the hold key from its own registered copy and its rebind, the scheme of the last device, the camera rig's views, attaching).
- `Tests/PlayMode` (`FerrySim.Unity.Prompts.Tests.PlayMode`): the real slice with both interior prefabs. Every mapped control's label is above its mount, at home or one plate away, with the seated eye turned to it; plate contrast by day and night; the 0.15 ms budget and zero allocation with every mapped control in view; captures to `artifacts/keybind-popups/captures/` (from the eye in `Always` mode, day and night, both classes, 720p and 1440p, each from three seat head looks: the console, the engine levers and the horn and alarm buttons; after rebinding the horn to F11; and in the lean-in view on the radar), with `labels.txt` (every plate's text, position and measured contrast), and `label-positions.txt`, `seat-view.txt` and `frame-cost.txt` beside them.

**From the seat no single view holds every mapped control**: they span about 140 degrees of yaw (the horn and alarm buttons near −70°, the engine levers near +70°), and with the head look's 45° pitch limit the alarm buttons sit just below the bottom edge of the best whole-console view (`seat-view.txt`). The budget test therefore aims the camera from the eye at the controls' centroid and backs it 0.16 m along that line, where all eight fit.

Run them as in `unity/README.md`, with `-assemblyNames FerrySim.Unity.Prompts.Tests.EditMode` (EditMode, `-nographics`) or `FerrySim.Unity.Prompts.Tests.PlayMode` (PlayMode, with graphics).

## Estimates

- Every mapped control's position in both interiors is a photo estimate of low confidence (`art/blender/data/*-wheelhouse.json`; WH-03, and WH-04 for the horn and alarm buttons). The popups follow the mounts, so a moved mount moves its popup; the captures show where they are today, not where the real controls are.
- Popups do not test occlusion: a control hidden behind another part of the model still gets its popup while its pivot projects inside the view.
- The fade distances (1.5 m and 6 m), the 15% full-strength radius, the minimum scale (0.8), the leader length (18 px), the hysteresis (3% and 4 px) and the type sizes are UI choices, not measurements.
