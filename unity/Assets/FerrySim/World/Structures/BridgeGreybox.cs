using System;
using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// A greybox bridge generated from the pipeline's bridges.geojson (NOAA ENC). The deck's underside sits
	/// at the published vertical clearance, which is metres above mean high water (MHW) at the chart's
	/// vertical datum, converted to NAVD88 with the datum station's MHW. Tower heights are not in the ENC
	/// data; they are a greybox estimate and flagged as such.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class BridgeGreybox : MonoBehaviour
	{
		[SerializeField] private string bridgeName = "";
		[SerializeField] private string[] encIds = Array.Empty<string>();

		[Header("Published clearance (NOAA ENC)")]
		[Tooltip("Vertical clearance of the main span, metres above the clearance datum.")]
		[SerializeField] private double verticalClearance;
		[SerializeField] private string clearanceDatum = "";
		[Tooltip("Navigable width between fenders or piers, metres.")]
		[SerializeField] private double horizontalClearance;
		[SerializeField] private string information = "";

		[Header("Heights, metres")]
		[Tooltip("Mean high water at the datum station, above NAVD88.")]
		[SerializeField] private double meanHighWaterNavd88;
		[Tooltip("Deck underside: MHW plus the vertical clearance, above NAVD88.")]
		[SerializeField] private double deckUndersideNavd88;
		[Tooltip("Deck underside as a WGS84 ellipsoidal height at the span's middle.")]
		[SerializeField] private double deckUndersideEllipsoidal;
		[SerializeField] private double towerTopNavd88;
		[SerializeField] private bool towerHeightIsEstimate = true;

		public string BridgeName => bridgeName;

		public string[] EncIds => encIds;

		public double VerticalClearance => verticalClearance;

		public string ClearanceDatum => clearanceDatum;

		public double HorizontalClearance => horizontalClearance;

		public string Information => information;

		public double MeanHighWaterNavd88 => meanHighWaterNavd88;

		public double DeckUndersideNavd88 => deckUndersideNavd88;

		public double DeckUndersideEllipsoidal => deckUndersideEllipsoidal;

		public double TowerTopNavd88 => towerTopNavd88;

		public bool TowerHeightIsEstimate => towerHeightIsEstimate;

		/// <summary>Fills the component. Only the edit-time builder calls this.</summary>
		public void Assign(BridgeGreyboxValues values)
		{
			bridgeName = values.BridgeName ?? "";
			encIds = values.EncIds ?? Array.Empty<string>();
			verticalClearance = values.VerticalClearance;
			clearanceDatum = values.ClearanceDatum ?? "";
			horizontalClearance = values.HorizontalClearance;
			information = values.Information ?? "";
			meanHighWaterNavd88 = values.MeanHighWaterNavd88;
			deckUndersideNavd88 = values.DeckUndersideNavd88;
			deckUndersideEllipsoidal = values.DeckUndersideEllipsoidal;
			towerTopNavd88 = values.TowerTopNavd88;
			towerHeightIsEstimate = values.TowerHeightIsEstimate;
		}
	}

	/// <summary>Plain values for <see cref="BridgeGreybox.Assign"/>.</summary>
	public sealed class BridgeGreyboxValues
	{
		public string BridgeName;
		public string[] EncIds;
		public double VerticalClearance;
		public string ClearanceDatum;
		public double HorizontalClearance;
		public string Information;
		public double MeanHighWaterNavd88;
		public double DeckUndersideNavd88;
		public double DeckUndersideEllipsoidal;
		public double TowerTopNavd88;
		public bool TowerHeightIsEstimate;
	}
}
