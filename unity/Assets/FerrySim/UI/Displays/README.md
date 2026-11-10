# Skinned displays

The engine monitor and the alarm panel, drawn entirely from data. A **skin** (contract C6, [display-skin-v1.schema.json](../../../../../docs/contracts/display-skin-v1.schema.json)) says where every element goes, which channel or alarm it shows, its range, unit, colours and pages; the code here only knows how to draw seven kinds of element and an alarm list. When close-up photos of the real screens arrive, the look changes by editing JSON, not C#.

Everything here lives in the `FerrySim.Unity.UI` assembly, namespace `FerrySim.UI.Displays`. These classes do not place a display on the 3D console or in the head-up display; the console and HUD hosts do that with the views below.

## Using a display

```csharp
var library = new DisplaySkinLibrary(DisplaySkinAlarms.From(source.Instruments));   // private skins first, then shipped
var port = new EngineMonitorView(DisplaySide.Port, library.Load("engine", "generic-a"));
var panel = new AlarmPanelView(library.Load("alarm-panel", "generic-a"));
panel.Connect(source);                        // forwards acknowledge, silence and the held lamp test to IInstrumentSource
port.Palette = PaletteKind.Night;            // or PaletteRoles.Apply on a parent: the views are IPaletteAware

// every frame:
port.Refresh(source.HasInstruments ? source.Instruments : null, simulationTime);
```

- **Rendering to a console screen.** Give the view its own panel: `DisplayPanelSettings.Create(skin, renderTexture, clear)` makes panel settings whose reference resolution is the skin's `reference`, so a texture of that size, or any whole multiple, shows the display filling it exactly. The PlayMode tests render every generic skin this way at 1× and 2×.
- **Refresh** updates values at the skin's `refreshHz` (at once after a page change, a new readout, or time going backwards) and lamps on every call, at the flash rhythm of contract C1 (1 Hz unacknowledged, 0.5 Hz ring-back, steady when acknowledged, everything lit during a lamp test), the same rhythm as `AlarmAnnunciator.LampOn`. Pass simulation time so the flash agrees with the annunciator. A null readout (no data) shows every reading as `---` and every lamp dark.
- **Pages.** `ShowPage(name)`, `NextPage()` and `PageChanged`. A softkey with a `target` opens its page when released.
- **Keys.** A click or touch on a softkey works on screen. A host with physical buttons calls `PressSoftkey(index)` and `ReleaseSoftkey(index)` (element index from 0). Action keys raise `AcknowledgeRequested`, `SilenceRequested` and `LampTestChanged(bool)`; the view never touches the alarms itself. `CancelPresses()` (also on detach and on `Disconnect`) lets go of a held lamp test.
- **No garbage.** Static artwork (dial faces, bar tracks, bands) is drawn once with `Painter2D` and again only when the palette changes; a needle is a rotated layer and a bar fill a scaled one; text is set only when it changes, from a per-readout string cache. `DisplayAllocationTests` measures zero bytes over many refreshes with every value moving.
- **No data looks like no data.** `NaN` is `---`, a needle or bar fill disappears; never a zero.
- `FakeInstrumentReadout` (`TClass()`, `Underway()`) drives a display without a vessel, for tests and dev scenes.

## Writing a skin

```json
{
	"version": 1,
	"display": "engine",
	"id": "generic-a",
	"reference": [800, 480],
	"refreshHz": 5,
	"pages": ["main", "alarms"],
	"elements": [ ... ]
}
```

- `display` is `engine`, `alarm-panel`, `radar` or `plotter`; `id` is unique for that display. Both are lower-case words and dashes, because they are also the file's path.
- `reference` is the screen's size in reference pixels. Every `rect` is `[x, y, width, height]` against it, origin at the top left, and must lie inside it. Use the real screen's pixel size when it is known, otherwise its aspect ratio at a convenient size.
- `pages`: the first is shown at start. An element with a `page` shows only on that page; one without shows on every page. No `pages` means one page.
- `{side}` in a `channel` or `alarm` becomes `port` or `starboard` for a per-engine display, so one skin serves both engines; in `text` it becomes `PORT` or `STARBOARD`. A name with `{side}` must be valid for both sides.
- `unit` converts the channel's SI value for display: `rpm`, `kn`, `deg`, `deg/min`, `psi`, `bar`, `kPa`, `degC`, `degF`, `V`, `L/h`, `gal/h`, `pct`, `m`, `ft`, or `none` (the SI value unconverted, the default). The unit must fit the channel (no psi on engine speed). All conversions are in `SkinUnits`; the fuel volume rates use `SkinUnits.FuelDensityKilogramsPerLitre` (0.84, an estimate, documented there). `range` and `bands` are in the display unit.
- `colour` is a palette role (`console`, `face`, `bezel`, `grid`, `text`, `text-dim`, `scale`, `needle`, `fill`, `port`, `starboard`, `engaged`, `shifting`, `caution`, `alarm`, `redline`, `series-a`, `series-b`, `cursor`) or `#RRGGBB`. A role follows the day and night palettes like the rest of the wheelhouse. A hex colour is the day colour; its night colour is derived (`SkinColour.NightFromDay`) by the palette's night rules (red ≥ green ≥ blue, green at most 0.65 of red, luminance capped at 0.25), so a colour sampled from a daylight photo can never put blue, green or white light into a dark wheelhouse. Prefer roles: they are what `PaletteTests` measures.
- **A skin loads whole or not at all.** An unknown channel, alarm, element type, unit, page, colour, group, format or action, a property that does not apply to its element type, a rectangle outside the reference, or a band outside its range rejects the skin, with a message naming the element: `element 4 (value engine.{side}.oil-presure): unknown channel 'engine.{side}.oil-presure' (as 'engine.port.oil-presure')`.

### Element types

Every element has `type` and `rect`, and may have `page`, `text` and `colour`.

**`frame`**: a box with an optional title, drawn in the face colour with a border (`colour` is the border). Static artwork that groups other elements.
```json
{"type": "frame", "rect": [0, 0, 800, 40], "text": "{side} ENGINE"}
```

**`text`**: a fixed caption, sized to its rect's height.
```json
{"type": "text", "rect": [16, 440, 200, 24], "text": "STBD", "colour": "text-dim"}
```

**`dial`**: a round gauge from `range[0]` to `range[1]`, the needle at `sweep[0]` and `sweep[1]` degrees clockwise from straight up at the ends (default −120 to 120). Pegs at both ends. Numbered ticks come at a step of 1, 2, 2.5 or 5 times a power of ten giving 4 to 8 intervals. With `decimals` it also shows the number. `bands` colour arcs of the scale; `colour` is the needle.
```json
{"type": "dial", "rect": [16, 52, 300, 300], "channel": "engine.{side}.speed", "range": [0, 2400], "sweep": [-135, 135],
 "unit": "rpm", "decimals": 0, "text": "ENGINE", "bands": [{"range": [2100, 2400], "colour": "redline"}]}
```

**`bar`**: horizontal when the rect is wider than tall, otherwise vertical; fills from the low end. `bands` are a strip along the track; `colour` is the fill.
```json
{"type": "bar", "rect": [16, 364, 300, 48], "channel": "engine.{side}.load", "range": [0, 100], "unit": "pct", "decimals": 0, "text": "LOAD"}
```

**`value`**: a number with its caption (top left) and unit (after the number), rounded half away from zero to `decimals` (default 0). With `"format": "gear"` on a `shift-phase` channel it shows NEUTRAL, AHEAD, ASTERN or SHIFTING, as the control model reports the gear (direction from the lever as applied, or the shaft when the lever reading is lost); SHIFTING is drawn in the shifting colour.
```json
{"type": "value", "rect": [336, 52, 216, 70], "channel": "engine.{side}.oil-pressure", "unit": "psi", "decimals": 0, "text": "OIL PRESS"}
{"type": "value", "rect": [336, 286, 216, 70], "channel": "engine.{side}.shift-phase", "format": "gear", "text": "GEAR"}
```

**`lamp`**: one annunciator tile following one alarm id the vessel defines (`vessels/instruments/<class>.json`). Lit it fills with `colour` (default `alarm`) and its caption takes the paired text colour; dark it is an outlined face. Without `text` the caption is the alarm id in words.
```json
{"type": "lamp", "rect": [16, 52, 180, 48], "alarm": "steering.failure", "text": "STEERING FAIL"}
```

**`list`**: every alarm the readout defines, headed and ordered by alarm group (steering, engine, navigation, hull) and in the readout's order within a group, each with a lamp and its state in words (ALARM, ACK, CLEARED, TEST), so nothing relies on colour or flashing alone. `group` keeps one group; `text` shows when the vessel defines none. Rows are an eighth of the list's height, smaller when more are needed. Prefer a list to lamps when the vessel's alarms may grow: a lamp names an id, a list fills itself.
```json
{"type": "list", "rect": [16, 52, 768, 360], "page": "alarms", "group": "Engine", "text": "NO ENGINE ALARMS"}
```

**`softkey`**: a key, with a `target` page or an `action` (`acknowledge`, `silence`, `lamp-test`), not both. A key with a target is filled while its page shows; any key is filled while held. `colour` is the fill (default `engaged`).
```json
{"type": "softkey", "rect": [16, 424, 140, 44], "text": "ALARMS", "target": "alarms"}
{"type": "softkey", "rect": [432, 412, 192, 52], "text": "LAMP TEST", "action": "lamp-test"}
```

`format`, `group`, `action`, `bands` and the `list` type were appended to C6 in v0.5 (append only; every earlier skin still loads).

## Where skins come from

`DisplaySkinLibrary.Load(display, id)` looks first for `<persistent data path>/skins/<display>/<id>.json` (`DisplaySkinLibrary.DefaultPrivateFolder`), then for the shipped `Resources/FerrySimUI/Skins/<display>/<id>.json`. A private skin that does not load, or whose contents name another display or id, is reported (a warning in the log, `Problems`, `ProblemReported`) and the shipped one is used, so a bad file never blanks a console. A private skin with an id the repository does not ship loads on its own.

**Private skins are never shipped or committed.** They are where a faithful copy of a real unit's screen belongs. Skins in the repository are generic look-alikes: they may match a real unit's fields, positions, colours and scales, and carry no maker names, model names or logos.

The shipped skins are `engine/generic-a` (a main page with engine speed, load, boost, oil pressure, coolant and exhaust temperature, fuel rate, gear oil pressure, shaft speed, gear and volts; an alarms page listing the engine group's alarms) and `alarm-panel/generic-a` (every alarm, grouped, with ACK, SILENCE and LAMP TEST). Both are original layouts. Their ranges, bands, units and refresh rates are guesses until crew photos arrive (`docs/reference-capture-kit.md`, SCR-03 for the engine displays and SCR-05 for the alarm panels).

## From a close-up photo to a skin

1. **Collect.** For each screen and page: a photo taken square-on (lens axis perpendicular to the glass, the whole screen in frame, no glare), the same page at idle, clutched in, cruise and full ahead (SCR-03), and for the alarm panel a lamp test and each tile lit (SCR-05). Note the screen's pixel resolution if the unit shows it in a settings page, and which units it uses. The photos stay in the private reference folder; they never enter the repository.
2. **Rectify.** Correct the perspective in any image editor so the screen is a true rectangle, and scale it to the reference size (the screen's resolution, or its aspect ratio at, say, 800 pixels wide). Set `reference` to that size.
3. **Measure.** With the editor's ruler or a grid overlay, read `[x, y, width, height]` for every field, dial, bar, lamp, key and box. Read each dial's end values and where its needle points at them (`range`, `sweep`), its warning and red-line arcs (`bands`), each number's decimals and unit, and which page each element is on.
4. **Colour.** Sample the day colours with the eyedropper into `#RRGGBB` only where no palette role is close; the night colours are derived. Check the night photo against the derived colours and prefer a role where they differ.
5. **Name the channels.** Map each field to a channel name from `ChannelNames` (`engine.{side}.oil-pressure`); a field the instrument model does not compute has no element (list it for the instrument model owner instead). Lamps take the alarm ids in `vessels/instruments/<class>.json`; prefer a `list` where the panel simply lists alarms.
6. **Load it privately first.** Put the file in `<persistent data path>/skins/<display>/<id>.json`; the log names any problem. Compare a capture with the photo.
7. **For the repository**, make a generic look-alike: the same fields, positions, units, scales and colour roles, with no maker or model names, logos or product artwork, and credit the source by role ("crew reference photos (private)") in the report, never in the file.

## Tests

- EditMode (`Tests/EditMode/Displays`): `SkinLoaderTests` (every rejection, `{side}`, colours and night derivation), `UnitConversionTests` (every unit against hand-checked values, both directions), `SkinnedDisplayTests` (dial, bar, value, gear, pages, softkeys, lamp phases and lamp test, refresh rate, no readout), `AlarmPanelViewTests` (grouping, events, lamp test, forwarding), `DisplayAllocationTests` (zero bytes), `DisplaySkinLibraryTests` (private first, broken private reported, the shipped skins against the T-class alarm file).
- PlayMode (`Tests/PlayMode/Displays`): `DisplayPlayModeTests` renders each generic display into a texture at its reference size and at 2×, by day and night, checks it fills the texture at the right scale (known pixels at known rects), that night has no blue, green or white light, and that every visible text meets 4.5:1 by day and 3:1 (readings) or 2:1 (captions) by night. It writes `artifacts/display-skins/<case>-<day|night>-<1x|2x>.png` and `contrast.txt` (not committed).
