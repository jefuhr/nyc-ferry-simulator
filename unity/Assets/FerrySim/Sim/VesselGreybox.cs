using System;
using FerrySim.Core.Contracts;
using FerrySim.Vessels;
using UnityEngine;

namespace FerrySim.Sim
{
	/// <summary>
	/// Greybox sizes from a vessel file: two demihulls (length overall, <c>hull.demihullBeam</c> wide,
	/// <c>hull.demihullSeparation</c> apart, keel to main deck), a deck between them, and a cabin whose size
	/// is backed out of the windage areas (frontal area over beam gives the height above water, lateral area
	/// minus the hull side gives the cabin side). Metres, in the vessel frame with the origin at the
	/// reference point (centreline, midships, design waterline).
	/// </summary>
	public readonly struct GreyboxDimensions
	{
		public GreyboxDimensions(double lengthOverall, double beam, double demihullBeam, double demihullSeparation, double depth, double draft, double cabinLength, double cabinHeight)
		{
			LengthOverall = lengthOverall;
			Beam = beam;
			DemihullBeam = demihullBeam;
			DemihullSeparation = demihullSeparation;
			Depth = depth;
			Draft = draft;
			CabinLength = cabinLength;
			CabinHeight = cabinHeight;
		}

		public double LengthOverall { get; }

		public double Beam { get; }

		public double DemihullBeam { get; }

		/// <summary>Centreline to centreline of the two demihulls.</summary>
		public double DemihullSeparation { get; }

		/// <summary>Keel to main deck (moulded depth).</summary>
		public double Depth { get; }

		/// <summary>Hull draft: how far the keel sits below the waterline.</summary>
		public double Draft { get; }

		public double Freeboard => Depth - Draft;

		public double CabinLength { get; }

		public double CabinHeight { get; }

		/// <summary>Eye height of someone standing in the wheelhouse on the cabin top, above the waterline.</summary>
		public double BridgeEyeHeight => Freeboard + CabinHeight + 1.7;

		/// <summary>Forward position of the wheelhouse eye point from midships.</summary>
		public double BridgeEyeForward => 0.5 * CabinLength - 2.0;

		public static GreyboxDimensions From(VesselParameters parameters)
		{
			var particulars = parameters.Particulars;
			var loa = Value(particulars?.LengthOverall, 26.0);
			var beam = Value(particulars?.Beam, 8.0);
			var depth = Value(particulars?.Depth, 2.8);
			var draft = Value(particulars?.Draft, 1.0);
			var demihullBeam = Value(parameters.Hull?.DemihullBeam, 0.3 * beam);
			var separation = Value(parameters.Hull?.DemihullSeparation, beam - demihullBeam);
			var freeboard = Math.Max(0.5, depth - draft);

			var frontalArea = Value(parameters.Windage?.FrontalArea, 0.0);
			var lateralArea = Value(parameters.Windage?.LateralArea, 0.0);
			var cabinHeight = frontalArea > 0.0 ? Clamp(frontalArea / beam - freeboard, 2.0, 5.0) : 3.0;
			var cabinLength = lateralArea > 0.0 ? Clamp((lateralArea - loa * freeboard) / cabinHeight, 0.3 * loa, 0.85 * loa) : 0.7 * loa;
			return new GreyboxDimensions(loa, beam, demihullBeam, separation, depth, draft, cabinLength, cabinHeight);
		}

		private static double Value(ParameterValue parameter, double fallback) =>
			parameter != null && parameter.Value > 0.0 && !double.IsInfinity(parameter.Value) ? parameter.Value : fallback;

		private static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));
	}

	/// <summary>
	/// Shows the authored catamaran for the running vessel and keeps its parameter-sized procedural
	/// greybox available as a fallback. Both are visual only; contacts belong to the core.
	/// </summary>
	public sealed class VesselGreybox : MonoBehaviour
	{
		public const string RootName = "Greybox";

		[SerializeField]
		private VesselSimulationRunner runner;

		[SerializeField]
		private Material hullMaterial;

		[SerializeField]
		private Material cabinMaterial;

		[SerializeField, Tooltip("Use the procedural greybox instead of the authored ferry model.")]
		private bool useGreybox;

		public bool UseGreybox
		{
			get => useGreybox;
			set { useGreybox = value; Rebuild(); }
		}

		public GreyboxDimensions Dimensions { get; private set; }
		private string _modelClass;

		private void Start()
		{
			if (runner == null)
			{
				runner = GetComponentInParent<VesselSimulationRunner>();
			}

			if (runner == null)
			{
				return;
			}

			if (runner.IsInitialized)
			{
				Rebuild();
			}

			runner.RunStarted += Rebuild;
		}

		private void OnDestroy()
		{
			if (runner != null)
			{
				runner.RunStarted -= Rebuild;
			}
		}

		public void Rebuild()
		{
			if (runner == null || runner.Parameters == null)
			{
				return;
			}

			var dimensions = GreyboxDimensions.From(runner.Parameters);
			var greybox = transform.Find(RootName);
			var model = transform.Find(FerryModels.RootName);
			var id = runner.Parameters.Id;
			if (greybox == null || !dimensions.Equals(Dimensions) || _modelClass != id)
			{
				Dimensions = dimensions;
				greybox = Build(transform, dimensions, hullMaterial, cabinMaterial).transform;
				if (model != null)
				{
					model.gameObject.SetActive(false);
					model.name += " (replaced)";
					DestroySafely(model.gameObject);
				}

				var created = FerryModels.Create(transform, id, 0, dimensions.LengthOverall, dimensions.Beam);
				model = created != null ? created.transform : null;
				_modelClass = id;
			}

			greybox.gameObject.SetActive(useGreybox || model == null);
			if (model != null) { model.gameObject.SetActive(!useGreybox); }
		}

		/// <summary>Replaces any existing greybox under <paramref name="parent"/> with one of the given size.</summary>
		public static GameObject Build(Transform parent, GreyboxDimensions d, Material hull, Material cabin)
		{
			var existing = parent.Find(RootName);
			if (existing != null)
			{
				// Destroy is deferred while playing; hide and rename the old one so it is never found again.
				existing.gameObject.SetActive(false);
				existing.name = RootName + " (replaced)";
				DestroySafely(existing.gameObject);
			}

			var root = new GameObject(RootName);
			root.transform.SetParent(parent, false);
			var layer = LayerMask.NameToLayer("Vessel");
			var deckTop = d.Freeboard;
			var hullCentreY = 0.5 * d.Depth - d.Draft;
			Box(root.transform, "PortHull", new Vector3((float)(-0.5 * d.DemihullSeparation), (float)hullCentreY, 0f),
				new Vector3((float)d.DemihullBeam, (float)d.Depth, (float)d.LengthOverall), hull, layer);
			Box(root.transform, "StarboardHull", new Vector3((float)(0.5 * d.DemihullSeparation), (float)hullCentreY, 0f),
				new Vector3((float)d.DemihullBeam, (float)d.Depth, (float)d.LengthOverall), hull, layer);
			Box(root.transform, "Deck", new Vector3(0f, (float)(deckTop - 0.2), (float)(-0.02 * d.LengthOverall)),
				new Vector3((float)d.Beam, 0.4f, (float)(0.94 * d.LengthOverall)), hull, layer);
			Box(root.transform, "Cabin", new Vector3(0f, (float)(deckTop + 0.5 * d.CabinHeight), (float)(-0.04 * d.LengthOverall)),
				new Vector3((float)(d.Beam - 0.4), (float)d.CabinHeight, (float)d.CabinLength), cabin, layer);
			Box(root.transform, "Wheelhouse", new Vector3(0f, (float)(deckTop + d.CabinHeight + 1.1), (float)(d.BridgeEyeForward - 1.0)),
				new Vector3((float)(0.55 * d.Beam), 2.2f, 4.5f), cabin, layer);
			return root;
		}

		private static void Box(Transform parent, string name, Vector3 centre, Vector3 size, Material material, int layer)
		{
			var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
			box.name = name;
			box.transform.SetParent(parent, false);
			box.transform.localPosition = centre;
			box.transform.localScale = size;
			if (layer >= 0)
			{
				box.layer = layer;
			}

			// The greybox is only drawn; contact belongs to the core.
			DestroySafely(box.GetComponent<Collider>());
			if (material != null)
			{
				box.GetComponent<Renderer>().sharedMaterial = material;
			}
		}

		private static void DestroySafely(UnityEngine.Object target)
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
