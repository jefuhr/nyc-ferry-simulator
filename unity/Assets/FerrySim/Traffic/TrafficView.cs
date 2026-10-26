using System;
using System.Collections.Generic;
using System.Diagnostics;
using FerrySim.Core.Contracts;
using FerrySim.Core.Traffic;
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
		private readonly Stopwatch _watch = new Stopwatch();
		private TrafficRun _built;
		private Transform[] _transforms = Array.Empty<Transform>();
		private bool[] _active = Array.Empty<bool>();
		private double[] _heightNorth = Array.Empty<double>();
		private double[] _heightEast = Array.Empty<double>();
		private double[] _heightLevel = Array.Empty<double>();
		private double[] _height = Array.Empty<double>();
		private bool _hasArea;
		private double _minNorth, _maxNorth, _minEast, _maxEast;
		private bool _subscribed;
		private long _frames;
		private double _totalMilliseconds;

		/// <summary>Every hull built for the run's traffic, in pack order, drawn or not.</summary>
		public IReadOnlyList<TrafficHull> Hulls => _hulls;

		/// <summary>Hulls drawn this frame.</summary>
		public int ShownCount { get; private set; }

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

		/// <summary>Sets what the view follows, for bootstrap code and tests, before or after it is enabled.</summary>
		public void Connect(VesselSimulationRunner simulationRunner, Transform worldFrame, WaterSurface waterSurface, Camera camera)
		{
			Unsubscribe();
			Clear();
			runner = simulationRunner;
			frame = worldFrame;
			water = waterSurface;
			viewCamera = camera;
			if (isActiveAndEnabled)
			{
				Subscribe();
			}

			Rebuild();
		}

		private void OnEnable()
		{
			Subscribe();
			Rebuild();
		}

		private void OnDisable() => Unsubscribe();

		private void OnDestroy()
		{
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
				if (_hasArea && !traffic.Replay.MayEnter(vessel.Index, _minNorth, _maxNorth, _minEast, _maxEast))
				{
					continue;
				}

				var mesh = TrafficHullMesh.Build(vessel.Class, vessel.Hull, out var height);
				_meshes.Add(mesh);
				var hull = new GameObject($"Traffic {vessel.Mmsi} {vessel.Name}".TrimEnd());
				hull.transform.SetParent(parent, false);
				hull.AddComponent<MeshFilter>().sharedMesh = mesh;
				hull.AddComponent<MeshRenderer>().sharedMaterial = MaterialFor(vessel.Class);
				var component = hull.AddComponent<TrafficHull>();
				component.Describe(vessel, height);
				hull.SetActive(false);
				hulls.Add(component);
			}

			_hulls.AddRange(hulls);
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

			Debug.Log($"[FerrySim] Traffic {pack.Date}: {_hulls.Count} of the day's {pack.Vessels.Count} vessels come near the slice; {(traffic.Removed != null ? "leaving out " + traffic.Removed : "none left out")}.", this);
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
					_hulls[i].gameObject.SetActive(false);
					_hulls[i].Hide();
				}
			}

			ShownCount = 0;
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
			_transforms = Array.Empty<Transform>();
			_active = Array.Empty<bool>();
			_built = null;
			ShownCount = 0;
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
