using System;
using System.Globalization;
using FerrySim.Core.Contracts;

namespace FerrySim.Cli
{
	/// <summary>
	/// The <c>--env</c> option: <c>calm</c>, or comma-separated <c>current=</c>, <c>wind=</c> and
	/// <c>depth=</c> items, for example <c>current=1.5kn@200,wind=15kn@from300,depth=8m</c>.
	/// Directions follow docs/conventions.md: a current is the direction it flows toward and a wind is
	/// the direction it blows from. A <c>to</c> or <c>from</c> prefix makes the sense explicit.
	/// </summary>
	public sealed class EnvironmentSpec
	{
		private EnvironmentSpec(string text, HorizontalVector current, HorizontalVector wind, double depth)
		{
			Text = text;
			Current = current;
			Wind = wind;
			Depth = depth;
		}

		/// <summary>The spec as the user wrote it (trimmed, lower case); recorded in the CSV header.</summary>
		public string Text { get; }

		/// <summary>Current velocity, direction toward, m/s.</summary>
		public HorizontalVector Current { get; }

		/// <summary>Wind velocity, direction toward, m/s.</summary>
		public HorizontalVector Wind { get; }

		/// <summary>Water depth, metres.</summary>
		public double Depth { get; }

		public static EnvironmentSpec Calm => Parse("calm");

		public static EnvironmentSpec Parse(string spec)
		{
			var text = (spec ?? string.Empty).Trim().ToLowerInvariant();
			if (text.Length == 0)
			{
				throw new UsageException("Environment spec is empty; use 'calm' or items like current=1.5kn@200.");
			}

			var defaults = UniformEnvironment.Calm;
			if (text == "calm")
			{
				return new EnvironmentSpec(text, defaults.Current, defaults.Wind, defaults.Depth);
			}

			var current = defaults.Current;
			var wind = defaults.Wind;
			var depth = defaults.Depth;
			var seen = new System.Collections.Generic.HashSet<string>();
			foreach (var rawItem in text.Split(','))
			{
				var item = rawItem.Trim();
				var equals = item.IndexOf('=');
				if (equals <= 0 || equals == item.Length - 1)
				{
					throw new UsageException($"Environment item '{item}' must look like key=value (current, wind or depth).");
				}

				var key = item.Substring(0, equals).Trim();
				var value = item.Substring(equals + 1).Trim();
				if (!seen.Add(key))
				{
					throw new UsageException($"Environment item '{key}' given more than once.");
				}

				switch (key)
				{
					case "current":
						current = ParseFlow(key, value, defaultFrom: false);
						break;
					case "wind":
						wind = ParseFlow(key, value, defaultFrom: true);
						break;
					case "depth":
						depth = ParseDepth(value);
						break;
					default:
						throw new UsageException($"Unknown environment item '{key}'; expected current, wind or depth.");
				}
			}

			return new EnvironmentSpec(text, current, wind, depth);
		}

		public UniformEnvironment CreateEnvironment() => new UniformEnvironment(Current, Wind, Depth);

		private static HorizontalVector ParseFlow(string key, string value, bool defaultFrom)
		{
			var at = value.IndexOf('@');
			if (at <= 0 || at == value.Length - 1)
			{
				throw new UsageException($"{key}={value}: expected <speed><unit>@<direction>, for example {key}=1.5kn@200.");
			}

			var speed = ParseQuantity(key, value.Substring(0, at), out var unit, "kn", "kt", "m/s", "mps");
			if (speed < 0.0)
			{
				throw new UsageException($"{key}={value}: speed cannot be negative.");
			}

			var speedMetresPerSecond = unit == "kn" || unit == "kt" ? Units.KnotsToMetresPerSecond(speed) : speed;

			var direction = value.Substring(at + 1);
			var from = defaultFrom;
			if (direction.StartsWith("from", StringComparison.Ordinal))
			{
				from = true;
				direction = direction.Substring(4);
			}
			else if (direction.StartsWith("to", StringComparison.Ordinal))
			{
				from = false;
				direction = direction.Substring(2);
			}

			if (!double.TryParse(direction, NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees) ||
				double.IsNaN(degrees) || degrees < 0.0 || degrees > 360.0)
			{
				throw new UsageException($"{key}={value}: direction must be degrees true in [0, 360].");
			}

			var radians = Angles.ToRadians(degrees);
			return from
				? HorizontalVector.FromSpeedFrom(speedMetresPerSecond, radians)
				: HorizontalVector.FromSpeedToward(speedMetresPerSecond, radians);
		}

		private static double ParseDepth(string value)
		{
			var depth = ParseQuantity("depth", value, out var unit, "m", "ft");
			if (depth <= 0.0)
			{
				throw new UsageException($"depth={value}: depth must be positive.");
			}

			return unit == "ft" ? Units.FeetToMetres(depth) : depth;
		}

		private static double ParseQuantity(string key, string text, out string unit, params string[] units)
		{
			text = text.Trim();
			foreach (var candidate in units)
			{
				if (!text.EndsWith(candidate, StringComparison.Ordinal))
				{
					continue;
				}

				var number = text.Substring(0, text.Length - candidate.Length).Trim();
				if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
					!double.IsNaN(parsed) && !double.IsInfinity(parsed))
				{
					unit = candidate;
					return parsed;
				}
			}

			throw new UsageException($"{key}: '{text}' needs a number with one of the units {string.Join(", ", units)}.");
		}
	}
}
