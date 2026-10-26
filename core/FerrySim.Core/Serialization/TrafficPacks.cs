using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FerrySim.Core.Traffic;

namespace FerrySim.Core.Serialization
{
	/// <summary>
	/// Finds traffic packs (ferrysim-traffic/1) by their day, yyyy-MM-dd, in the layout the data pipeline writes:
	/// <c>&lt;root&gt;/harbor/traffic/&lt;date&gt;</c>, each root laid out like <c>data/processed</c>. The roots are searched in order;
	/// Unity's default is StreamingAssets' <c>data/processed</c>, then the repository's. Finding a day reads only that its manifest
	/// exists; <see cref="Load"/> verifies every byte (<see cref="TrafficPackJson"/>).
	/// </summary>
	public sealed class TrafficPacks
	{
		/// <summary>The packs' folder under a root: <c>harbor/traffic</c>.</summary>
		public const string Folder = "harbor/traffic";

		private const string DateFormat = "yyyy-MM-dd";

		private readonly string[] _roots;

		/// <summary>Packs under <paramref name="roots"/>, absolute folders searched in order, each laid out like <c>data/processed</c>.</summary>
		public TrafficPacks(params string[] roots)
		{
			if (roots == null || roots.Length == 0)
			{
				throw new ArgumentException("At least one folder to look for traffic packs in is needed.", nameof(roots));
			}

			_roots = new string[roots.Length];
			for (var i = 0; i < roots.Length; i++)
			{
				_roots[i] = Path.GetFullPath(roots[i] ?? throw new ArgumentNullException(nameof(roots)));
			}
		}

		/// <summary>The folders searched, in order.</summary>
		public IReadOnlyList<string> Roots => _roots;

		/// <summary>True for a day the packs can be named by: yyyy-MM-dd, a real date. Nothing else can reach outside the pack folders.</summary>
		public static bool IsDay(string date) =>
			date != null && date.Length == DateFormat.Length && DateTime.TryParseExact(date, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

		/// <summary>The first root's day folder with a manifest, or null. Throws <see cref="ArgumentException"/> for a day that is not yyyy-MM-dd.</summary>
		public string DayFolder(string date)
		{
			if (!IsDay(date))
			{
				throw new ArgumentException($"traffic day '{date}' is not yyyy-MM-dd, for example 2025-07-14.", nameof(date));
			}

			foreach (var root in _roots)
			{
				var folder = Path.Combine(root, "harbor", "traffic", date);
				if (File.Exists(Path.Combine(folder, TrafficPackJson.ManifestFile)))
				{
					return folder;
				}
			}

			return null;
		}

		/// <summary>What a reader needs to know when <paramref name="date"/> is not found: every place looked at.</summary>
		public string NotFound(string date)
		{
			var places = new List<string>();
			foreach (var root in _roots)
			{
				places.Add(Path.Combine(root, "harbor", "traffic", IsDay(date) ? date : "?", TrafficPackJson.ManifestFile));
			}

			return $"traffic pack '{date}' was not found (looked for {string.Join(" and ", places)}); the data pipeline builds packs " +
				"(python -m ferrydata.traffic build)";
		}

		/// <summary>Loads and verifies the day's pack. Throws <see cref="FileNotFoundException"/> when it is not found and <see cref="TrafficPackException"/>, naming the file, for any mismatch.</summary>
		public TrafficPack Load(string date) => Load(date, out _);

		/// <summary>As <see cref="Load(string)"/>, also giving the day folder it was read from.</summary>
		public TrafficPack Load(string date, out string dayFolder)
		{
			dayFolder = DayFolder(date) ?? throw new FileNotFoundException(NotFound(date) + ".");
			var pack = TrafficPackJson.Load(dayFolder);
			if (pack.Date != date)
			{
				throw new TrafficPackException(Path.Combine(dayFolder, TrafficPackJson.ManifestFile), $"is the pack of {pack.Date}, but it is in the folder of {date}.");
			}

			return pack;
		}

		/// <summary>The days with a pack, oldest first, from the first root that has the packs' folder.</summary>
		public List<string> Days()
		{
			var days = new List<string>();
			foreach (var root in _roots)
			{
				var folder = Path.Combine(root, "harbor", "traffic");
				if (!Directory.Exists(folder))
				{
					continue;
				}

				foreach (var path in Directory.GetDirectories(folder))
				{
					var name = Path.GetFileName(path);
					if (IsDay(name) && File.Exists(Path.Combine(path, TrafficPackJson.ManifestFile)))
					{
						days.Add(name);
					}
				}

				days.Sort(StringComparer.Ordinal);
				return days;
			}

			return days;
		}
	}
}
