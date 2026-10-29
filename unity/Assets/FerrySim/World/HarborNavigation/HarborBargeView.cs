using System;
using System.Collections.Generic;
using FerrySim.Core.World;
using FerrySim.UI;
using UnityEngine;

namespace FerrySim.World.HarborNavigation
{
	public sealed class HarborBargeView : MonoBehaviour
	{
		public const float EstimatedFreeboard = 1.2f, EstimatedDraft = 1.5f;
		public HarborBarge Source { get; internal set; }
		public NavigationAnchor Anchor { get; internal set; }
		public float Freeboard => EstimatedFreeboard;
		public float Draft => EstimatedDraft;
		public string Details { get; internal set; }
	}
}
