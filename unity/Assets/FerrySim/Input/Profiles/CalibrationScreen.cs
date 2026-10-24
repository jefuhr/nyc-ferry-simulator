using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.Controls.Profiles
{
	/// <summary>Standalone UI Toolkit host; shares the active controller's profile store when present.</summary>
	[DefaultExecutionOrder(-100)]
	[DisallowMultipleComponent]
	public sealed class CalibrationScreen : MonoBehaviour
	{
		private DeviceProfileStore _configuredStore;
		private PanelSettings _settings;
		private RenderTexture _target;
		public CalibrationView View { get; private set; }
		public void Configure(DeviceProfileStore store, RenderTexture target = null)
		{
			_configuredStore = store;
			_target = target;
		}
		private void OnEnable()
		{
			var controller = FindFirstObjectByType<HelmInputController>();
			var store = _configuredStore ?? controller?.Profiles;
			if (store == null)
			{
				store = new DeviceProfileStore(DeviceProfileStore.DefaultPath);
				if (!store.Load()) Debug.LogWarning("[FerrySim] " + store.LastError, this);
			}
			_settings = ScriptableObject.CreateInstance<PanelSettings>();
			_settings.themeStyleSheet = Resources.Load<ThemeStyleSheet>("FerrySimUI/WheelhouseTheme");
			_settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
			_settings.referenceResolution = new Vector2Int(1280, 720);
			_settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
			_settings.match = 0.5f;
			if (_target != null)
			{
				_settings.targetTexture = _target;
				_settings.clearColor = true;
				_settings.colorClearValue = new Color(14f / 255f, 19f / 255f, 24f / 255f);
			}
			var document = GetComponent<UIDocument>() ?? gameObject.AddComponent<UIDocument>();
			document.panelSettings = _settings;
			View = new CalibrationView(store);
			document.rootVisualElement.Add(View);
			View.StretchToParentSize();
		}
		private void Update() => View?.Refresh();
		private void OnDisable()
		{
			View?.Dispose(); View?.RemoveFromHierarchy(); View = null;
			var document = GetComponent<UIDocument>();
			if (document != null) document.panelSettings = null;
			if (_settings != null) { _settings.targetTexture = null; Destroy(_settings); }
			_settings = null;
		}
	}
}
