using System;
using System.Collections.Generic;
using FerrySim.Core.World;
using FerrySim.UI;
using UnityEngine;

namespace FerrySim.World.HarborNavigation
{
	public sealed class HarborSpanView : MonoBehaviour
	{
		public HarborSpan Source { get; internal set; }
		public string BridgeId { get; internal set; }
		public bool Drawn { get; internal set; }
		public bool DeckEstimated => !Source.VerticalBounds.Minimum.HasValue;
		public bool TowerEstimated => !Source.VerticalBounds.Maximum.HasValue;
		// These values are presentation only. The source and resident query index retain their nulls.
		public double? DisplayUndersideNavd88 { get; internal set; }
		public string DisplayHeightBasis { get; internal set; }
	}
}
