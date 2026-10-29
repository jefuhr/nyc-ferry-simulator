using System;
using System.Collections.Generic;
using FerrySim.Core.World;
using FerrySim.UI;
using UnityEngine;

namespace FerrySim.World.HarborNavigation
{
	public sealed class HarborLabelGroup : MonoBehaviour
	{
		public HarborStop Source { get; internal set; }
		public string Details { get; internal set; }
		public bool Shown => _renderer != null && _renderer.enabled;
		public TextMesh Text { get; private set; }
		private Renderer _renderer;
		private bool? _night;
		public void Initialize(Font font, Material material, string text)
		{
			Text = gameObject.AddComponent<TextMesh>();
			Text.font = font; Text.fontSize = 48; Text.characterSize = .12f;
			Text.anchor = TextAnchor.LowerCenter; Text.alignment = TextAlignment.Center; Text.text = text;
			_renderer = GetComponent<Renderer>(); _renderer.sharedMaterial = material;
		}
		public void Refresh(Camera camera, string selectedStop, bool night)
		{
			if (_night != night) { Text.color = (night ? InstrumentPalette.Night : InstrumentPalette.Day).Text; _night = night; }
			var distance = camera != null ? Vector3.Distance(camera.transform.position, transform.position) : 0;
			_renderer.enabled = Source.Id == selectedStop || distance <= 750;
			if (camera == null) return;
			transform.rotation = camera.transform.rotation;
			transform.localScale = Vector3.one * Mathf.Clamp(distance * .018f, 1, 35);
		}
	}
}
