# Wheelhouse instruments

`InstrumentModel` is a viewer model outside the simulation state and digest. Construct it once from the separate class data, then call `Step(state, telemetry, controls, taps)` after each simulation step. `VesselDynamicsSimulation` implements the read-only `IInstrumentTaps`; no instrument operation writes to the simulation.

```csharp
var parameters = InstrumentParametersJson.Load("vessels/instruments/t-class.json");
var instruments = new InstrumentModel(parameters);
simulation.Step(controls);
instruments.Step(simulation.State, simulation.Telemetry, controls, simulation);
var oilPressurePascals = instruments.Value(Channel.EnginePortOilPressure);
```

The JSON loader belongs to `FerrySim.Core.Serialization`. The model and data types have no JSON or Unity dependency. All values use SI, including kelvin, pascals, rad/s and kg/s. Before the first step, channels are unavailable (`NaN`). The first step initializes the gauges at their warm steady readings; later steps use elapsed simulation time for exact first-order lags. Call `Reset` when the run changes; backwards simulation time also resets the model. Repeated observations at the same time do not advance lags or alarm repeats.

Each engine curve is `idle + speedRise * speedFraction + loadRise * load`, where speedFraction runs from idle to rated speed and both fractions are clamped to [0, 1]. Load is transmitted clutch torque referred to the engine, divided by available full-load brake torque, rather than lever position. Each gauge and load has its own first-order lag. These are estimated warm-running display responses, not an engine thermal, fuel-system, lubrication or electrical simulation. Bus voltage is an explicitly estimated constant. Missing published gauge data must not acquire a `published` confidence by association with the published engine rating.

## Alarms

The data defines only shallow water, grounding, contact and steering failure. Grounding means negative depth under keel; contact means contact force above its data threshold. `SetShallowWaterThreshold(double? metres)` replaces only the shallow-water condition threshold with a finite, non-negative viewer setting; `null` and `Reset` restore the class-file default. It is never recorded or included in the physics digest. The harbor reapplies the saved setting at composition, on changes and on every new run/class. Shallow water raises on the next observed step, with no raise delay or hysteresis. Shallow-water clearance defaults to 1.524 m; it is a display estimate, not a safe operating limit. A steering discrepancy must persist beyond the time needed for a full hard-over-to-hard-over transit plus the data's extra delay. Every change of ordered rudder restarts that grace period, so repeated tiller reversals cannot alarm a healthy rate-limited steering gear. There is no fault injection or engine-failure alarm in v0.5.

`AlarmAnnunciator` owns raise, acknowledge, silence, ring-back, repeat and lamp-test state. `Acknowledge` and `Silence` accept an alarm group; the instrument model also offers all-group overloads. Silence leaves flashing and acknowledgement outstanding. A fresh raise or repeat sounds again. A cleared unacknowledged condition remains in `RingBack` until acknowledged. Repeat uses simulation time from acknowledgement; missing or zero `reRaiseSeconds` means never. Lamp test lights the panel lamps without changing alarm phases or sounding an alarm. Flash helpers use simulation time: 1 Hz unacknowledged and 0.5 Hz ring-back, both with 50 percent duty cycle. All viewers use `AlarmAnnunciator.LampLit(phase, lampTest, simulationTime)` so paused frames and flash edges agree.

Each consumer polls `[FirstEventSequence, NextEventSequence)` with its own sequence cursor. The log stores the latest 256 events with monotonic sequence numbers. An overwritten or future sequence throws; a slow consumer resynchronizes from current phases and sounders. Events carry the physics step of the last observation. Viewer actions are never inputs, recordings or state digest fields.

## Verification

The .NET suites in `core/dotnet/FerrySim.Core.Tests/Instruments` cover the state machine, bounded event log, estimated steady readings, independent engine lags, parameter validation and zero allocation in `Step`. Steering and zero-jog bit identity live in `Dynamics/Nfu*Tests.cs`. Performance measurements are evidence for the measured runtime, not a cross-platform timing guarantee.
