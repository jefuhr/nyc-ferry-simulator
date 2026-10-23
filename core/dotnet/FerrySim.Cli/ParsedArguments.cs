using System;
using System.Collections.Generic;
using System.Globalization;

namespace FerrySim.Cli
{
	/// <summary>
	/// Hand-rolled argument parsing: positionals, <c>--name value</c> / <c>--name=value</c> options
	/// and bare flags. Anything not declared by the command is a usage error.
	/// </summary>
	public sealed class ParsedArguments
	{
		private readonly Dictionary<string, string> _options = new Dictionary<string, string>(StringComparer.Ordinal);
		private readonly HashSet<string> _flags = new HashSet<string>(StringComparer.Ordinal);

		private readonly List<string> _positionals = new List<string>();

		private ParsedArguments()
		{
		}

		public IReadOnlyList<string> Positionals => _positionals;

		public static ParsedArguments Parse(IReadOnlyList<string> args, IEnumerable<string> valueOptions, IEnumerable<string> flagOptions)
		{
			var values = new HashSet<string>(valueOptions, StringComparer.Ordinal);
			var flags = new HashSet<string>(flagOptions, StringComparer.Ordinal);
			var parsed = new ParsedArguments();

			for (var i = 0; i < args.Count; i++)
			{
				var arg = args[i];
				if (!arg.StartsWith("--", StringComparison.Ordinal))
				{
					parsed._positionals.Add(arg);
					continue;
				}

				var name = arg.Substring(2);
				string inlineValue = null;
				var equals = name.IndexOf('=');
				if (equals >= 0)
				{
					inlineValue = name.Substring(equals + 1);
					name = name.Substring(0, equals);
				}

				if (flags.Contains(name))
				{
					if (inlineValue != null)
					{
						throw new UsageException($"Option --{name} does not take a value.");
					}

					parsed._flags.Add(name);
				}
				else if (values.Contains(name))
				{
					if (parsed._options.ContainsKey(name))
					{
						throw new UsageException($"Option --{name} given more than once.");
					}

					if (inlineValue == null)
					{
						if (i + 1 >= args.Count)
						{
							throw new UsageException($"Option --{name} needs a value.");
						}

						inlineValue = args[++i];
					}

					parsed._options[name] = inlineValue;
				}
				else
				{
					throw new UsageException($"Unknown option --{name}.");
				}
			}

			return parsed;
		}

		public bool HasFlag(string name) => _flags.Contains(name);

		public bool Has(string name) => _options.ContainsKey(name);

		public string GetString(string name, string fallback = null) => _options.TryGetValue(name, out var value) ? value : fallback;

		public string Require(string name) =>
			_options.TryGetValue(name, out var value) ? value : throw new UsageException($"Missing required option --{name}.");

		public double GetDouble(string name, double fallback, double min = double.NegativeInfinity, double max = double.PositiveInfinity)
		{
			if (!_options.TryGetValue(name, out var text))
			{
				return fallback;
			}

			if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || double.IsNaN(value) || double.IsInfinity(value))
			{
				throw new UsageException($"Option --{name} needs a number, got '{text}'.");
			}

			if (value < min || value > max)
			{
				throw new UsageException($"Option --{name} must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}, got {text}.");
			}

			return value;
		}

		public double? GetOptionalDouble(string name, double min = double.NegativeInfinity, double max = double.PositiveInfinity) =>
			Has(name) ? GetDouble(name, 0.0, min, max) : (double?)null;

		/// <summary>Picks one of <paramref name="allowed"/> (case-insensitive), or the fallback when the option is absent.</summary>
		public string GetChoice(string name, string fallback, params string[] allowed)
		{
			if (!_options.TryGetValue(name, out var text))
			{
				return fallback;
			}

			foreach (var candidate in allowed)
			{
				if (string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase))
				{
					return candidate;
				}
			}

			throw new UsageException($"Option --{name} must be one of {string.Join(", ", allowed)}, got '{text}'.");
		}
	}
}
