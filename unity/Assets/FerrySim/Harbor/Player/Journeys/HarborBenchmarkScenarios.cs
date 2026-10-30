using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Env;
using FerrySim.Sim;
using Newtonsoft.Json.Linq;
using Unity.Mathematics;
using UnityEngine;

namespace FerrySim.Harbor.Player.Journeys
{
	public sealed partial class HarborBenchmark
	{
		private bool SpecialStart => _name == "starts" || _name == "switches" || _name.StartsWith("start-", StringComparison.Ordinal);
		private readonly JArray _starts = new JArray();
		private IEnumerator CatalogStarts()
		{
			_runner.RecordRuns = false;
			_services.Displays.RadarShown = _displays; _services.Displays.ChartplotterShown = _displays;
			var catalog = _services.HarborCatalog;
			var choices = new List<ScenarioChoice>();
			if (_name == "starts")
			{
				var pack = _runner.EnvironmentPacks.LoadHarborDay("harbor/2025-07-16");
				var environment = new DataEnvironment(pack,pack.EpochUtc);
				foreach (var landing in catalog.Landings)
				{
					var position = default(FerrySim.Core.Contracts.HorizontalVector);
					foreach (var stop in _services.Pack.Stops) if (stop.Id == landing.StopId) position = stop.Position;
					var low = double.PositiveInfinity; var high = double.NegativeInfinity; var lowMinute=0; var highMinute=0;
					for (var minute=0;minute<1440;minute++)
					{
						var level=environment.SampleWaterLevel(position.North,position.East,minute*60);
						if (level<low) {low=level;lowMinute=minute;} if (level>high) {high=level;highMinute=minute;}
					}
					foreach (var berth in landing.Berths)
					foreach (var vessel in catalog.Vessels)
					foreach (var minute in new[] {lowMinute,highMinute}) choices.Add(new ScenarioChoice(landing.StopId,"2025-07-16",minute,vessel.Id,berth.Id));
				}
			}
			else if (_name == "switches")
			{
				for (var i=0;i<3*_switchCycles;i++) foreach (var stop in new[] {"87","137","89","112"}) choices.Add(new ScenarioChoice(stop,"2025-07-16",738,"t-class"));
			}
			else choices.Add(new ScenarioChoice(_name.Substring(6),"2025-07-16",738,"t-class"));
			foreach (var choice in choices)
			{
				SetPhase(6);
				JObject beforeCollection=null, afterCollection=null;
				if (_switchCollect && _starts.Count>0)
				{
					beforeCollection=MemorySnapshot();
					yield return Resources.UnloadUnusedAssets();
					GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
					yield return null;
					afterCollection=MemorySnapshot();
				}
				var acceptedAt=Time.realtimeSinceStartupAsDouble;
				Event(7,-1,0,0);
				var row=new JObject { ["stop_id"]=choice.StopId,["hull"]=choice.VesselId,["berth"]=choice.BerthId,["minute"]=choice.LocalMinutes,["date"]=choice.Date,["accepted_wall_s"]=acceptedAt };
				row["sequence"]=_starts.Count+1;
				if (beforeCollection!=null) {row["before_collection"]=beforeCollection;row["after_collection"]=afterCollection;}
				bool accepted=false; string refusal=null;
				try
				{
					accepted=catalog.TryCreate(choice,out var settings,out refusal);
					if (accepted) accepted=_bootstrap.TryStart(settings,out refusal);
				}
				catch (Exception error) { refusal=error.ToString(); _failures.Add(refusal); }
				row["accepted"]=accepted;row["refusal"]=refusal;
				if (!accepted)
				{
					row["readiness_verdict"]="unverified";
					row["reason"]=choice.StopId=="137" || choice.StopId=="113" || choice.StopId=="118" ? "blocked by the crew question" : "refused by real catalog";
					_starts.Add(row);continue;
				}
				if (_bootstrap.WheelhouseUI.View != null) { _bootstrap.WheelhouseUI.View.PickerVisible=false; _bootstrap.WheelhouseUI.View.DebriefVisible=false; }
				_services.Navigation.SelectedStopId=choice.StopId;
				var firstFrame=Time.realtimeSinceStartupAsDouble;
				var until=firstFrame+90;
				while (_bootstrap.ViewState != HarborViewState.Ready && _bootstrap.Problem==null && Time.realtimeSinceStartupAsDouble<until) yield return null;
				var readyAt=Time.realtimeSinceStartupAsDouble;
				_runner.Pause();
				var placement=ReadinessPlacement();
				var ready=_bootstrap.ViewState==HarborViewState.Ready && placement;
				row["ready"]=ready;row["start_to_ready_s"]=readyAt-acceptedAt;row["request_to_first_frame_s"]=firstFrame-acceptedAt;row["first_frame_to_ready_s"]=readyAt-firstFrame;
				row["readiness_verdict"]=ready ? (readyAt-acceptedAt<=3 ? "pass" : "fail") : "fail";
				row["near_field_placement_verified"]=placement;
				if (_switchDiagnostics) row["memory_first_view"]=MemorySnapshot();
				row["atomic_load_timings"]=JObject.FromObject(_runner.HarborLoadTimings);
				if (ready) Event(8,-1,0,readyAt-acceptedAt);
				if (!ready) _failures.Add(_bootstrap.Problem ?? "Start readiness timeout " + choice.StopId);
				try { HarborProcessMemory.Read(out var working,out var peak,out var pagefile,out var source);row["process_peak_bytes"]=peak;row["process_memory_source"]=source; }
				catch (Exception error) {row["process_memory_error"]=error.Message;}
				_starts.Add(row);
				if (ready)
				{
					SetPhase(2);_runner.Resume();
					var dwell=Time.realtimeSinceStartupAsDouble+(_switchDiagnostics ? 10 : 2);
					while (Time.realtimeSinceStartupAsDouble<dwell) yield return null;
					_runner.Pause();
					if (_switchDiagnostics) row["memory_after_10s"]=MemorySnapshot();
					row["depth_misses"]=_runner.Environment.DepthMisses;row["time_holds"]=_runner.Environment.TimeHolds;
				}
				if (_capture && (choice.StopId=="87" || choice.StopId=="89" || choice.StopId=="112") && choice.VesselId=="t-class") yield return Capture("start-"+choice.StopId+"-"+_starts.Count);
				yield return null;
			}
			_scenario["starts"]=_starts;
			_scenario["switch_memory_diagnostics"]=_switchDiagnostics;_scenario["diagnostic_collection"]=_switchCollect;_scenario["switch_sequence_cycles"]=_switchCycles;
			_scenario["all_24_landings_ready"] = false; // the three crew-refused faces are still offered, never forced.
			File.WriteAllText(Path.Combine(_output,"starts.json"),_starts.ToString()+"\n");
		}
		private bool ReadinessPlacement()
		{
			if (!(_runner.Placement is FerrySim.Sim.CesiumVesselPlacement placement)) return false;
			var pose=_runner.RenderPose;
			var expected=LocalTangentPlane.World.ToGeodetic(pose.North,pose.East);
			var llh=placement.Anchor.longitudeLatitudeHeight;
			var height=_services.WaterSurface.EllipsoidalHeight(pose.North,pose.East,_runner.RenderTime)-pose.Down;
			return Math.Abs(llh.x-expected.LongitudeDegrees)<1e-7 && Math.Abs(llh.y-expected.LatitudeDegrees)<1e-7 && Math.Abs(llh.z-height)<.01 && _services.Navigation.Barges.Count==28;
		}
		private IEnumerator CameraSweep()
		{
			_runner.Pause();
			PrepareCameraSweep(_bootstrap.CameraRig);
			var camera=_bootstrap.CameraRig.GetComponentInChildren<Camera>();
			var anchor=camera.GetComponent<CesiumGlobeAnchor>();
			if (anchor==null) anchor=camera.gameObject.AddComponent<CesiumGlobeAnchor>();
			var geo=FindFirstObjectByType<CesiumGeoreference>();
			var points=new double3[_route.Points.Length];
			for (var i=0;i<points.Length;i++)
			{
				var p=LocalTangentPlane.World.ToGeodetic(_route.Points[i].North,_route.Points[i].East);
				points[i]=geo.ellipsoid.LongitudeLatitudeHeightToCenteredFixed(new double3(p.LongitudeDegrees,p.LatitudeDegrees,120));
			}
			var expectedDistance=0.0;
			for(var i=1;i<points.Length;i++)expectedDistance+=math.distance(points[i-1],points[i]);
			var samples=new CameraSweepSample[200000];
			var count=0;var observedDistance=0.0;var maximumError=0.0;var previous=default(double3);
			var rendered=new WaitForEndOfFrame();
			SetPhase(2);
			for (var i=1;i<points.Length;i++)
			{
				var distance=math.length(points[i]-points[i-1]);
				var start=Time.realtimeSinceStartupAsDouble;
				while (true)
				{
					var fraction=Math.Min(1,(Time.realtimeSinceStartupAsDouble-start)*64.3055555555556/distance);
					var expected=math.lerp(points[i-1],points[i],fraction);
					ApplyCameraSweepPose(anchor,camera,geo,expected,points[i]-points[i-1]);
					yield return rendered;
					var actual=anchor.positionGlobeFixed;
					maximumError=Math.Max(maximumError,math.distance(expected,actual));
					if(count>0)observedDistance+=math.distance(previous,actual);
					previous=actual;
					if(count<samples.Length)samples[count++]=new CameraSweepSample {Frame=Time.frameCount,Wall=Time.realtimeSinceStartupAsDouble,Expected=expected,Actual=actual};
					else _overflow=true;
					// Complete the frame sample before writing the final evidence or starting the next leg.
					yield return null;
					if (fraction>=1) break;
				}
			}
			var endError=math.distance(anchor.positionGlobeFixed,points[points.Length-1]);
			var verified=count>=3 && maximumError<=.1 && endError<=.1 && Math.Abs(observedDistance-expectedDistance)<=expectedDistance*.001 && !_overflow;
			_scenario["camera_expected_distance_m"]=expectedDistance;_scenario["camera_observed_distance_m"]=observedDistance;
			_scenario["camera_maximum_error_m"]=maximumError;_scenario["camera_end_error_m"]=endError;
			_scenario["camera_sample_count"]=count;_scenario["camera_route_verified"]=verified;
			if(!verified)_failures.Add("The rendered camera did not follow the complete requested sweep");
			using(var csv=new StreamWriter(Path.Combine(_output,"camera.csv")))
			{
				csv.WriteLine("frame,wall_s,expected_x,expected_y,expected_z,actual_x,actual_y,actual_z");
				for(var i=0;i<count;i++)
				{
					var sample=samples[i];
					csv.WriteLine(string.Format(CultureInfo.InvariantCulture,"{0},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R},{7:R}",sample.Frame,sample.Wall,sample.Expected.x,sample.Expected.y,sample.Expected.z,sample.Actual.x,sample.Actual.y,sample.Actual.z));
				}
			}
			_scenario["stress"]=true;_scenario["camera_speed_mps"]=64.3055555555556;_scenario["boat_driven"]=false;
		}
		private struct CameraSweepSample
		{
			public int Frame;
			public double Wall;
			public double3 Expected,Actual;
		}

		public static void PrepareCameraSweep(FerrySim.World.CameraRig rig)
		{
			rig.enabled=false;
			if(rig.FreeFly!=null)rig.FreeFly.enabled=false;
			if(rig.Chase!=null)rig.Chase.enabled=false;
			if(rig.Wheelhouse!=null)rig.Wheelhouse.enabled=false;
		}
		public static void ApplyCameraSweepPose(CesiumGlobeAnchor anchor,Camera camera,CesiumGeoreference geo,double3 expected,double3 directionEcef)
		{
			anchor.positionGlobeFixed=expected;
			var direction=math.normalize(geo.TransformEarthCenteredEarthFixedDirectionToUnity(directionEcef));
			camera.transform.rotation=Quaternion.LookRotation(new Vector3((float)direction.x,(float)direction.y,(float)direction.z)+Vector3.down*.15f,Vector3.up);
			FerrySim.World.CameraAnchor.Commit(anchor);
		}
	}
}
