using System;
using System.Collections.Generic;
using System.Diagnostics;
using CesiumForUnity;
using Unity.Mathematics;
using FerrySim.Core.Contracts;
using FerrySim.Core.Traffic;
using FerrySim.Vessels;
using FerrySim.Sim;
using FerrySim.World;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace FerrySim.Traffic
{
	/// <summary>
	/// Draws the run's traffic (<see cref="VesselSimulationRunner.Traffic"/>): a greybox hull for every vessel of the day that
	/// comes near the slice, placed every frame where the core's replay puts it at the time the learner's vessel is drawn (the
	/// runner's interpolated time, so the two never drift apart), floating on the drawn water. A vessel is drawn only while it is
	/// in the run's traffic (inside one of its segments, not the one left out), inside the slice's area with a margin, and within
	/// the view range of the camera. Static vessels never move. Hulls are built at each run start whose traffic changed and
	/// destroyed with the next; placing them allocates nothing. Traffic is only drawn: it never pushes the learner's vessel, and its
	/// collisions are the runner's events (Sim/README.md, Traffic).
	/// </summary>
	[DefaultExecutionOrder(100)]
	[DisallowMultipleComponent]
	public sealed class TrafficView : MonoBehaviour
	{
		/// <summary>Default view range, m: vessels farther from the camera are not drawn.</summary>
		public const float DefaultViewRange = 4000f;

		/// <summary>Default margin around the slice's area, m, so a vessel does not vanish at the area's edge.</summary>
		public const float DefaultAreaMargin = 250f;

		/// <summary>How far a hull moves before its water height is looked up again, m (the drawn surface changes by millimetres over it).</summary>
		private const double HeightCacheDistance = 20.0;

		[SerializeField]
		private VesselSimulationRunner runner;

		[Tooltip("The frame hulls are placed in: the world frame (x east, y up, z north at the shared origin).")]
		[SerializeField]
		private Transform frame;

		[Tooltip("The drawn water the hulls float on, and whose datum bounds the slice's area.")]
		[SerializeField]
		private WaterSurface water;

		[Tooltip("The camera whose distance limits what is drawn. Empty: the main camera.")]
		[SerializeField]
		private Camera viewCamera;

		[SerializeField]
		[Min(100f)]
		private float viewRange = DefaultViewRange;

		[SerializeField]
		[Min(0f)]
		private float areaMargin = DefaultAreaMargin;

		private readonly List<TrafficHull> _hulls = new List<TrafficHull>();
		private readonly List<Mesh> _meshes = new List<Mesh>();
		private readonly Dictionary<TrafficClass, Material> _materials = new Dictionary<TrafficClass, Material>();
		private readonly Dictionary<(TrafficClass, double, double, double), (Mesh mesh, double height)> _sharedMeshes =
			new Dictionary<(TrafficClass, double, double, double), (Mesh mesh, double height)>();
		private readonly HashSet<Mesh> _renderMeshes = new HashSet<Mesh>();
		private readonly HashSet<Material> _renderMaterials = new HashSet<Material>();
		private readonly Stopwatch _watch = new Stopwatch();
		private TrafficRun _built;
		private Transform[] _transforms = Array.Empty<Transform>();
		private bool[] _active = Array.Empty<bool>();
		private double[] _heightNorth = Array.Empty<double>();
		private double[] _heightEast = Array.Empty<double>();
		private double[] _heightLevel = Array.Empty<double>();
		private double[] _height = Array.Empty<double>();
		private CesiumGeoreference _georeference;
		private IWaterSurface _harborWater;
		private ITrafficArea _harborArea;
		private TrafficGlobeLattice _lattice;
		private int[] _hullIndices = Array.Empty<int>();
		private bool _hasArea;
		private double _minNorth, _maxNorth, _minEast, _maxEast;
		private bool _subscribed;
		private long _frames;
		private double _totalMilliseconds;

		/// <summary>Every hull built for the run's traffic, in pack order, drawn or not.</summary>
		public IReadOnlyList<TrafficHull> Hulls => _hulls;

		/// <summary>Hulls drawn this frame.</summary>
		public int ShownCount { get; private set; }

		public int RosterCount => _built?.Pack.Vessels.Count ?? 0;
		/// <summary>All present targets, excluding leave-out, including those outside render coverage/range.</summary>
		public int ActiveCount { get; private set; }
		/// <summary>Enabled signal points meeting optics and sector at the camera (before camera frustum/occlusion).</summary>
		public int LightsDrawn { get; private set; }
		/// <summary>Renderer activation/deactivation transitions in the last Place; no pool is needed when all hulls fit.</summary>
		public int ActivationChanges { get; private set; }
		/// <summary>No pool when the all-eligible strategy fits the budget.</summary>
		public int PoolChurn => 0;
		public int MissingTargets { get; private set; }
		public int MeshCount => _renderMeshes.Count;
		public int MaterialCount => _renderMaterials.Count;
		public long LatticeBytes => _lattice?.StorageBytes ?? 0;

		/// <summary>The run time the hulls were last placed at, s since the scenario start.</summary>
		public double PlacedTime { get; private set; } = double.NaN;

		/// <summary>The traffic the hulls were built for, or null.</summary>
		public TrafficRun Traffic => _built;

		/// <summary>CPU time of the last placement, of the slowest since the traffic was built, and their mean, ms.</summary>
		public double LastMilliseconds { get; private set; }

		public double MaximumMilliseconds { get; private set; }

		public double MeanMilliseconds => _frames > 0 ? _totalMilliseconds / _frames : 0.0;

		public float ViewRange
		{
			get => viewRange;
			set => viewRange = Mathf.Max(100f, value);
		}

		public float AreaMargin
		{
			get => areaMargin;
			set => areaMargin = Mathf.Max(0f, value);
		}

		public VesselSimulationRunner Runner => runner;

		/// <summary>The same run-clock sky as the own vessel; its navigation-light threshold differs from the panel palette.</summary>
		public SunController Sun { get; set; }

		/// <summary>Allows a paired rendering-cost measurement with hull lights off.</summary>
		public bool NavigationLightsEnabled { get; set; } = true;

		/// <summary>Sets what the view follows, for bootstrap code and tests, before or after it is enabled.</summary>
		public void Connect(VesselSimulationRunner simulationRunner, Transform worldFrame, WaterSurface waterSurface, Camera camera, SunController sun = null)
		{
			Unsubscribe();
			Clear();
			_georeference = null;
			_harborWater = null;
			_harborArea = null;
			_lattice = null;
			runner = simulationRunner;
			frame = worldFrame;
			water = waterSurface;
			viewCamera = camera;
			Sun = sun;
			if (isActiveAndEnabled)
			{
				Subscribe();
			}

			Rebuild();
		}

		/// <summary>Connects harbor render coverage and spatial water without changing the slice adapter.</summary>
		public void ConnectHarbor(VesselSimulationRunner simulationRunner, CesiumGeoreference georeference,
			IWaterSurface waterSurface, ITrafficArea area, Camera camera, SunController sun = null)
		{
			if (georeference == null) throw new ArgumentNullException(nameof(georeference));
			if (waterSurface == null) throw new ArgumentNullException(nameof(waterSurface));
			if (area == null) throw new ArgumentNullException(nameof(area));
			var lattice = new TrafficGlobeLattice(area.Extent);
			Unsubscribe();
			Clear();
			runner = simulationRunner;
			frame = georeference.transform;
			water = null;
			_georeference = georeference;
			_harborWater = waterSurface;
			_harborArea = area;
			_lattice = lattice;
			viewCamera = camera;
			Sun = sun;
			if (isActiveAndEnabled) Subscribe();
			Rebuild();
		}

		private void OnEnable()
		{
			Subscribe();
			Rebuild();
		}

		private void OnDisable()
		{
			Unsubscribe();
			HideAll();
		}

		private void OnDestroy()
		{
			Unsubscribe();
			Clear();
			foreach (var material in _materials.Values)
			{
				DestroySafely(material);
			}

			_materials.Clear();
		}

		private void Subscribe()
		{
			if (!_subscribed && runner != null)
			{
				runner.RunStarted += Rebuild;
				_subscribed = true;
			}
		}

		private void Unsubscribe()
		{
			if (_subscribed && runner != null)
			{
				runner.RunStarted -= Rebuild;
			}

			_subscribed = false;
		}

		private void LateUpdate() => Place();

		/// <summary>
		/// Builds the hulls for the runner's traffic when it differs from the traffic they were built for: none without traffic, else
		/// one per vessel that may come within the slice's area and its margin as the replay moves it (<see cref="TrafficReplay.MayEnter"/>:
		/// only a vessel proven never to enter goes without a hull).
		/// </summary>
		public void Rebuild()
		{
			var traffic = runner != null ? runner.Traffic : null;
			if (ReferenceEquals(traffic, _built))
			{
				return;
			}

			Clear();
			_built = traffic;
			_frames = 0;
			_totalMilliseconds = 0.0;
			MaximumMilliseconds = 0.0;
			if (traffic == null)
			{
				return;
			}

			ReadArea();
			var parent = frame != null ? frame : transform;
			var pack = traffic.Pack;
			var hulls = new List<TrafficHull>();
			foreach (var vessel in pack.Vessels)
			{
				if ((_harborArea != null && !_harborArea.MayEnter(traffic.Replay, vessel.Index)) ||
					(_hasArea && !traffic.Replay.MayEnter(vessel.Index, _minNorth, _maxNorth, _minEast, _maxEast)))
				{
					continue;
				}

				var hull = new GameObject($"Traffic {vessel.Mmsi} {vessel.Name}".TrimEnd());
				hull.transform.SetParent(parent, false);
				var classId = FerryModels.ClassForMmsi(vessel.Mmsi);
				var model = FerryModels.Create(hull.transform, classId, 1, vessel.Hull.Length, vessel.Hull.Beam);
				double height;
				if (model != null)
				{
					height = model.Height * model.transform.localScale.y;
				}
				else
				{
					var key = (vessel.Class, vessel.Hull.Length, vessel.Hull.Beam, vessel.Hull.Draft);
					Mesh mesh;
					if (_harborArea != null && _sharedMeshes.TryGetValue(key, out var shared))
					{
						mesh = shared.mesh;
						height = shared.height;
					}
					else
					{
						mesh = TrafficHullMesh.Build(vessel.Class, vessel.Hull, out height);
						_meshes.Add(mesh);
						if (_harborArea != null) _sharedMeshes.Add(key, (mesh, height));
					}
					hull.AddComponent<MeshFilter>().sharedMesh = mesh;
					hull.AddComponent<MeshRenderer>().sharedMaterial = MaterialFor(vessel.Class);
				}
				var component = hull.AddComponent<TrafficHull>();
				component.Describe(vessel, height);
				foreach (var filter in hull.GetComponentsInChildren<MeshFilter>(true)) _renderMeshes.Add(filter.sharedMesh);
				foreach (var renderer in hull.GetComponentsInChildren<Renderer>(true))
					foreach (var material in renderer.sharedMaterials) _renderMaterials.Add(material);
				hull.SetActive(false);
				hulls.Add(component);
			}

			_hulls.AddRange(hulls);
			_hullIndices = new int[pack.Vessels.Count];
			for (var i = 0; i < _hullIndices.Length; i++) _hullIndices[i] = -1;
			for (var i = 0; i < _hulls.Count; i++) _hullIndices[_hulls[i].VesselIndex] = i;
			_transforms = new Transform[_hulls.Count];
			_active = new bool[_hulls.Count];
			_heightNorth = new double[_hulls.Count];
			_heightEast = new double[_hulls.Count];
			_heightLevel = new double[_hulls.Count];
			_height = new double[_hulls.Count];
			for (var i = 0; i < _hulls.Count; i++)
			{
				_transforms[i] = _hulls[i].transform;
				_heightNorth[i] = double.NaN;
			}

			Debug.Log($"[FerrySim] Traffic {pack.Date}: {_hulls.Count} of the day's {pack.Vessels.Count} vessels come near the render area; {(traffic.Removed != null ? "leaving out " + traffic.Removed : "none left out")}.", this);
		}

		/// <summary>
		/// The simulation time the learner's vessel is drawn at this frame: between the last two physics states by the driver's
		/// interpolation factor, s since the scenario start.
		/// </summary>
		public static double RenderTime(FixedStepDriver driver)
		{
			if (driver == null)
			{
				return double.NaN;
			}

			var steps = driver.StepCount;
			return steps > 0 ? SimulationClock.TimeAtStep(steps - 1) + driver.InterpolationFactor * SimulationClock.FixedTimeStep : 0.0;
		}

		/// <summary>Places every hull for the runner's current drawn time. Called every frame after the runner; tests call it directly.</summary>
		public void Place()
		{
			var traffic = _built;
			if (traffic == null || runner == null || runner.Driver == null || !ReferenceEquals(runner.Traffic, traffic))
			{
				HideAll();
				return;
			}

			if (_harborArea != null)
			{
				PlaceHarbor(traffic);
				return;
			}

			_watch.Restart();
			var time = RenderTime(runner.Driver);
			var packTime = traffic.PackTime(time);
			var replay = traffic.Replay;
			var monitor = runner.TrafficMonitor;
			var removed = traffic.RemovedVessel;
			var camera = viewCamera != null ? viewCamera : Camera.main;
			var hasCamera = camera != null && frame != null;
			double cameraNorth = 0.0, cameraEast = 0.0;
			if (hasCamera)
			{
				var local = frame.InverseTransformPoint(camera.transform.position);
				cameraNorth = local.z;
				cameraEast = local.x;
			}

			var range = (double)viewRange;
			var level = water != null ? water.WaterLevelNavd88 : 0.0;
			var lightsOn = NavigationLightsEnabled && Sun != null && Daylight.ShowsNavigationLights(Sun.Sun, Sun.VisibilityMetres);
			var shown = 0;
			for (var i = 0; i < _hulls.Count; i++)
			{
				var hull = _hulls[i];
				var vessel = hull.VesselIndex;
				TrafficSample sample = default;
				var visible = vessel != removed && replay.TrySample(vessel, packTime, out sample) &&
					(!_hasArea || (sample.North >= _minNorth && sample.North <= _maxNorth && sample.East >= _minEast && sample.East <= _maxEast)) &&
					(!hasCamera || (sample.North - cameraNorth) * (sample.North - cameraNorth) + (sample.East - cameraEast) * (sample.East - cameraEast) <= range * range);
				if (!visible)
				{
					if (_active[i])
					{
						_active[i] = false;
						hull.gameObject.SetActive(false);
						hull.Hide();
					}

					continue;
				}

				hull.NavigationLights.On = lightsOn;
				hull.Follow(sample, monitor != null && monitor.IsAlarmed(vessel), monitor != null && monitor.IsColliding(vessel));
				var transformed = _transforms[i];
				transformed.localPosition = new Vector3((float)sample.East, (float)WaterHeight(i, sample.North, sample.East, level), (float)sample.North);
				transformed.localRotation = Quaternion.Euler(0f, (float)(sample.Heading * (180.0 / Math.PI)), 0f);
				if (!_active[i])
				{
					_active[i] = true;
					hull.gameObject.SetActive(true);
				}

				shown++;
			}

			ShownCount = shown;
			PlacedTime = time;
			_watch.Stop();
			LastMilliseconds = _watch.Elapsed.TotalMilliseconds;
			MaximumMilliseconds = Math.Max(MaximumMilliseconds, LastMilliseconds);
			_totalMilliseconds += LastMilliseconds;
			_frames++;
		}

		private void PlaceHarbor(TrafficRun traffic)
		{
			_watch.Restart();
			var time = RenderTime(runner.Driver);
			var packTime = traffic.PackTime(time);
			var camera = viewCamera != null ? viewCamera : Camera.main;
			var hasCamera = camera != null;
			var eye = hasCamera ? camera.transform.position : Vector3.zero;
			var cameraEcef = hasCamera ? _georeference.TransformUnityPositionToEarthCenteredEarthFixed((double3)(float3)frame.InverseTransformPoint(eye)) : double3.zero;
			var lightsOn = NavigationLightsEnabled && Sun != null && Daylight.ShowsNavigationLights(Sun.Sun, Sun.VisibilityMetres);
			var extinction = SignalLightOptics.LightExtinction(Sun != null ? Sun.VisibilityMetres : 0);
			var threshold = SignalLightOptics.ThresholdLux(Sun != null ? Sun.Sun.ApparentElevationDegrees : 90);
			ActiveCount = ShownCount = LightsDrawn = ActivationChanges = MissingTargets = 0;
			for (var vessel = 0; vessel < _hullIndices.Length; vessel++)
			{
				var index = _hullIndices[vessel];
				TrafficSample sample = default;
				var present = vessel != traffic.RemovedVessel && traffic.Replay.TrySample(vessel, packTime, out sample);
				if (present) ActiveCount++;
				if (!present)
				{
					if (index >= 0) HideHarborHull(index);
					continue;
				}
				if (!_harborArea.Contains(sample.North, sample.East))
				{
					if (index >= 0) HideHarborHull(index);
					continue;
				}
				if (index < 0) { MissingTargets++; continue; }
				var hull = _hulls[index];
				// Sample at each render time, even when stationary. No scalar tide or spatial/time cache.
				var height = _harborWater.EllipsoidalHeight(sample.North, sample.East, time);
				if (double.IsNaN(height) || double.IsInfinity(height)) { MissingTargets++; HideHarborHull(index); continue; }
				_lattice.Sample(sample.North, sample.East, height, out var position, out var up, out var north);
				var distance = hasCamera ? math.distance(position, cameraEcef) : 0;
				var detail = !hasCamera || distance <= viewRange;
				// Conservative upper envelope: the nearest light can be closer than the reference point. Sector and
				// actual per-light optics stay in SignalLight's shader; this never clips a potentially visible light.
				var lightDistance = Math.Max(0.01, distance - hull.Length - hull.Beam - hull.Height - 2);
				var signal = lightsOn && (!hasCamera || SignalLightOptics.Illuminance(hull.MaximumLightIntensity, lightDistance, extinction) >= threshold);
				if (!detail && !signal) { HideHarborHull(index); continue; }
				TrafficGlobeLattice.Place(_georeference, _transforms[index], position, up, north, sample.Heading);
				hull.ShowDetail(detail);
				hull.NavigationLights.On = lightsOn;
				hull.Follow(sample, runner.TrafficMonitor != null && runner.TrafficMonitor.IsAlarmed(vessel),
					runner.TrafficMonitor != null && runner.TrafficMonitor.IsColliding(vessel));
				if (!_active[index]) { _active[index] = true; hull.gameObject.SetActive(true); ActivationChanges++; }
				if (detail) ShownCount++;
				if (lightsOn)
					for (var l = 0; l < hull.NavigationLights.Lights.Count; l++)
						if (!hasCamera || hull.NavigationLights.Lights[l].RatioToThreshold(eye, extinction, threshold) >= 1) LightsDrawn++;
			}
			PlacedTime = time;
			_watch.Stop();
			LastMilliseconds = _watch.Elapsed.TotalMilliseconds;
			MaximumMilliseconds = Math.Max(MaximumMilliseconds, LastMilliseconds);
			_totalMilliseconds += LastMilliseconds;
			_frames++;
		}

		private void HideHarborHull(int index)
		{
			if (!_active[index]) return;
			_active[index] = false;
			_hulls[index].NavigationLights.On = false;
			_hulls[index].gameObject.SetActive(false);
			_hulls[index].Hide();
			ActivationChanges++;
		}

		/// <summary>The hull for a vessel of the day, or null when it never comes near the slice.</summary>
		public TrafficHull HullFor(int mmsi)
		{
			for (var i = 0; i < _hulls.Count; i++)
			{
				if (_hulls[i].Mmsi == mmsi)
				{
					return _hulls[i];
				}
			}

			return null;
		}

		/// <summary>The drawn water's height under a hull, in the frame: looked up again only when the hull or the level moved.</summary>
		private double WaterHeight(int i, double north, double east, double level)
		{
			if (water == null || water.Datum == null || !water.Datum.IsPopulated)
			{
				return level;
			}

			var dn = north - _heightNorth[i];
			var de = east - _heightEast[i];
			if (!(dn * dn + de * de <= HeightCacheDistance * HeightCacheDistance) || _heightLevel[i] != level)
			{
				_height[i] = water.SurfaceUp(north, east);
				_heightNorth[i] = north;
				_heightEast[i] = east;
				_heightLevel[i] = level;
			}

			return _height[i];
		}

		private void ReadArea()
		{
			_hasArea = water != null && water.Datum != null && water.Datum.IsPopulated;
			if (!_hasArea)
			{
				return;
			}

			WaterSurface.SliceBounds(water.Datum, out _minNorth, out _maxNorth, out _minEast, out _maxEast);
			_minNorth -= areaMargin;
			_maxNorth += areaMargin;
			_minEast -= areaMargin;
			_maxEast += areaMargin;
		}

		private Material MaterialFor(TrafficClass vesselClass)
		{
			if (_materials.TryGetValue(vesselClass, out var material))
			{
				return material;
			}

			var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
			if (shader == null)
			{
				return null;
			}

			material = new Material(shader) { name = "Traffic " + TrafficClasses.Name(vesselClass) };
			var colour = TrafficHullMesh.ClassColor(vesselClass);
			material.color = colour;
			if (material.HasProperty("_BaseColor"))
			{
				material.SetColor("_BaseColor", colour);
			}

			if (material.HasProperty("_Smoothness"))
			{
				material.SetFloat("_Smoothness", 0.2f);
			}

			_materials[vesselClass] = material;
			return material;
		}

		private void HideAll()
		{
			for (var i = 0; i < _hulls.Count; i++)
			{
				if (_active[i])
				{
					_active[i] = false;
					// Hulls are parented to the world frame, which can be destroyed before this view during teardown.
					var hull = _hulls[i];
					if (hull == null)
					{
						continue;
					}

					if (hull.NavigationLights != null)
					{
						hull.NavigationLights.On = false;
					}

					hull.gameObject.SetActive(false);
					hull.Hide();
				}
			}

			ShownCount = ActiveCount = LightsDrawn = ActivationChanges = MissingTargets = 0;
		}

		private void Clear()
		{
			foreach (var hull in _hulls)
			{
				if (hull != null)
				{
					hull.gameObject.SetActive(false);
					DestroySafely(hull.gameObject);
				}
			}

			foreach (var mesh in _meshes)
			{
				DestroySafely(mesh);
			}

			_hulls.Clear();
			_meshes.Clear();
			_sharedMeshes.Clear();
			_renderMeshes.Clear();
			_renderMaterials.Clear();
			_hullIndices = Array.Empty<int>();
			_transforms = Array.Empty<Transform>();
			_active = Array.Empty<bool>();
			_built = null;
			ShownCount = ActiveCount = LightsDrawn = ActivationChanges = MissingTargets = 0;
			PlacedTime = double.NaN;
		}

		private static void DestroySafely(Object target)
		{
			if (target == null)
			{
				return;
			}

			if (Application.isPlaying)
			{
				Destroy(target);
			}
			else
			{
				DestroyImmediate(target);
			}
		}
	}
}
