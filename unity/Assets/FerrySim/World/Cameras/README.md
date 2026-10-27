# Wheelhouse bow view

B toggles the bow-deck preset in wheelhouse mode only. Shift+B switches automatic tilt on or off, initially on and remembered for the session across run and scene changes. Other camera modes neither consume these keys nor apply the preset.

The look angle is atan2 of the vertical drop from `GreyboxDimensions.BridgeEyeHeight` to `Freeboard`, and the forward distance from `BridgeEyeForward` to the deck front at 0.45 × length overall. The direction is transformed into the mount frame first, so its existing 8° down tilt is not counted twice; the mount pitch and yaw limits clamp it. A 0.25 s exponential transition smoothly enters and leaves the view; head look remains within the mount limits.

Automatic tilt enters at bow-to-face distance ≤20 m and ground speed ≤1 m/s (about 1.94 kn), and leaves beyond 30 m or above 1.5 m/s (about 2.92 kn). These thresholds are estimated UI choices. Queries use the run's contact geometry, not rendered meshes: only water-side faces facing the bow within 60° qualify. At each run start, `BowApproachFaces.From` selects eligible berth-face or primary-face segments from the loaded landing geometry, including all selectable berths. The scene installs that cached geometry on the mount. Piers, bulkheads and other walls cannot trigger the preset, and an unconfigured mount has empty approach geometry. The fixed 1,024-segment query buffer exceeds the slice's complete contact set and is reused each frame.

Right-mouse hand look or middle-mouse recentring suppresses automatic tilt until the approach exits and a new one begins. B can still select the preset explicitly. Leaving B's preset also suppresses the current automatic approach so the toggle can return the view. A run reset clears head look and manual/approach state but keeps the automatic preference. Camera state never enters the simulation or recording. Per-frame queries and transitions allocate no managed objects.

## Review fix

B toggles the visible bow view, whether it was selected manually or automatically. One press while automatic tilt is showing the bow clears the manual latch and suppresses automatic tilt for the rest of that approach; leaving and entering a new approach re-arms it. When the bow view is off, B selects the manual preset. Shift+B still changes only the session automatic preference, and hand look still overrides the current approach. Run reset clears manual/suppression state and held helm repeat while preserving that preference.

The regression is covered both by pure approach-state tests and by keyboard/mouse input against a camera, eligible face, helm controller and real runner reset. It was executed red before the state change. The preset angle, smoothing and automatic-entry rendering are unchanged.
