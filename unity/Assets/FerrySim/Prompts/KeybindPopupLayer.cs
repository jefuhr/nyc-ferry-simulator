using System;
using FerrySim.Controls;
using FerrySim.UI;
using FerrySim.Vessels;
using FerrySim.World;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.Prompts
{
	/// <summary>What one popup shows and where, for tests and diagnostics. Positions are panel pixels, y down.</summary>
	public readonly struct KeybindPopupState
	{
		public KeybindPopupState(string mountId, string keyText, string caption, bool visible, Rect plate, Vector2 anchor, float scale)
		{
			MountId = mountId;
			KeyText = keyText;
			Caption = caption;
			Visible = visible;
			Plate = plate;
			Anchor = anchor;
			Scale = scale;
		}

		/// <summary>The control's mount id (C3).</summary>
		public string MountId { get; }

		/// <summary>The binding text shown, empty when no action of the control is bound in the active scheme.</summary>
		public string KeyText { get; }

		/// <summary>The control's label from the control map.</summary>
		public string Caption { get; }

		public bool Visible { get; }

		/// <summary>The plate as placed (already scaled), in panel pixels. Meaningful while <see cref="Visible"/>.</summary>
		public Rect Plate { get; }

		/// <summary>Where the control's pivot projects, in panel pixels. Meaningful while <see cref="Visible"/>.</summary>
		public Vector2 Anchor { get; }

		/// <summary>The plate's scale: 1 for a control near the view centre and close by, smaller further out.</summary>
		public float Scale { get; }
	}

	/// <summary>
	/// A full-screen, non-interactive layer of keybind popups: for each wheelhouse control that has input actions
	/// (C3), a plate with the binding of the first action bound in the active scheme and, under it, the control's
	/// label, hovering just above the control with a short leader to its pivot.
	/// <para>
	/// The text is rebuilt only when the bindings change (<see cref="IBindingService.BindingsChanged"/>), the
	/// scheme changes or the controls are replaced. <see cref="Refresh"/> runs every frame: it projects each
	/// control, hides those behind the camera, off screen or facing away, declutters the rest
	/// (<see cref="KeybindDeclutter"/>) and moves the plates with <c>translate</c>, <c>scale</c> and <c>rotate</c>
	/// on dynamic-transform elements; it never sets <c>left</c> or <c>top</c> and allocates nothing. The labels are a
	/// fixed pool of <see cref="PoolSize"/> built up front.
	/// </para>
	/// <para>
	/// Emphasis falls off with distance and with distance from the view centre through the plate's size and the
	/// leader's opacity. The plate itself stays opaque: any translucency over a bright window takes the captions
	/// below WCAG AA (UI/README.md, Day and night).
	/// </para>
	/// </summary>
	public sealed class KeybindPopupLayer : VisualElement
	{
		/// <summary>The most controls one interior can have popups for; more are ignored with a warning.</summary>
		public const int PoolSize = 24;

		/// <summary>The most popups shown at once, chosen by nearness to the view centre.</summary>
		public const int MaxShown = 12;

		/// <summary>Key text size in reference pixels (1280×720).</summary>
		public const float KeyFontSize = 18f;

		/// <summary>Label text size in reference pixels.</summary>
		public const float CaptionFontSize = 12f;

		/// <summary>Gap between a control's pivot and its plate, reference pixels.</summary>
		public const float LeaderLength = 18f;

		/// <summary>Plates are kept this far inside the screen edges, reference pixels.</summary>
		public const float ScreenMargin = 4f;

		/// <summary>The smallest plate scale (far away and at the edge of the view).</summary>
		public const float MinScale = 0.8f;

		/// <summary>The leader's opacity at the weakest.</summary>
		public const float MinLeaderOpacity = 0.4f;

		/// <summary>Full strength up to this distance from the eye, metres.</summary>
		public const float FullStrengthDistance = 1.5f;

		/// <summary>Weakest from this distance from the eye, metres.</summary>
		public const float WeakestDistance = 6f;

		/// <summary>Full strength within this fraction of the half diagonal from the view centre.</summary>
		public const float FullStrengthRadius = 0.15f;

		/// <summary>The panel size assumed before the layer has been laid out (and in tests without a panel).</summary>
		public static readonly Vector2 FallbackPanelSize = new Vector2(1280f, 720f);

		private const float PlatePaddingX = 7f;
		private const float PlatePaddingTop = 2f;
		private const float PlatePaddingBottom = 3f;
		private const float PlateBorder = 1f;
		private const float DotSize = 7f;
		private const float LeaderWidth = 4f;

		private readonly Popup[] _popups = new Popup[PoolSize];
		private readonly Target[] _targets = new Target[PoolSize];
		private readonly KeybindDeclutter _declutter = new KeybindDeclutter(PoolSize);
		private readonly Action _onBindingsChanged;
		private int _count;
		private Transform _interior;
		private bool _hasInterior;
		private IBindingService _bindings;
		private string _scheme = KeybindSchemes.Keyboard;
		private PaletteKind _palette = PaletteKind.Day;
		private bool _displayed = true;
		private int _visible;
		private bool _warnedPool;

		public KeybindPopupLayer()
		{
			name = "keybind-popups";
			pickingMode = PickingMode.Ignore;
			focusable = false;
			style.position = Position.Absolute;
			style.left = 0f;
			style.top = 0f;
			style.right = 0f;
			style.bottom = 0f;
			style.overflow = Overflow.Visible;
			for (var i = 0; i < PoolSize; i++)
			{
				_popups[i] = new Popup(i);
				Add(_popups[i].Group);
			}

			ApplyPalette();
			_onBindingsChanged = Rebuild;
			SetDisplayed(false);
		}

		/// <summary>Off, while the hints key is held, or always (C5 <c>hud.keybindHints</c>).</summary>
		public KeybindHintMode Mode { get; set; } = KeybindHintMode.Hold;

		/// <summary>True hides every popup: the options menu or the scenario picker is open.</summary>
		public bool Suppressed { get; set; }

		/// <summary>Whether <c>Interface/KeybindHints</c> is held (set by <see cref="KeybindPopups"/>).</summary>
		public bool HoldPressed { get; set; }

		/// <summary>Whether the camera is in the wheelhouse view, the lean-in view included (set by <see cref="KeybindPopups"/>).</summary>
		public bool InWheelhouseView { get; set; } = true;

		/// <summary>Day or night colours from <see cref="InstrumentPalette"/>.</summary>
		public PaletteKind Palette
		{
			get => _palette;
			set
			{
				if (_palette == value)
				{
					return;
				}

				_palette = value;
				ApplyPalette();
			}
		}

		/// <summary>The control scheme whose bindings are shown: "Keyboard", "Gamepad" or "Joystick". A change rebuilds the text.</summary>
		public string Scheme
		{
			get => _scheme;
			set
			{
				value ??= KeybindSchemes.Keyboard;
				if (value == _scheme)
				{
					return;
				}

				_scheme = value;
				Rebuild();
			}
		}

		/// <summary>True when the popups are to be shown this frame (mode, hold key, suppression, view and interior allow it).</summary>
		public bool IsShowing =>
			_count > 0 && _hasInterior && _interior != null && InWheelhouseView && !Suppressed &&
			(Mode == KeybindHintMode.Always || (Mode == KeybindHintMode.Hold && HoldPressed));

		/// <summary>How many controls have a popup (controls with at least one action).</summary>
		public int ControlCount => _count;

		/// <summary>How many popups are on screen after the last <see cref="Refresh"/>.</summary>
		public int VisibleCount => _displayed ? _visible : 0;

		/// <summary>How many times the text has been rebuilt (for tests: never per frame).</summary>
		public int RebuildCount { get; private set; }

		/// <summary>The colour of the label under the key: dim text by day, the text colour at night (the night dim text is under 3:1 on black).</summary>
		public static Color CaptionColor(InstrumentPalette palette) => palette.Kind == PaletteKind.Night ? palette.Text : palette.TextDim;

		/// <summary>Where the key text comes from. Rebuilds the text now and again on every <see cref="IBindingService.BindingsChanged"/>.</summary>
		public void SetBindings(IBindingService bindings)
		{
			if (_bindings != null)
			{
				_bindings.BindingsChanged -= _onBindingsChanged;
			}

			_bindings = bindings;
			if (_bindings != null)
			{
				_bindings.BindingsChanged += _onBindingsChanged;
			}

			Rebuild();
		}

		/// <summary>
		/// The controls of one interior instance: the entries of its control map that have actions. Call again after
		/// a class switch (the entries belong to one instance). Null for either hides everything.
		/// </summary>
		public void SetControls(Transform interiorRoot, ControlMapFile controlMap)
		{
			_interior = interiorRoot;
			_hasInterior = interiorRoot != null && controlMap != null;
			_count = 0;
			_declutter.Reset();
			if (_hasInterior)
			{
				var mounts = interiorRoot.GetComponent<WheelhouseControlMap>();
				foreach (var entry in controlMap.Entries)
				{
					if (entry.Actions.Count == 0 || entry.Transform == null)
					{
						continue;
					}

					if (_count == PoolSize)
					{
						if (!_warnedPool)
						{
							Debug.LogWarning($"Keybind popups: more than {PoolSize} controls have actions; the rest get no popup.");
							_warnedPool = true;
						}

						break;
					}

					var actions = new string[entry.Actions.Count];
					for (var a = 0; a < actions.Length; a++)
					{
						actions[a] = entry.Actions[a];
					}

					var normal = Vector3.zero;
					if (mounts != null)
					{
						foreach (var mount in mounts.Controls)
						{
							if (mount.Id == entry.MountId)
							{
								normal = mount.Normal;
								break;
							}
						}
					}

					_targets[_count++] = new Target
					{
						MountId = entry.MountId,
						Pivot = entry.Transform,
						Space = interiorRoot,
						LocalNormal = normal,
						HasNormal = normal.sqrMagnitude > 1e-6f,
						Actions = actions,
						Label = entry.Label ?? string.Empty,
					};
				}
			}

			for (var i = 0; i < PoolSize; i++)
			{
				_popups[i].Forget();
				if (i >= _count)
				{
					_targets[i] = default;
				}
			}

			Rebuild();
		}

		/// <summary>Places the popups for this frame. Allocation-free; text is not touched.</summary>
		public void Refresh(Camera camera)
		{
			if (camera == null || !IsShowing)
			{
				if (_displayed)
				{
					for (var i = 0; i < PoolSize; i++)
					{
						_popups[i].SetVisible(false);
					}

					SetDisplayed(false);
				}

				_visible = 0;
				return;
			}

			SetDisplayed(true);
			var size = layout.size;
			if (!(size.x >= 1f) || !(size.y >= 1f))
			{
				size = FallbackPanelSize;
			}

			var centre = size * 0.5f;
			var halfDiagonal = centre.magnitude;
			var eye = camera.transform.position;
			var near = camera.nearClipPlane;
			for (var i = 0; i < _count; i++)
			{
				ref var target = ref _targets[i];
				if (!target.Bound || target.Pivot == null)
				{
					_declutter.Clear(i);
					continue;
				}

				var world = target.Pivot.position;
				var viewport = camera.WorldToViewportPoint(world);
				if (viewport.z <= near || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
				{
					_declutter.Clear(i);
					continue;
				}

				var toEye = eye - world;
				if (target.HasNormal && target.Space != null && Vector3.Dot(target.Space.TransformDirection(target.LocalNormal), toEye) <= 0f)
				{
					_declutter.Clear(i);
					continue;
				}

				var anchor = new Vector2(viewport.x * size.x, (1f - viewport.y) * size.y);
				var priority = Vector2.Distance(anchor, centre) / halfDiagonal;
				var strength = Strength(toEye.magnitude, priority);
				var scale = Mathf.Lerp(MinScale, 1f, strength);
				target.Anchor = anchor;
				target.Strength = strength;
				target.Scale = scale;
				var plate = _popups[i].Plate.layout;
				var plateSize = plate.width >= 1f && plate.height >= 1f ? plate.size : target.Estimate;
				_declutter.Set(i, anchor, plateSize * scale, priority);
			}

			_visible = _declutter.Solve(_count, size, LeaderLength, MaxShown, ScreenMargin);
			for (var i = 0; i < PoolSize; i++)
			{
				var popup = _popups[i];
				if (i >= _count || !_declutter.Shown(i))
				{
					popup.SetVisible(false);
					continue;
				}

				ref var target = ref _targets[i];
				popup.Place(target.Anchor, _declutter.Placed(i), target.Scale, target.Strength);
				popup.SetVisible(true);
			}
		}

		/// <summary>The popup for a control, for tests and diagnostics.</summary>
		public bool TryGetState(string mountId, out KeybindPopupState state)
		{
			for (var i = 0; i < _count; i++)
			{
				if (_targets[i].MountId == mountId)
				{
					state = GetState(i);
					return true;
				}
			}

			state = default;
			return false;
		}

		/// <summary>The popup for control <paramref name="index"/> (0 to <see cref="ControlCount"/> − 1), in control map order.</summary>
		public KeybindPopupState GetState(int index)
		{
			ref var target = ref _targets[index];
			return new KeybindPopupState(target.MountId, target.KeyText ?? string.Empty, target.Label ?? string.Empty,
				_displayed && _declutter.Shown(index), _declutter.Placed(index), target.Anchor, target.Scale);
		}

		internal VisualElement PlateElement(string mountId)
		{
			for (var i = 0; i < _count; i++)
			{
				if (_targets[i].MountId == mountId)
				{
					return _popups[i].Plate;
				}
			}

			return null;
		}

		private static float Strength(float distance, float priority)
		{
			var byDistance = 1f - Mathf.Clamp01((distance - FullStrengthDistance) / (WeakestDistance - FullStrengthDistance));
			var byCentre = 1f - Mathf.Clamp01((priority - FullStrengthRadius) / (1f - FullStrengthRadius));
			return byDistance * byCentre;
		}

		private void Rebuild()
		{
			RebuildCount++;
			for (var i = 0; i < _count; i++)
			{
				ref var target = ref _targets[i];
				var text = string.Empty;
				if (_bindings != null)
				{
					foreach (var action in target.Actions)
					{
						var shown = _bindings.DisplayString(action, _scheme);
						if (!string.IsNullOrEmpty(shown))
						{
							text = shown;
							break;
						}
					}
				}

				target.KeyText = text;
				target.Bound = text.Length > 0;
				target.Estimate = EstimateSize(text, target.Label);
				_popups[i].SetText(text, target.Label);
			}
		}

		/// <summary>A plate's size before the panel has measured it, from the character counts.</summary>
		private static Vector2 EstimateSize(string key, string caption)
		{
			var width = Mathf.Max(KeyFontSize * 0.62f * key.Length, CaptionFontSize * 0.56f * caption.Length);
			var height = KeyFontSize * 1.25f + (caption.Length > 0 ? CaptionFontSize * 1.25f : 0f);
			return new Vector2(width + 2f * (PlatePaddingX + PlateBorder), height + PlatePaddingTop + PlatePaddingBottom + 2f * PlateBorder);
		}

		private void SetDisplayed(bool displayed)
		{
			if (_displayed == displayed)
			{
				return;
			}

			_displayed = displayed;
			style.display = displayed ? DisplayStyle.Flex : DisplayStyle.None;
		}

		private void ApplyPalette()
		{
			var palette = InstrumentPalette.For(_palette);
			for (var i = 0; i < PoolSize; i++)
			{
				_popups[i].ApplyPalette(palette);
			}
		}

		private struct Target
		{
			public string MountId;
			public Transform Pivot;
			public Transform Space;
			public Vector3 LocalNormal;
			public bool HasNormal;
			public string[] Actions;
			public string Label;
			public string KeyText;
			public bool Bound;
			public Vector2 Estimate;
			public Vector2 Anchor;
			public float Strength;
			public float Scale;
		}

		/// <summary>
		/// One pooled popup: a zero-size group at the control's pivot holding the leader (rotated and stretched from
		/// the pivot to the plate), a dot on the pivot and the plate (translated relative to the pivot and scaled).
		/// Writes a style only when its value changes.
		/// </summary>
		private sealed class Popup
		{
			public readonly VisualElement Group;
			public readonly VisualElement Plate;
			private readonly VisualElement _leader;
			private readonly VisualElement _dot;
			private readonly Label _key;
			private readonly Label _caption;
			private bool _visible = true;
			private Vector2 _groupAt = new Vector2(float.NaN, float.NaN);
			private Vector2 _plateAt = new Vector2(float.NaN, float.NaN);
			private float _scale = float.NaN;
			private float _angle = float.NaN;
			private float _length = float.NaN;
			private float _leaderOpacity = float.NaN;

			public Popup(int index)
			{
				Group = new VisualElement { name = "keybind-popup-" + index, pickingMode = PickingMode.Ignore, focusable = false, usageHints = UsageHints.DynamicTransform };
				Group.style.position = Position.Absolute;
				Group.style.left = 0f;
				Group.style.top = 0f;
				Group.style.width = 0f;
				Group.style.height = 0f;
				Group.style.overflow = Overflow.Visible;

				_leader = new VisualElement { name = "keybind-leader", pickingMode = PickingMode.Ignore, focusable = false, usageHints = UsageHints.DynamicTransform };
				_leader.style.position = Position.Absolute;
				_leader.style.left = -LeaderWidth * 0.5f;
				_leader.style.top = 0f;
				_leader.style.width = LeaderWidth;
				_leader.style.height = 1f;
				_leader.style.borderLeftWidth = 1f;
				_leader.style.borderRightWidth = 1f;
				_leader.style.transformOrigin = new TransformOrigin(new Length(LeaderWidth * 0.5f, LengthUnit.Pixel), new Length(0f, LengthUnit.Pixel));

				_dot = new VisualElement { name = "keybind-pivot", pickingMode = PickingMode.Ignore, focusable = false };
				_dot.style.position = Position.Absolute;
				_dot.style.left = -DotSize * 0.5f;
				_dot.style.top = -DotSize * 0.5f;
				_dot.style.width = DotSize;
				_dot.style.height = DotSize;
				SetBorder(_dot.style, 1f, DotSize * 0.5f);

				Plate = new VisualElement { name = "keybind-plate", pickingMode = PickingMode.Ignore, focusable = false, usageHints = UsageHints.DynamicTransform };
				Plate.style.position = Position.Absolute;
				Plate.style.left = 0f;
				Plate.style.top = 0f;
				Plate.style.flexDirection = FlexDirection.Column;
				Plate.style.alignItems = Align.Center;
				Plate.style.paddingLeft = PlatePaddingX;
				Plate.style.paddingRight = PlatePaddingX;
				Plate.style.paddingTop = PlatePaddingTop;
				Plate.style.paddingBottom = PlatePaddingBottom;
				SetBorder(Plate.style, PlateBorder, 4f);
				Plate.style.transformOrigin = new TransformOrigin(new Length(0f, LengthUnit.Pixel), new Length(0f, LengthUnit.Pixel));

				_key = MakeLabel("keybind-key", KeyFontSize, FontStyle.Bold);
				_caption = MakeLabel("keybind-caption", CaptionFontSize, FontStyle.Normal);
				Plate.Add(_key);
				Plate.Add(_caption);
				Group.Add(_leader);
				Group.Add(_dot);
				Group.Add(Plate);
				SetVisible(false);
			}

			public void SetText(string key, string caption)
			{
				_key.text = key;
				_caption.text = caption;
				_caption.style.display = string.IsNullOrEmpty(caption) ? DisplayStyle.None : DisplayStyle.Flex;
			}

			public void ApplyPalette(InstrumentPalette palette)
			{
				Plate.style.backgroundColor = palette.HudBacking;
				SetBorderColor(Plate.style, palette.Bezel);
				_key.style.color = palette.Text;
				_caption.style.color = CaptionColor(palette);
				_leader.style.backgroundColor = palette.Needle;
				_leader.style.borderLeftColor = palette.HudBacking;
				_leader.style.borderRightColor = palette.HudBacking;
				_dot.style.backgroundColor = palette.Needle;
				SetBorderColor(_dot.style, palette.HudBacking);
			}

			/// <summary>Forgets the written transforms so the next placement writes them all (new controls).</summary>
			public void Forget()
			{
				SetVisible(false);
				_groupAt = _plateAt = new Vector2(float.NaN, float.NaN);
				_scale = _angle = _length = _leaderOpacity = float.NaN;
			}

			public void SetVisible(bool visible)
			{
				if (_visible == visible)
				{
					return;
				}

				_visible = visible;
				Group.style.visibility = visible ? Visibility.Visible : Visibility.Hidden;
			}

			public void Place(Vector2 anchor, Rect plate, float scale, float strength)
			{
				if (Moved(ref _groupAt, anchor))
				{
					Group.style.translate = new Translate(new Length(anchor.x, LengthUnit.Pixel), new Length(anchor.y, LengthUnit.Pixel));
				}

				var offset = plate.position - anchor;
				if (Moved(ref _plateAt, offset))
				{
					Plate.style.translate = new Translate(new Length(offset.x, LengthUnit.Pixel), new Length(offset.y, LengthUnit.Pixel));
				}

				if (Changed(ref _scale, scale, 1e-4f))
				{
					Plate.style.scale = new Scale(new Vector3(scale, scale, 1f));
				}

				// The leader runs from the pivot to the middle of the plate's near edge.
				var end = new Vector2(plate.center.x, plate.yMax <= anchor.y ? plate.yMax : plate.yMin) - anchor;
				var length = end.magnitude;
				var angle = length > 0.01f ? Mathf.Atan2(-end.x, end.y) * Mathf.Rad2Deg : 0f;
				if (Changed(ref _angle, angle, 0.01f))
				{
					_leader.style.rotate = new Rotate(new Angle(angle, AngleUnit.Degree));
				}

				if (Changed(ref _length, length, 0.005f))
				{
					_leader.style.scale = new Scale(new Vector3(1f, Mathf.Max(length, 0.0001f), 1f));
				}

				var opacity = Mathf.Round(Mathf.Lerp(MinLeaderOpacity, 1f, strength) * 16f) / 16f;
				if (Changed(ref _leaderOpacity, opacity, 1e-3f))
				{
					_leader.style.opacity = opacity;
					_dot.style.opacity = opacity;
				}
			}

			private static bool Moved(ref Vector2 written, Vector2 value)
			{
				if (Mathf.Abs(written.x - value.x) < 0.005f && Mathf.Abs(written.y - value.y) < 0.005f)
				{
					return false;
				}

				written = value;
				return true;
			}

			private static bool Changed(ref float written, float value, float tolerance)
			{
				if (Mathf.Abs(written - value) < tolerance)
				{
					return false;
				}

				written = value;
				return true;
			}

			private static Label MakeLabel(string name, float size, FontStyle fontStyle)
			{
				var label = new Label { name = name, pickingMode = PickingMode.Ignore, focusable = false };
				label.style.fontSize = size;
				label.style.unityFontStyleAndWeight = fontStyle;
				label.style.unityTextAlign = TextAnchor.MiddleCenter;
				label.style.whiteSpace = WhiteSpace.NoWrap;
				label.style.marginLeft = label.style.marginRight = label.style.marginTop = label.style.marginBottom = 0f;
				label.style.paddingLeft = label.style.paddingRight = label.style.paddingTop = label.style.paddingBottom = 0f;
				return label;
			}

			private static void SetBorder(IStyle style, float width, float radius)
			{
				style.borderLeftWidth = style.borderRightWidth = style.borderTopWidth = style.borderBottomWidth = width;
				style.borderTopLeftRadius = style.borderTopRightRadius = style.borderBottomLeftRadius = style.borderBottomRightRadius = radius;
			}

			private static void SetBorderColor(IStyle style, Color color)
			{
				style.borderLeftColor = style.borderRightColor = style.borderTopColor = style.borderBottomColor = color;
			}
		}
	}
}
