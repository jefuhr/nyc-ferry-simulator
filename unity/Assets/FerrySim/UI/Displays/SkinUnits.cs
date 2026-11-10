using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Instruments;

namespace FerrySim.UI.Displays
{
	/// <summary>The display units a skin may name (contract C6), in schema order.</summary>
	public enum DisplayUnit
	{
		Rpm,
		Knots,
		Degrees,
		DegreesPerMinute,
		Psi,
		Bar,
		Kilopascals,
		Celsius,
		Fahrenheit,
		Volts,
		LitresPerHour,
		GallonsPerHour,
		Percent,
		Metres,
		Feet,

		/// <summary>The channel's SI value, unconverted.</summary>
		None,
	}

	/// <summary>What a channel measures, so a skin cannot show engine speed in psi.</summary>
	public enum ChannelQuantity
	{
		/// <summary>A ratio: levers, tiller and jog in [-1, 1], engine load in [0, 1].</summary>
		Ratio,
		AngularSpeed,
		Speed,
		Angle,
		AngularRate,
		Length,
		Pressure,
		Temperature,
		MassFlow,
		Voltage,

		/// <summary>A numbered state, such as the gearbox shift phase.</summary>
		State,

		/// <summary>0 or 1.</summary>
		Flag,
	}

	/// <summary>
	/// Every conversion a skin can ask for, in one place. Channels are SI (docs/conventions.md); a skin's ranges and
	/// numbers are in its display unit, so a value is converted here at the moment it is shown and nowhere else.
	/// Factors come from <see cref="Units"/> and <see cref="Angles"/> where they exist; the others are defined below.
	/// </summary>
	public static class SkinUnits
	{
		/// <summary>
		/// Fuel density used to turn the fuel-rate channel (kg/s) into a volume flow, kg per litre. 0.84 sits inside
		/// the 0.820 to 0.845 kg/L range that the EN 590 automotive diesel specification allows at 15 °C, and near
		/// the typical density of US No. 2 ultra-low-sulphur diesel. It is an estimate: the vessels' fuel and its
		/// temperature are not known, and a real engine screen reports its own calculated rate.
		/// </summary>
		public const double FuelDensityKilogramsPerLitre = 0.84;

		/// <summary>One US liquid gallon, exactly 231 cubic inches.</summary>
		public const double LitresPerUsGallon = 3.785411784;

		/// <summary>One pound-force per square inch: 0.45359237 kg × 9.80665 m/s² over (0.0254 m)².</summary>
		public const double PascalsPerPsi = 0.45359237 * 9.80665 / (0.0254 * 0.0254);

		public const double PascalsPerBar = 100000.0;

		public const double PascalsPerKilopascal = 1000.0;

		/// <summary>0 °C in kelvin.</summary>
		public const double KelvinAtZeroCelsius = 273.15;

		private const double SecondsPerHour = 3600.0;
		private const double SecondsPerMinute = 60.0;

		// Indexed by DisplayUnit; the schema's names and the symbols a screen prints.
		private static readonly string[] Names = { "rpm", "kn", "deg", "deg/min", "psi", "bar", "kPa", "degC", "degF", "V", "L/h", "gal/h", "pct", "m", "ft", "none" };
		private static readonly string[] Symbols = { "rpm", "kn", "°", "°/min", "psi", "bar", "kPa", "°C", "°F", "V", "L/h", "gal/h", "%", "m", "ft", "" };

		/// <summary>Finds a unit by its schema name. Names are case-sensitive.</summary>
		public static bool TryParse(string name, out DisplayUnit unit)
		{
			for (var i = 0; i < Names.Length; i++)
			{
				if (string.Equals(Names[i], name, StringComparison.Ordinal))
				{
					unit = (DisplayUnit)i;
					return true;
				}
			}

			unit = DisplayUnit.None;
			return false;
		}

		/// <summary>The schema name of <paramref name="unit"/>.</summary>
		public static string Name(DisplayUnit unit) => Names[(int)unit];

		/// <summary>What a screen prints after the number; empty for <see cref="DisplayUnit.None"/>.</summary>
		public static string Symbol(DisplayUnit unit) => Symbols[(int)unit];

		/// <summary>Converts a channel's SI value to <paramref name="unit"/>. NaN stays NaN.</summary>
		public static double FromSi(DisplayUnit unit, double si)
		{
			switch (unit)
			{
				case DisplayUnit.Rpm: return Units.RadiansPerSecondToRpm(si);
				case DisplayUnit.Knots: return Units.MetresPerSecondToKnots(si);
				case DisplayUnit.Degrees: return Angles.ToDegrees(si);
				case DisplayUnit.DegreesPerMinute: return Angles.ToDegrees(si) * SecondsPerMinute;
				case DisplayUnit.Psi: return si / PascalsPerPsi;
				case DisplayUnit.Bar: return si / PascalsPerBar;
				case DisplayUnit.Kilopascals: return si / PascalsPerKilopascal;
				case DisplayUnit.Celsius: return si - KelvinAtZeroCelsius;
				case DisplayUnit.Fahrenheit: return (si - KelvinAtZeroCelsius) * 1.8 + 32.0;
				case DisplayUnit.Volts: return si;
				case DisplayUnit.LitresPerHour: return si * SecondsPerHour / FuelDensityKilogramsPerLitre;
				case DisplayUnit.GallonsPerHour: return si * SecondsPerHour / FuelDensityKilogramsPerLitre / LitresPerUsGallon;
				case DisplayUnit.Percent: return si * 100.0;
				case DisplayUnit.Metres: return si;
				case DisplayUnit.Feet: return si / Units.MetresPerFoot;
				default: return si;
			}
		}

		/// <summary>Converts a value in <paramref name="unit"/> back to SI: the inverse of <see cref="FromSi"/>.</summary>
		public static double ToSi(DisplayUnit unit, double value)
		{
			switch (unit)
			{
				case DisplayUnit.Rpm: return Units.RpmToRadiansPerSecond(value);
				case DisplayUnit.Knots: return Units.KnotsToMetresPerSecond(value);
				case DisplayUnit.Degrees: return Angles.ToRadians(value);
				case DisplayUnit.DegreesPerMinute: return Angles.ToRadians(value / SecondsPerMinute);
				case DisplayUnit.Psi: return value * PascalsPerPsi;
				case DisplayUnit.Bar: return value * PascalsPerBar;
				case DisplayUnit.Kilopascals: return value * PascalsPerKilopascal;
				case DisplayUnit.Celsius: return value + KelvinAtZeroCelsius;
				case DisplayUnit.Fahrenheit: return (value - 32.0) / 1.8 + KelvinAtZeroCelsius;
				case DisplayUnit.Volts: return value;
				case DisplayUnit.LitresPerHour: return value * FuelDensityKilogramsPerLitre / SecondsPerHour;
				case DisplayUnit.GallonsPerHour: return value * LitresPerUsGallon * FuelDensityKilogramsPerLitre / SecondsPerHour;
				case DisplayUnit.Percent: return value / 100.0;
				case DisplayUnit.Metres: return value;
				case DisplayUnit.Feet: return Units.FeetToMetres(value);
				default: return value;
			}
		}

		/// <summary>The quantity a unit shows; <see cref="DisplayUnit.None"/> fits every quantity.</summary>
		private static bool Shows(DisplayUnit unit, ChannelQuantity quantity)
		{
			switch (unit)
			{
				case DisplayUnit.Rpm: return quantity == ChannelQuantity.AngularSpeed;
				case DisplayUnit.Knots: return quantity == ChannelQuantity.Speed;
				case DisplayUnit.Degrees: return quantity == ChannelQuantity.Angle;
				case DisplayUnit.DegreesPerMinute: return quantity == ChannelQuantity.AngularRate;
				case DisplayUnit.Psi:
				case DisplayUnit.Bar:
				case DisplayUnit.Kilopascals: return quantity == ChannelQuantity.Pressure;
				case DisplayUnit.Celsius:
				case DisplayUnit.Fahrenheit: return quantity == ChannelQuantity.Temperature;
				case DisplayUnit.Volts: return quantity == ChannelQuantity.Voltage;
				case DisplayUnit.LitresPerHour:
				case DisplayUnit.GallonsPerHour: return quantity == ChannelQuantity.MassFlow;
				case DisplayUnit.Percent: return quantity == ChannelQuantity.Ratio;
				case DisplayUnit.Metres:
				case DisplayUnit.Feet: return quantity == ChannelQuantity.Length;
				default: return true;
			}
		}

		/// <summary>True when <paramref name="unit"/> can show <paramref name="channel"/>.</summary>
		public static bool Fits(DisplayUnit unit, Channel channel) => unit == DisplayUnit.None || Shows(unit, Quantity(channel));

		/// <summary>What <paramref name="channel"/> measures (its SI unit is in <see cref="Channel"/>).</summary>
		public static ChannelQuantity Quantity(Channel channel)
		{
			switch (channel)
			{
				case Channel.LeverPort:
				case Channel.LeverStarboard:
				case Channel.TillerOrder:
				case Channel.SteeringJog:
				case Channel.EnginePortLoad:
				case Channel.EngineStarboardLoad:
					return ChannelQuantity.Ratio;
				case Channel.NfuActive:
					return ChannelQuantity.Flag;
				case Channel.RudderOrder:
				case Channel.RudderPort:
				case Channel.RudderStarboard:
				case Channel.Heading:
				case Channel.CourseOverGround:
					return ChannelQuantity.Angle;
				case Channel.RateOfTurn:
					return ChannelQuantity.AngularRate;
				case Channel.SpeedOverGround:
				case Channel.SpeedThroughWater:
					return ChannelQuantity.Speed;
				case Channel.DepthUnderKeel:
					return ChannelQuantity.Length;
				case Channel.EnginePortSpeed:
				case Channel.EnginePortShaftSpeed:
				case Channel.EngineStarboardSpeed:
				case Channel.EngineStarboardShaftSpeed:
					return ChannelQuantity.AngularSpeed;
				case Channel.EnginePortShiftPhase:
				case Channel.EngineStarboardShiftPhase:
					return ChannelQuantity.State;
				case Channel.EnginePortOilPressure:
				case Channel.EnginePortBoostPressure:
				case Channel.EnginePortGearOilPressure:
				case Channel.EngineStarboardOilPressure:
				case Channel.EngineStarboardBoostPressure:
				case Channel.EngineStarboardGearOilPressure:
					return ChannelQuantity.Pressure;
				case Channel.EnginePortCoolantTemperature:
				case Channel.EnginePortExhaustTemperature:
				case Channel.EngineStarboardCoolantTemperature:
				case Channel.EngineStarboardExhaustTemperature:
					return ChannelQuantity.Temperature;
				case Channel.EnginePortFuelRate:
				case Channel.EngineStarboardFuelRate:
					return ChannelQuantity.MassFlow;
				case Channel.BusVoltage:
					return ChannelQuantity.Voltage;
				default:
					// A channel appended to C1 after this list: shown only unconverted until it is classified here.
					throw new ArgumentOutOfRangeException(nameof(channel), channel, "channel has no display quantity yet; add it to SkinUnits.Quantity");
			}
		}
	}
}
