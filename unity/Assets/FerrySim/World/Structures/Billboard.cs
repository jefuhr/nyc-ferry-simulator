using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// Keeps a debug label facing the main camera and roughly the same size on screen: its scale grows
	/// with distance between a minimum and a maximum, and hidden when far away. Runs after the camera
	/// controllers.
	/// </summary>
	[ExecuteAlways]
	[DisallowMultipleComponent]
	public sealed class Billboard : MonoBehaviour
	{
		[Tooltip("Scale per metre of distance from the camera.")]
		[Min(0f)]
		[SerializeField] private float scalePerMetre = 0.05f;
		[Min(0.01f)]
		[SerializeField] private float minScale = 1f;
		[Min(0.01f)]
		[SerializeField] private float maxScale = 60f;
		[Tooltip("Hide the label beyond this distance, metres, so far labels do not clutter the view.")]
		[Min(1f)]
		[SerializeField] private float maxDistance = 2500f;
		[Tooltip("Optional dark plate behind the text, sized to it every frame so the label reads on any sky.")]
		[SerializeField] private Transform backing;
		[Min(0f)]
		[SerializeField] private float backingPadding = 0.15f;

		private Renderer _renderer;

		/// <summary>Sets the backing plate; the world builder calls this.</summary>
		public void SetBacking(Transform plate) => backing = plate;

		private void LateUpdate()
		{
			var camera = Camera.main;
			if (camera == null)
			{
				return;
			}

			var cameraTransform = camera.transform;
			transform.rotation = cameraTransform.rotation;
			var distance = Vector3.Distance(cameraTransform.position, transform.position);
			var scale = Mathf.Clamp(distance * scalePerMetre, minScale, maxScale);
			transform.localScale = new Vector3(scale, scale, scale);

			if (_renderer == null)
			{
				_renderer = GetComponent<Renderer>();
			}

			if (_renderer == null)
			{
				return;
			}

			var visible = distance <= maxDistance;
			_renderer.enabled = visible;
			if (backing == null)
			{
				return;
			}

			// The text mesh's local bounds; the plate sits just behind it (the text faces -z).
			var bounds = _renderer.localBounds;
			var plate = backing.GetComponent<Renderer>();
			if (plate != null)
			{
				plate.enabled = visible && bounds.size.x > 0f;
			}

			backing.localPosition = new Vector3(bounds.center.x, bounds.center.y, 0.05f);
			backing.localRotation = Quaternion.identity;
			backing.localScale = new Vector3(bounds.size.x + 2f * backingPadding, bounds.size.y + 2f * backingPadding, 1f);
		}
	}
}
