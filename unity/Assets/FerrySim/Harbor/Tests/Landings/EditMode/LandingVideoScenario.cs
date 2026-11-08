using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Harbor.Player.Journeys;
using FerrySim.Sim;
using FerrySim.Slice;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FerrySim.Harbor.Tests.Landings
{
	/// <summary>
	/// Test-only caption scenarios. Unresolved real berths remain blocked; available main faces are explicitly
	/// standardized proxies. Uses the shipped parser, contact eligibility, environment, driver and landing observer.
	/// No scene, rendering, traffic, lines, input recording from the video or navigation clearance is implied.
	/// </summary>
	public sealed class LandingVideoScenario
	{
		private LandingVideoScenario(JObject data) { Data = data; }
		public JObject Data { get; }
		public string Id => (string)Data["id"];
		public string BlockedReason => (string)Data["blocked_reason"];

		public static LandingVideoScenario[] LoadDirectory(string folder) => Directory.GetFiles(folder, "*.json")
			.OrderBy(p => p, StringComparer.Ordinal).Select(p => Parse(File.ReadAllText(p))).ToArray();

		public static LandingVideoScenario Parse(string json)
		{
			var data = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
			if ((string)data["schema"] != "ferrysim-crew-landing/1") throw new FormatException("Unknown landing scenario schema.");
			var id = (string)data["id"];
			if (string.IsNullOrWhiteSpace(id) || id.Any(c => !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-'))
				throw new FormatException("A scenario needs a safe stable id.");
			if ((string)data["vessel_file"] != "t-class.json" && (string)data["vessel_file"] != "k-class.json") throw new FormatException("Unknown vessel file.");
			if ((bool?)data["gusts_enabled"] != false || (bool?)data["fender_overrides_enabled"] != false)
				throw new FormatException("Baseline scenarios must keep experiments off; opt in at Run explicitly.");
			if ((double?)data["start_bow_clearance_m"] != 400 || (int?)data["maximum_steps"] != 180000 || (double?)data["waypoint_bow_clearance_m"] != 60)
				throw new FormatException("The standardized driver settings changed; version the protocol first.");
			var checks = data["checks"];
			if ((double?)checks?["first_contact_knots_max"] != 1.5 || (double?)checks?["peak_force_n_max"] != 414000 ||
				(bool?)checks?["bottoming_allowed"] != false || (int?)checks?["hold_steps"] != 1000 ||
				(double?)checks?["hold_sog_knots_exclusive_max"] != .1 || (double?)checks?["heading_error_degrees_max"] != 5)
				throw new FormatException("Landing acceptance thresholds must match HarborJourneyObserver.");
			if (string.IsNullOrWhiteSpace((string)data["blocked_reason"]) &&
				(string.IsNullOrWhiteSpace((string)data["mapping"]?["stop_id"]) || string.IsNullOrWhiteSpace((string)data["mapping"]?["berth_id"])))
				throw new FormatException("An unresolved berth must be blocked.");
			if (!DateTime.TryParseExact((string)data["date"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
				(int?)data["minute"] == null || (int)data["minute"] < 0 || (int)data["minute"] >= 1440) throw new FormatException("Invalid simulation time.");
			var wind = data["wind"] as JObject ?? throw new FormatException("Missing wind object.");
			var mean = WindNumber(wind["mean_knots"], 200);
			var from = WindNumber(wind["from_degrees"], 359.999999);
			var gust = WindNumber(wind["gust_knots"], 200);
			if ((mean > 0 && !from.HasValue) || (from.HasValue && !mean.HasValue) ||
				(gust.HasValue && (!mean.HasValue || gust < mean))) throw new FormatException("Incomplete wind or gust below the mean.");
			if (!(data["fender_overrides"] is JArray)) throw new FormatException("Missing fender override list.");
			return new LandingVideoScenario(data);
		}

		public JObject Run(string root, bool applyGusts = false, bool applyFenderOverrides = false, bool verifyReplay = false)
		{
			var result = new JObject { ["id"] = Id, ["verdict"] = "blocked", ["reason"] = BlockedReason,
				["mapping"] = Data["mapping"].DeepClone(), ["source"] = Data["source"], ["gusts_enabled"] = applyGusts, ["fender_overrides_enabled"] = applyFenderOverrides,
				["gust_seed"] = Data["gust_seed"], ["wind"] = Data["wind"].DeepClone(), ["fender_overrides"] = Data["fender_overrides"].DeepClone(), ["vessel_file"] = Data["vessel_file"] };
			if (!string.IsNullOrEmpty(BlockedReason)) return result;
			var contactsPath = Path.Combine(root, "data/processed/harbor/unity/contacts.json");
			var surfacesPath = Path.Combine(root, "unity/Assets/FerrySim/Harbor/Data/landing-contact-surfaces.json");
			var landings = LandingGeometry.Load(contactsPath, surfacesPath);
			var stop = (string)Data["mapping"]["stop_id"];
			var berth = (string)Data["mapping"]["berth_id"];
			LandingFace face;
			try { face = landings.BerthFace(stop, berth); }
			catch (LandingDataException error) { result["reason"] = error.Message; return result; }
			if (face.Segments.Any(s => !s.StartFaceEligible)) { result["reason"] = "The selected face is not eligible."; return result; }
			var vessel = VesselParametersJson.Load(Path.Combine(root, "vessels", (string)Data["vessel_file"]));
			var pack = EnvironmentPackJson.Load(Path.Combine(root, "data/processed/harbor/env/days", (string)Data["date"]));
			var startUtc = pack.EpochUtc.AddMinutes((int)Data["minute"]);
			var water = new DataEnvironment(pack, startUtc);
			var wind = Data["wind"];
			var speed = Units.KnotsToMetresPerSecond((double?)wind["mean_knots"] ?? 0);
			var from = Angles.ToRadians((double?)wind["from_degrees"] ?? 0);
			var environment = new CaptionWind(water, new HorizontalVector(-speed * Math.Cos(from), -speed * Math.Sin(from)),
				Units.KnotsToMetresPerSecond((double?)wind["gust_knots"] ?? 0), new GustOptions(applyGusts, (uint)Data["gust_seed"]));
			var middle = (face.Start + face.End) * .5;
			var direction = face.End - face.Start;
			var normal = new HorizontalVector(direction.East, -direction.North) * (1 / direction.Magnitude);
			var bow = FaceClearance.BodyOutline(vessel).Max(p => p.North);
			var start = middle + normal * (bow + (double)Data["start_bow_clearance_m"]);
			var heading = Math.Atan2(-normal.East, -normal.North);
			var clearance = FaceClearance.Check(vessel, start.North, start.East, heading, landings.Geometry.Segments);
			if (!clearance.IsClear()) { result["reason"] = "400 m start overlaps or is too close to segment " + clearance.NearestSegmentId; return result; }
			var route = new HarborJourney { Name = Id, Arrival = stop, Departure = stop, Date = (string)Data["date"], Minute = (int)Data["minute"],
				Points = new[] { middle + normal * (bow + (double)Data["waypoint_bow_clearance_m"]) },
				SpanIds = Array.Empty<string>(), SpanRings = Array.Empty<HorizontalVector[]>(), FaceMiddle = middle, FaceNormal = normal,
				FaceIds = face.Segments.Select(s => s.Id).ToArray(), ForceLimit = 414000 };
			var overrides = new Dictionary<int, ContactSurface>();
			foreach (var entry in (JArray)Data["fender_overrides"])
			{
				var id = (int)entry["segment_id"];
				if (!route.FaceIds.Contains(id) || string.IsNullOrWhiteSpace((string)entry["source"])) throw new FormatException("Fender override needs this berth's segment and measurement source.");
				overrides.Add(id, new ContactSurface((double)entry["stiffness"], (double)entry["damping"], (double)entry["friction"], (double)entry["max_compression"]));
			}
			IWorldGeometry geometry = applyFenderOverrides ? new ContactSurfaceOverrides(landings.Geometry, overrides, true) : landings.Geometry;
			var initial = VesselState.Planar(0, start.North, start.East, heading);
			var sim = new VesselDynamicsSimulation(vessel, environment, geometry, initial);
			result["initial_under_keel_m"] = sim.Telemetry.DepthUnderKeel;
			result["simulation_start_utc"] = startUtc.ToString("O"); result["start_ne_m"] = new JArray(start.North, start.East);
			result["environment_digest"] = pack.Digest;
			result["contacts_sha256"] = Hash(contactsPath); result["surfaces_sha256"] = Hash(surfacesPath);
			if (sim.IsAground) { result["reason"] = "The 400 m start is aground."; return result; }
			var controls = new HarborJourneyControls(route, () => sim.State, () => sim.ContactCount > 0, vessel.Particulars.LengthOverall.Value);
			var observer = new HarborJourneyObserver(route, sim);
			var digest = new StateDigest();
			var output = Path.Combine(root, "artifacts/landing-behaviour", Id + (applyGusts || applyFenderOverrides ? "-experimental" : "")); Directory.CreateDirectory(output);
			var trace = new ControlTrace();
			var last = ControlInputs.Neutral;
			var steps = 0;
			var fenderHoldSteps = 0;
			var peakFenderForce = 0.0;
			using (var trajectory = new StreamWriter(Path.Combine(output, "trajectory.csv")))
			{
				trajectory.WriteLine("step,time_s,north_m,east_m,heading_deg,sog_knots,contact_force_n,under_keel_m");
				for (; steps < (int)Data["maximum_steps"] && !controls.Complete; steps++)
				{
					var input = FixedStepDriver.Canonical(controls.ControlsForStep(steps));
					if (steps == 0 || !input.Equals(last)) { trace.Append(steps, input); last = input; }
					observer.BeforeStep(steps, sim.State, sim.Telemetry, input);
					sim.Step(input); observer.AfterStep(steps + 1, sim.State, sim.Telemetry); digest.Add(sim.State);
					peakFenderForce = Math.Max(peakFenderForce, sim.ContactLoad.Magnitude);
					fenderHoldSteps = sim.ContactCount > 0 && sim.ContactLoad.Magnitude > 0 && sim.Telemetry.SpeedOverGround < Units.KnotsToMetresPerSecond(.1) ? fenderHoldSteps + 1 : 0;
					if (!sim.State.IsFinite()) throw new InvalidOperationException("Non-finite state in " + Id);
					if (steps % 100 == 0) trajectory.WriteLine(string.Join(",", steps + 1, F(sim.State.Time), F(sim.State.North), F(sim.State.East),
						F(Angles.ToDegrees(sim.State.Heading)), F(Units.MetresPerSecondToKnots(sim.Telemetry.SpeedOverGround)), F(sim.Telemetry.ContactForce), F(sim.Telemetry.DepthUnderKeel)));
				}
			}
			trace.Append(steps, last);
			using (var writer = new StreamWriter(Path.Combine(output, "controls.csv"))) trace.Write(writer);
			result["verdict"] = observer.LandingVerdict(sim.State); result["reason"] = null;
			result["steps"] = steps; result["driver_complete"] = controls.Complete; result["driver_phase"] = controls.Phase;
			result["state_digest"] = digest.Text;
			result["peak_fender_force_n"] = peakFenderForce; result["fender_hold_1000_steps_okay"] = fenderHoldSteps >= 1000;
			result["first_contact_knots"] = observer.FirstContactSpeed < 0 ? JValue.CreateNull() : new JValue(Units.MetresPerSecondToKnots(observer.FirstContactSpeed));
			result["first_contact_id"] = observer.FirstContactId; result["peak_force_n"] = observer.PeakForce; result["peak_compression_m"] = observer.PeakCompression;
			result["bottomed_out"] = observer.BottomedOut; result["hold_1000_steps_okay"] = observer.FinalHoldOkay;
			result["heading_error_deg"] = Math.Abs(Angles.ToDegrees(Angles.WrapPi(sim.State.Heading - heading)));
			result["grounded_steps"] = observer.GroundedSteps; result["minimum_under_keel_m"] = observer.MinimumUnderKeel;
			result["failures"] = new JArray(observer.Failures); result["depth_misses"] = water.Coverage.DepthMisses;
			result["time_holds"] = water.Coverage.TimeHolds; result["trial_kind"] = "standardized main-face proxy; not video replay";
			var stationId = (string)Data["tide"]["reference_station"];
			if (stationId != null)
			{
				var station = pack.CurrentStations.Single(s => s.Id == stationId);
				var current = station.Velocity.Sample(water.EpochOffset);
				result["phase_reference_station"] = stationId; result["phase_reference_current_ne_mps"] = new JArray(current.North, current.East);
				var phaseDirection = Angles.ToRadians((double)Data["tide"]["phase_toward_deg"]);
				result["caption_phase_matches_start"] = current.North * Math.Cos(phaseDirection) + current.East * Math.Sin(phaseDirection) > 0;
			}
			if (verifyReplay)
			{
				var replay = ControlTrace.Load(Path.Combine(output, "controls.csv"));
				var replayDigest = new StateDigest();
				sim.Reset(initial);
				for (var step = 0; step < steps; step++) { sim.Step(replay.At(step)); replayDigest.Add(sim.State); }
				result["replay_state_digest"] = replayDigest.Text;
				result["replay_matches"] = replayDigest.Text == digest.Text && replay.EndStep == steps;
			}
			File.WriteAllText(Path.Combine(output, "result.json"), result.ToString() + "\n");
			return result;
		}

		private static double? WindNumber(JToken token, double maximum)
		{
			if (token == null || token.Type == JTokenType.Null) return null;
			if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) throw new FormatException("Wind values must be numbers or null.");
			var value = (double)token;
			if (double.IsNaN(value) || value < 0 || value > maximum) throw new FormatException("Wind is outside the finite supported range.");
			return value;
		}

		private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
		private static string Hash(string path)
		{
			using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
		}

		private sealed class CaptionWind : IEnvironment
		{
			private readonly DataEnvironment _water;
			private readonly HorizontalVector _mean;
			private readonly double _ceiling;
			private readonly GustOptions _gusts;
			public CaptionWind(DataEnvironment water, HorizontalVector mean, double ceiling, GustOptions gusts) { _water = water; _mean = mean; _ceiling = ceiling; _gusts = gusts; }
			public HorizontalVector SampleCurrent(double n, double e, double t) => _water.SampleCurrent(n, e, t);
			public double SampleDepth(double n, double e, double t) => _water.SampleDepth(n, e, t);
			public HorizontalVector SampleWind(double n, double e, double t) => _gusts.Apply(_mean, _ceiling, _water.PackTime(t));
		}
	}
}
