using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using FerrySim.Core.Env;
using FerrySim.Core.Contracts;
using FerrySim.Core.Traffic;
using FerrySim.Core.World;
using FerrySim.Core.Serialization;
using UnityEngine;

namespace FerrySim.Sim
{
	/// <summary>
	/// Finds environment packs (ferrysim-env/1) by name, <c>region/date</c> (for example <c>lower-east-river/2025-07-14</c>), in
	/// the layout the data pipeline writes: the day in <c>&lt;root&gt;/&lt;region&gt;/env/days/&lt;date&gt;</c> and the region's
	/// depth set beside it in <c>&lt;root&gt;/&lt;region&gt;/env/depth</c>. The roots are searched in order. <see cref="Default"/>
	/// is StreamingAssets' <c>data/processed</c>, where every player build copies the slice's packs, then the repository's.
	/// Listing a day reads only its manifest (<see cref="EnvironmentPackDay"/>); <see cref="Load"/> verifies every byte through
	/// <see cref="EnvironmentPackJson"/> and refuses any mismatch, naming the file.
	/// </summary>
	public sealed class EnvironmentPacks
	{
		/// <summary>The pipeline's processed outputs, relative to the repository root (and to StreamingAssets in a player).</summary>
		public const string ProcessedFolder = "data/processed";

		private const string ManifestFile = "manifest.json";
		private const string DateFormat = "yyyy-MM-dd";
		private const string EpochFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

		private readonly string[] _roots;
		private VerifiedDepth _harborDepth;
		private string _harborDepthFolder;

		/// <summary>Packs under <paramref name="roots"/>, absolute folders searched in order, each laid out like <c>data/processed</c>.</summary>
		public EnvironmentPacks(params string[] roots)
		{
			if (roots == null || roots.Length == 0)
			{
				throw new ArgumentException("At least one folder to look for environment packs in is needed.", nameof(roots));
			}

			_roots = new string[roots.Length];
			for (var i = 0; i < roots.Length; i++)
			{
				_roots[i] = Path.GetFullPath(roots[i] ?? throw new ArgumentNullException(nameof(roots)));
			}
		}

		/// <summary>StreamingAssets' <c>data/processed</c> first, then the repository's, as <see cref="SimPaths.Resolve"/> reads files.</summary>
		public static EnvironmentPacks Default =>
			new EnvironmentPacks(Path.Combine(Application.streamingAssetsPath, ProcessedFolder), Path.Combine(SimPaths.RepoRoot, ProcessedFolder));

		/// <summary>The folders searched, in order.</summary>
		public IReadOnlyList<string> Roots => _roots;

		/// <summary>A pack's name: <c>region/date</c>.</summary>
		public static string Name(string region, string date) => region + "/" + date;

		/// <summary>The region's pack folder relative to the repository root (and StreamingAssets): <c>data/processed/&lt;region&gt;/env</c>.</summary>
		public static string RegionFolder(string region) => ProcessedFolder + "/" + region + "/env";

		/// <summary>
		/// Splits a pack name into its region (lower-case letters, digits and hyphens) and its date (yyyy-MM-dd). False, with a
		/// reason, for anything else, so a name can never reach outside the pack folders.
		/// </summary>
		public static bool TryParseName(string name, out string region, out string date, out string problem)
		{
			region = null;
			date = null;
			var slash = string.IsNullOrEmpty(name) ? -1 : name.IndexOf('/');
			if (slash > 0 && slash == name.LastIndexOf('/'))
			{
				var regionPart = name.Substring(0, slash);
				var datePart = name.Substring(slash + 1);
				if (IsRegion(regionPart) && datePart.Length == DateFormat.Length &&
					DateTime.TryParseExact(datePart, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
				{
					region = regionPart;
					date = datePart;
					problem = null;
					return true;
				}
			}

			problem = $"environment pack '{name}' is not region/date, for example lower-east-river/2025-07-14";
			return false;
		}

		/// <summary>
		/// America/New_York's offset from UTC on a pack's day, from its local-midnight epoch (never from a time-zone database,
		/// which differs between platforms): -4 h in daylight time, -5 h in standard time.
		/// </summary>
		public static TimeSpan LocalUtcOffset(string date, DateTimeOffset epochUtc)
		{
			var day = DateTime.ParseExact(date, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None);
			return new DateTimeOffset(day.Ticks, TimeSpan.Zero) - epochUtc;
		}

		/// <summary>A time <paramref name="seconds"/> after a pack's epoch, rounded to the tick as the core's coverage report does.</summary>
		public static DateTimeOffset AtPackTime(DateTimeOffset epochUtc, double seconds) =>
			epochUtc.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

		/// <summary>ISO 8601 UTC with as many fractional digits as the time has, as recordings write times.</summary>
		public static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

		/// <summary>The first root's day folder that has a manifest, or null. Throws <see cref="ArgumentException"/> for a malformed name.</summary>
		public string DayFolder(string name)
		{
			if (!TryParseName(name, out var region, out var date, out var problem))
			{
				throw new ArgumentException(problem + ".", nameof(name));
			}

			foreach (var root in _roots)
			{
				var folder = Path.Combine(root, region, "env", "days", date);
				if (File.Exists(Path.Combine(folder, ManifestFile)))
				{
					return folder;
				}
			}

			return null;
		}

		/// <summary>What a reader needs to know when <paramref name="name"/> is not found: every place looked at.</summary>
		public string NotFound(string name)
		{
			TryParseName(name, out var region, out var date, out _);
			var places = new List<string>();
			foreach (var root in _roots)
			{
				places.Add(Path.Combine(root, region ?? "?", "env", "days", date ?? "?", ManifestFile));
			}

			return $"environment pack '{name}' was not found (looked for {string.Join(" and ", places)}); the data pipeline builds packs " +
				"(python -m ferrydata.environment build), and a player carries them in StreamingAssets";
		}

		/// <summary>
		/// The day's manifest summary: date, epoch, coverage and whether it has wind. Read without the byte checks
		/// <see cref="Load"/> makes, for listing days and checking a start. Throws <see cref="FileNotFoundException"/> when the
		/// pack is not found and <see cref="FormatException"/>, naming the file, when its manifest is not a day manifest.
		/// </summary>
		public EnvironmentPackDay ReadDay(string name)
		{
			var folder = DayFolder(name) ?? throw new FileNotFoundException(NotFound(name) + ".");
			TryParseName(name, out var region, out var date, out _);
			return EnvironmentPackDay.Read(folder, region, date);
		}

		/// <summary>
		/// Why a run of pack <paramref name="name"/> cannot start at <paramref name="startUtc"/>, or null when it can: a name
		/// that is not region/date, a pack that is not found or whose manifest is unreadable, or a start outside the day's
		/// coverage.
		/// </summary>
		public string StartProblem(string name, DateTimeOffset startUtc)
		{
			if (!TryParseName(name, out _, out _, out var problem))
			{
				return problem;
			}

			EnvironmentPackDay day;
			try
			{
				day = ReadDay(name);
			}
			catch (Exception error) when (error is FileNotFoundException || error is FormatException || error is IOException || error is UnauthorizedAccessException)
			{
				return error.Message.TrimEnd('.');
			}

			return day.Covers(startUtc)
				? null
				: $"it starts at {Iso(startUtc)}, outside environment pack '{name}', which covers {Iso(day.CoverageStartUtc)} to {Iso(day.CoverageEndUtc)}";
		}

		/// <summary>
		/// Loads and verifies the pack: every file against its manifest's SHA-256, both DIGEST files, the day's depth digest and
		/// the manifests (<see cref="EnvironmentPackJson"/>). Throws <see cref="FileNotFoundException"/> when it is not found and
		/// <see cref="EnvironmentPackException"/>, naming the file, for any mismatch.
		/// </summary>
		public EnvironmentPack Load(string name) => Load(name, out _);

		/// <summary>As <see cref="Load(string)"/>, also giving the day folder it was read from.</summary>
		public EnvironmentPack Load(string name, out string dayFolder)
		{
			dayFolder = DayFolder(name) ?? throw new FileNotFoundException(NotFound(name) + ".");
			var pack = EnvironmentPackJson.Load(dayFolder);
			if (pack.Name != name)
			{
				throw new EnvironmentPackException(Path.Combine(dayFolder, ManifestFile), $"is the pack {pack.Name}, but it is in the folder of {name}.");
			}

			return pack;
		}

		/// <summary>Prepares all resident harbor inputs before the runner changes any current binding.</summary>
		public PreparedHarborStart PrepareHarbor(ScenarioSettings settings, string runtimeFolder,
			Func<string, string, IWorldGeometry> parseContacts, TrafficPacks trafficPacks = null)
		{
			if (settings == null)
			{
				throw new ArgumentNullException(nameof(settings));
			}
			if (parseContacts == null)
			{
				throw new ArgumentNullException(nameof(parseContacts));
			}

			var started = Stopwatch.GetTimestamp();
			settings.Validate(this, trafficPacks ?? ScenarioTraffic.DefaultPacks);
			var name = settings.environment.pack;
			if (!TryParseName(name, out var region, out _, out _) || region != "harbor")
			{
				throw new ArgumentException("A harbor start requires a harbor environment day.");
			}

			var dayFolder = DayFolder(name) ?? throw new FileNotFoundException(NotFound(name));
			var depth = HarborDepth(EnvironmentPackJson.DepthFolderOf(dayFolder), out var loadedDepth);
			var day = EnvironmentPackJson.Load(dayFolder, depth, out var dayTimings);
			if (day.Name != name)
			{
				throw new EnvironmentPackException(Path.Combine(dayFolder, ManifestFile), $"is {day.Name}, expected {name}.");
			}

			var phase = Stopwatch.GetTimestamp();
			var runtime = HarborRuntimePackJson.Load(runtimeFolder);
			var runtimeMs = ElapsedMilliseconds(phase);
			if (runtime.EnvironmentDepthDigest != day.Depth.Digest)
			{
				throw new HarborRuntimePackException(Path.Combine(runtimeFolder, ManifestFile),
					$"environment_depth_digest is {runtime.EnvironmentDepthDigest}, but the day uses {day.Depth.Digest}.");
			}

			phase = Stopwatch.GetTimestamp();
			var contactsPath = Path.Combine(runtimeFolder, "contacts.json");
			var contacts = parseContacts(runtime.ContactsJson, contactsPath) ?? throw new InvalidOperationException(contactsPath + ": contact parser returned no geometry.");
			var contactMs = ElapsedMilliseconds(phase);
			phase = Stopwatch.GetTimestamp();
			var traffic = settings.CreateTraffic(trafficPacks ?? ScenarioTraffic.DefaultPacks);
			var trafficMs = ElapsedMilliseconds(phase);
			var environment = RunEnvironment.ForPack(day, settings.StartUtc, dayFolder);
			var timing = new HarborStartTimings(loadedDepth ? _harborDepth.Timings : default, dayTimings, runtimeMs, contactMs, trafficMs, ElapsedMilliseconds(started));
			return new PreparedHarborStart(environment, runtime, contacts, traffic, runtimeFolder, timing);
		}

		/// <summary>
		/// Loads and verifies a harbor day against the resident verified depth set, loading that set once, so a depth
		/// preview and the atomic start share one copy (decision 0005, Resident data). Throws as <see cref="Load(string)"/> does.
		/// </summary>
		public EnvironmentPack LoadHarborDay(string name)
		{
			var dayFolder = DayFolder(name) ?? throw new FileNotFoundException(NotFound(name) + ".");
			var depth = HarborDepth(EnvironmentPackJson.DepthFolderOf(dayFolder), out _);
			var pack = EnvironmentPackJson.Load(dayFolder, depth);
			if (pack.Name != name)
			{
				throw new EnvironmentPackException(Path.Combine(dayFolder, ManifestFile), $"is the pack {pack.Name}, but it is in the folder of {name}.");
			}

			return pack;
		}

		/// <summary>The resident verified depth set for <paramref name="depthFolder"/>, replaced only when the folder changes.</summary>
		private VerifiedDepth HarborDepth(string depthFolder, out bool loaded)
		{
			loaded = _harborDepth == null || _harborDepthFolder != depthFolder;
			if (loaded)
			{
				var verified = EnvironmentPackJson.LoadVerifiedDepth(depthFolder);
				_harborDepth = verified;
				_harborDepthFolder = depthFolder;
			}

			return _harborDepth;
		}

		private static double ElapsedMilliseconds(long start) => (Stopwatch.GetTimestamp() - start) * (1000.0 / Stopwatch.Frequency);

		/// <summary>
		/// The days with a pack in <paramref name="region"/>, oldest first, from the first root that has the region's
		/// <c>days</c> folder. A folder whose name is not a date, or whose manifest is unreadable, is left out and its reason
		/// added to <paramref name="problems"/>.
		/// </summary>
		public List<EnvironmentPackDay> Days(string region, List<string> problems)
		{
			var days = new List<EnvironmentPackDay>();
			if (!IsRegion(region))
			{
				problems?.Add($"'{region}' is not a region name (lower-case letters, digits and hyphens)");
				return days;
			}

			foreach (var root in _roots)
			{
				var folder = Path.Combine(root, region, "env", "days");
				if (!Directory.Exists(folder))
				{
					continue;
				}

				var names = new List<string>();
				foreach (var path in Directory.GetDirectories(folder))
				{
					names.Add(Path.GetFileName(path));
				}

				names.Sort(StringComparer.Ordinal);
				foreach (var date in names)
				{
					var name = Name(region, date);
					if (!TryParseName(name, out _, out _, out var problem))
					{
						problems?.Add($"{Path.Combine(folder, date)}: {problem}");
						continue;
					}

					try
					{
						days.Add(EnvironmentPackDay.Read(Path.Combine(folder, date), region, date));
					}
					catch (Exception error) when (error is FormatException || error is IOException || error is UnauthorizedAccessException)
					{
						problems?.Add(error.Message);
					}
				}

				return days;
			}

			problems?.Add($"no environment packs for {region} (looked for {string.Join(" and ", Folders(region))})");
			return days;
		}

		private IEnumerable<string> Folders(string region)
		{
			foreach (var root in _roots)
			{
				yield return Path.Combine(root, region, "env", "days");
			}
		}

		private static bool IsRegion(string region)
		{
			if (string.IsNullOrEmpty(region) || region[0] == '-')
			{
				return false;
			}

			foreach (var ch in region)
			{
				if (!((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-'))
				{
					return false;
				}
			}

			return true;
		}

		internal static bool TryParseEpoch(string text, out DateTimeOffset epoch) =>
			DateTimeOffset.TryParseExact(text, EpochFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out epoch);
	}

	/// <summary>A complete candidate bundle. Constructed only after its contacts, day, optional traffic and runtime pack verify.</summary>
	public sealed class PreparedHarborStart
	{
		internal PreparedHarborStart(RunEnvironment environment, HarborRuntimePack runtime, IWorldGeometry contacts, TrafficRun traffic, string runtimeFolder, HarborStartTimings timings)
		{
			Environment = environment;
			Runtime = runtime;
			Contacts = contacts;
			Traffic = traffic;
			RuntimeFolder = runtimeFolder;
			Timings = timings;
		}

		/// <summary>Prepared day with independent physics and observer environments.</summary>
		public RunEnvironment Environment { get; }

		/// <summary>Verified resident runtime export.</summary>
		public HarborRuntimePack Runtime { get; }

		/// <summary>Authoritative contacts parsed from the verified JSON snapshot.</summary>
		public IWorldGeometry Contacts { get; }

		/// <summary>Prepared optional traffic; null when disabled.</summary>
		public TrafficRun Traffic { get; }

		/// <summary>Exact folder of the verified runtime export.</summary>
		public string RuntimeFolder { get; }

		/// <summary>Measured costs of preparing this bundle.</summary>
		public HarborStartTimings Timings { get; }
	}

	/// <summary>Preparation costs, including separate environment phases and the whole candidate bundle.</summary>
	public readonly struct HarborStartTimings
	{
		internal HarborStartTimings(EnvironmentLoadTimings depth, EnvironmentLoadTimings day, double runtime, double contacts, double traffic, double total)
		{
			Depth = depth;
			Day = day;
			RuntimeMilliseconds = runtime;
			ContactsMilliseconds = contacts;
			TrafficMilliseconds = traffic;
			TotalMilliseconds = total;
		}

		/// <summary>Initial depth load phases; zero when the verified snapshot was reused.</summary>
		public EnvironmentLoadTimings Depth { get; }

		/// <summary>Read, hash, decode, validation and total costs for the candidate day.</summary>
		public EnvironmentLoadTimings Day { get; }

		/// <summary>Runtime export verification and parsing cost in milliseconds.</summary>
		public double RuntimeMilliseconds { get; }

		/// <summary>Authoritative contact parser cost in milliseconds.</summary>
		public double ContactsMilliseconds { get; }

		/// <summary>Optional traffic preparation cost in milliseconds.</summary>
		public double TrafficMilliseconds { get; }

		/// <summary>Whole-bundle preparation cost in milliseconds.</summary>
		public double TotalMilliseconds { get; }
	}

	/// <summary>
	/// One scenario day as its manifest describes it, read without verifying the pack's bytes: enough to list the day and to
	/// check a start. Loading the pack itself (<see cref="EnvironmentPacks.Load(string)"/>) verifies everything.
	/// </summary>
	public sealed class EnvironmentPackDay
	{
		private EnvironmentPackDay()
		{
		}

		/// <summary>The pack as region/date.</summary>
		public string Name { get; private set; }

		public string Region { get; private set; }

		/// <summary>The local (America/New_York) date, yyyy-MM-dd.</summary>
		public string Date { get; private set; }

		/// <summary>The day folder the manifest was read from.</summary>
		public string Folder { get; private set; }

		/// <summary>Local midnight on <see cref="Date"/>, as UTC: series times count from it.</summary>
		public DateTimeOffset EpochUtc { get; private set; }

		/// <summary>Start of the series' coverage, s since <see cref="EpochUtc"/>.</summary>
		public double CoverageStart { get; private set; }

		/// <summary>End of the series' coverage, s since <see cref="EpochUtc"/>.</summary>
		public double CoverageEnd { get; private set; }

		/// <summary>True when the day lists no wind station, so the wind reads as still air.</summary>
		public bool WindMissing { get; private set; }

		/// <summary>America/New_York's offset from UTC on the day, from the epoch.</summary>
		public TimeSpan LocalUtcOffset => EnvironmentPacks.LocalUtcOffset(Date, EpochUtc);

		public DateTimeOffset CoverageStartUtc => EnvironmentPacks.AtPackTime(EpochUtc, CoverageStart);

		public DateTimeOffset CoverageEndUtc => EnvironmentPacks.AtPackTime(EpochUtc, CoverageEnd);

		/// <summary>True when a run starting at <paramref name="startUtc"/> starts inside the coverage, its ends included.</summary>
		public bool Covers(DateTimeOffset startUtc) => startUtc >= CoverageStartUtc && startUtc <= CoverageEndUtc;

		internal static EnvironmentPackDay Read(string folder, string region, string date)
		{
			var path = Path.Combine(folder, "manifest.json");
			string text;
			try
			{
				text = File.ReadAllText(path);
			}
			catch (FileNotFoundException)
			{
				throw new FormatException($"{path}: missing; every pack day folder has a manifest.json.");
			}

			Summary summary;
			try
			{
				summary = JsonUtility.FromJson<Summary>(text);
			}
			catch (ArgumentException error)
			{
				throw new FormatException($"{path}: not JSON ({error.Message}).");
			}

			var problems = new List<string>();
			if (summary == null || summary.format != EnvironmentPack.Format)
			{
				problems.Add($"format is '{summary?.format}', not {EnvironmentPack.Format}");
			}
			else
			{
				if (summary.region != region)
				{
					problems.Add($"region is '{summary.region}', but the folder is {region}'s");
				}

				if (summary.date != date)
				{
					problems.Add($"date is '{summary.date}', but the folder is {date}");
				}

				if (!EnvironmentPacks.TryParseEpoch(summary.epoch_utc, out _))
				{
					problems.Add($"epoch_utc '{summary.epoch_utc}' is not UTC as YYYY-MM-DDTHH:MM:SSZ");
				}

				var coverage = summary.time_coverage_s;
				if (coverage == null || coverage.Length != 2 || !(coverage[1] > coverage[0]) || double.IsInfinity(coverage[0]) || double.IsInfinity(coverage[1]))
				{
					problems.Add("time_coverage_s is not [start, end] with the end after the start");
				}
			}

			if (problems.Count > 0)
			{
				throw new FormatException($"{path}: " + string.Join("; ", problems) + ".");
			}

			EnvironmentPacks.TryParseEpoch(summary.epoch_utc, out var epoch);
			return new EnvironmentPackDay
			{
				Name = EnvironmentPacks.Name(region, date),
				Region = region,
				Date = date,
				Folder = folder,
				EpochUtc = epoch,
				CoverageStart = summary.time_coverage_s[0],
				CoverageEnd = summary.time_coverage_s[1],
				WindMissing = summary.wind_stations == null || summary.wind_stations.Length == 0,
			};
		}

		// The day manifest's fields the summary reads (ferrysim-env/1); JsonUtility ignores the others.
		[Serializable]
		private sealed class Summary
		{
			public string format;
			public string region;
			public string date;
			public string epoch_utc;
			public double[] time_coverage_s;
			public Station[] wind_stations;
		}

		[Serializable]
		private sealed class Station
		{
			public string id;
		}
	}
}
