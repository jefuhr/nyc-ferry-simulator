using System;
using System.Collections.Generic;
using FerrySim.Core.World;
using FerrySim.UI;
using UnityEngine;

namespace FerrySim.World.HarborNavigation
{
	public sealed class HarborAidView : MonoBehaviour
	{
		public HarborAid Source { get; internal set; }
		public string Details { get; internal set; }
		public bool PeriodEstimated { get; internal set; }
		internal Renderer Body;
		internal int FirstLight, LightCount;
	}
}
