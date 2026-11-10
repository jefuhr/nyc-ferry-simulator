# Instrument data

These files are separate from the pinned `vessels/<class>.json` dynamics parameters. Changing a display curve must not change a recording's vessel hash, simulation state or digest.

Every numerical model value uses the repository's `value`, `unit`, `source`, `confidence` shape. Instrument quantities additionally need SI kelvin (`K`), volts (`V`) and mass flow (`kg/s`); their loader validates these without changing the frozen vessel parameter unit contract. Rated speed and power are copied from the existing published machinery particulars in the parent vessel file. All other values are simulator estimates, including warm initial temperatures, pressure curves, fuel consumption, lags, voltage and alarm thresholds. No equipment identity is needed to use these data.

Engine curves contain an idle reading, a rise over idle-to-rated speed, a rise over zero-to-full transmitted load, and a first-order time constant. Fuel load rise uses an estimated 0.00000006 kg/J (216 g/kWh) multiplied by the published rated power. It is not a measured consumption curve. The first observation initializes a warm engine; there is no cold-start or cooling-after-shutdown model.

Better data would be simultaneous warm readings of speed, actual load, oil pressure, coolant temperature, exhaust temperature, boost, fuel mass flow and gearbox oil pressure at idle, cruise and full power, plus time-stamped throttle/shift transients. Bus-voltage measurements and crew-approved alarm thresholds/repeat policies would replace the remaining display assumptions. Steering release behavior requires crew confirmation; the default remains return-to-tiller and no parent vessel file is edited here.
