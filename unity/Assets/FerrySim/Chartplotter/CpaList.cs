using FerrySim.Core.Navigation;
using FerrySim.Core.Traffic;
using FerrySim.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.Chartplotter
{
	/// <summary>
	/// The CPA/TCPA list: the closest targets from a <see cref="TargetRanking"/> (active threats first, by CPA; PAST rows dimmed), one row each with the name (or MMSI),
	/// the CPA in nautical miles, the TCPA as m:ss (PAST once behind) and the true bearing and range. An alarm row is
	/// filled in the alarm colour, and a banner above the rows says CPA ALARM while any target is one; it is steady, never
	/// flashing. Muted, the banner says CPA / TCPA · MUTED in the caution colour and no row is filled, while the rows and
	/// their values stay. A readout under the banner shows the alarm limits in force. Row text comes from bounded string
	/// tables, so updating allocates nothing.
	/// </summary>
	public sealed class CpaList : VisualElement
	{
		public const int DefaultRows = 6;

		public const string BannerText = "CPA / TCPA";
		public const string AlarmText = "CPA ALARM";
		public const string MutedText = "CPA / TCPA · MUTED";

		private enum BannerState
		{
			Normal,
			Alarm,
			Muted,
		}

		private readonly Label _banner;
		private readonly Label _limits;
		private readonly Label _heading;
		private readonly VisualElement[] _rows;
		private readonly Label[] _names;
		private readonly Label[] _cpas;
		private readonly Label[] _tcpas;
		private readonly Label[] _bearings;
		private readonly int[] _rowTargets;
		private readonly string[] _mmsiText;
		private ChartPalette _palette = ChartPalette.Day;
		private ITargetSource _lastSource;
		private long _lastGeneration = long.MinValue;
		private BannerState _bannerState = BannerState.Normal;

		public CpaList(int rows = DefaultRows)
		{
			pickingMode = PickingMode.Ignore;
			style.flexDirection = FlexDirection.Column;
			style.width = 170;
			style.paddingLeft = 6;
			style.paddingRight = 6;
			style.paddingTop = 4;
			style.borderLeftWidth = 1;
			_banner = Caption(BannerText, 11, true);
			_banner.style.height = 18;
			_banner.style.paddingLeft = 4;
			_banner.style.marginBottom = 2;
			Add(_banner);
			_limits = Caption(string.Empty, 10, false, true);
			_limits.style.paddingLeft = 4;
			_limits.style.marginBottom = 1;
			Add(_limits);
			_heading = Caption("TARGET      CPA   TCPA   BRG", 9, false, true);
			_heading.style.marginBottom = 2;
			Add(_heading);
			_rows = new VisualElement[rows];
			_names = new Label[rows];
			_cpas = new Label[rows];
			_tcpas = new Label[rows];
			_bearings = new Label[rows];
			_rowTargets = new int[rows];
			_mmsiText = new string[rows];
			for (var i = 0; i < rows; i++)
			{
				var row = new VisualElement { pickingMode = PickingMode.Ignore };
				row.style.flexDirection = FlexDirection.Column;
				row.style.flexShrink = 0; // Never squashed into clipping its values: the rows are sized to fit.
				row.style.paddingLeft = 4;
				row.style.paddingRight = 4;
				row.style.paddingTop = 2;
				row.style.paddingBottom = 2;
				row.style.marginBottom = 2;
				row.style.borderBottomWidth = 1;
				_names[i] = Caption(string.Empty, 11, true, true);
				row.Add(_names[i]);
				var values = new VisualElement { pickingMode = PickingMode.Ignore };
				values.style.flexDirection = FlexDirection.Row;
				values.style.justifyContent = Justify.SpaceBetween;
				_cpas[i] = Caption(string.Empty, 11, false, true);
				_tcpas[i] = Caption(string.Empty, 11, false, true);
				_bearings[i] = Caption(string.Empty, 11, false, true);
				values.Add(_cpas[i]);
				values.Add(_tcpas[i]);
				values.Add(_bearings[i]);
				row.Add(values);
				row.style.display = DisplayStyle.None;
				Add(row);
				_rows[i] = row;
				_rowTargets[i] = -1;
			}
		}

		public int Rows => _rows.Length;

		/// <summary>Rows shown by the last update.</summary>
		public int Shown { get; private set; }

		/// <summary>True while the banner shows the alarm (an alarm target and the alarm not muted).</summary>
		public bool AlarmShown => _bannerState == BannerState.Alarm;

		/// <summary>True while the banner says the alarm is muted.</summary>
		public bool MutedShown => _bannerState == BannerState.Muted;

		public string BannerShown => _banner.text;

		/// <summary>The alarm limits readout under the banner.</summary>
		public string LimitsText => _limits.text;

		/// <summary>The banner's and the readout's text colours as set (the palette's roles).</summary>
		public Color BannerColour => _banner.style.color.value;

		public Color LimitsColour => _limits.style.color.value;

		/// <summary>True when the row is filled in the alarm colour.</summary>
		public bool RowFilled(int row) => _rows[row].style.backgroundColor == _palette.TargetAlarm;

		public string RowName(int row) => _names[row].text;

		public string RowCpa(int row) => _cpas[row].text;

		public string RowTcpa(int row) => _tcpas[row].text;

		public string RowBearing(int row) => _bearings[row].text;

		public int RowTarget(int row) => _rowTargets[row];

		public bool RowVisible(int row) => _rows[row].style.display == DisplayStyle.Flex;

		/// <summary>True when the row's text is dimmed (a target past its closest point).</summary>
		public bool RowDimmed(int row) => _names[row].style.color == _palette.TextDim;

		private static Label Caption(string text, int size, bool bold, bool compact = false)
		{
			var label = new Label(text) { pickingMode = PickingMode.Ignore };
			label.AddToClassList("fs-label");
			label.style.fontSize = size;
			label.style.unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal;
			if (compact)
			{
				// No vertical padding or margin: six rows, the banner, the limits readout and the heading fit the 358 px body at the shipping size.
				label.style.paddingTop = 0;
				label.style.paddingBottom = 0;
				label.style.marginTop = 0;
				label.style.marginBottom = 0;
			}

			return label;
		}

		public void SetPalette(ChartPalette palette)
		{
			_palette = palette ?? ChartPalette.Day;
			style.backgroundColor = _palette.Panel;
			style.borderLeftColor = _palette.Bezel;
			_heading.style.color = _palette.TextDim;
			_limits.style.color = _palette.TextDim;
			for (var i = 0; i < _rows.Length; i++)
			{
				_rows[i].style.borderBottomColor = _palette.Bezel;
			}

			ApplyBanner(_bannerState, true); // The banner colours come from the new palette.
		}

		/// <summary>Sets the alarm limits readout (a string built by the view on a change, never per frame).</summary>
		public void SetLimitsText(string text) => PaletteRoles.SetText(_limits, text);

		private void ApplyBanner(BannerState state, bool force)
		{
			if (state == _bannerState && !force)
			{
				return;
			}

			_bannerState = state;
			PaletteRoles.SetText(_banner, state == BannerState.Alarm ? AlarmText : state == BannerState.Muted ? MutedText : BannerText);
			_banner.style.backgroundColor = state == BannerState.Alarm ? _palette.TargetAlarm : _palette.Panel;
			_banner.style.color = state == BannerState.Alarm ? _palette.TargetAlarmText : state == BannerState.Muted ? _palette.Instruments.Caution : _palette.Text;
		}

		/// <summary>Fills the rows from a ranking; names come from the source as existing strings. Muted, no alarm treatment is drawn.</summary>
		public void Update(TargetRanking ranking, ITargetSource source, bool muted = false)
		{
			ApplyBanner(muted ? BannerState.Muted : ranking != null && ranking.Alarm ? BannerState.Alarm : BannerState.Normal, false);
			var generation = source != null ? source.Generation : long.MinValue;
			if (!ReferenceEquals(source, _lastSource) || generation != _lastGeneration)
			{
				// Another source or another roster of the same source: no row's MMSI text may survive it.
				_lastSource = source;
				_lastGeneration = generation;
				for (var i = 0; i < _mmsiText.Length; i++)
				{
					_mmsiText[i] = null;
					_rowTargets[i] = -1;
				}
			}

			var count = ranking == null ? 0 : System.Math.Min(ranking.Count, _rows.Length);
			Shown = count;
			for (var i = 0; i < _rows.Length; i++)
			{
				if (i >= count)
				{
					if (_rows[i].style.display != DisplayStyle.None)
					{
						_rows[i].style.display = DisplayStyle.None;
					}

					continue;
				}

				var target = ranking.Target(i);
				var encounter = ranking.Encounter(i);
				var alarm = ranking.IsAlarm(i) && !muted;
				var name = source != null ? source.Name(target) : string.Empty;
				if (string.IsNullOrEmpty(name))
				{
					// An MMSI string per row, remade only when the row's target changes.
					if (_rowTargets[i] != target || _mmsiText[i] == null)
					{
						_mmsiText[i] = source != null ? source.Mmsi(target).ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
					}

					name = _mmsiText[i];
				}

				_rowTargets[i] = target;
				PaletteRoles.SetText(_names[i], name);
				PaletteRoles.SetText(_cpas[i], PlotterStrings.MilesHundredths(encounter.Cpa));
				PaletteRoles.SetText(_tcpas[i], PlotterStrings.MinutesAndSeconds(encounter.Tcpa));
				PaletteRoles.SetText(_bearings[i], InstrumentFormat.Compass(encounter.Bearing));
				var past = ranking.IsPast(i);
				var background = alarm ? _palette.TargetAlarm : _palette.Panel;
				// A target past its closest point reads dimmed: it is listed, not watched.
				var text = alarm ? _palette.TargetAlarmText : past ? _palette.TextDim : _palette.Text;
				var dim = alarm ? _palette.TargetAlarmText : _palette.TextDim;
				_rows[i].style.backgroundColor = background;
				_names[i].style.color = text;
				_cpas[i].style.color = text;
				_tcpas[i].style.color = text;
				_bearings[i].style.color = dim;
				if (_rows[i].style.display != DisplayStyle.Flex)
				{
					_rows[i].style.display = DisplayStyle.Flex;
				}
			}
		}
	}
}
