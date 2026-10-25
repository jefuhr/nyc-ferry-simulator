using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// The water and the air in three groups: the tide (level, datum and trend), the current's set and drift, and the
	/// wind and where it comes from. The panel carries one in a row of its top strip, the HUD one in two rows; both
	/// show the same <see cref="EnvironmentDisplay"/>. Each group has a fixed width and its values sit in fixed
	/// right-aligned boxes, so nothing shifts as digits change, the trend changes or the wind goes missing. A run
	/// without tide (calm or uniform) says CALM or UNIFORM where the trend and the datum would be.
	/// </summary>
	public sealed class EnvironmentReadouts : VisualElement
	{
		public const string WindFromCaption = "WIND FROM";
		public const string WindCaptionMissing = "WIND";

		private bool _sourceShown;
		private bool _trendShown;
		private bool _windShownMissing;

		/// <param name="prefix">Element names start with it: "env" on the panel, "hud-env" on the HUD.</param>
		public EnvironmentReadouts(string prefix)
		{
			name = prefix;
			AddToClassList("fs-env");
			pickingMode = PickingMode.Ignore;

			var tide = Group("fs-env-group--tide");
			tide.Add(PaletteRoles.Label("TIDE", PaletteRoles.Dim, "fs-caption", "fs-env-caption"));
			Tide = Value(prefix + "-tide", "fs-env-value--tide");
			tide.Add(Tide);
			TideUnit = PaletteRoles.Label(DisplayUnits.Symbol(DistanceUnit.Feet), PaletteRoles.Dim, "fs-unit", "fs-unit--spaced", "fs-env-unit");
			TideUnit.name = prefix + "-tide-unit";
			tide.Add(TideUnit);
			// The datum reads with the height ("3.0 ft NAVD88"), then the trend.
			TideDatum = PaletteRoles.Label(EnvironmentFormat.Datum, PaletteRoles.Dim, "fs-caption", "fs-env-datum");
			TideDatum.name = prefix + "-tide-datum";
			tide.Add(TideDatum);
			TideArrow = new TrendArrow { name = prefix + "-tide-arrow" };
			tide.Add(TideArrow);
			TideTrend = PaletteRoles.Label(string.Empty, PaletteRoles.Dim, "fs-caption", "fs-env-trend");
			TideTrend.name = prefix + "-tide-trend";
			TideTrend.style.display = DisplayStyle.None;
			tide.Add(TideTrend);
			Source = PaletteRoles.Label(string.Empty, PaletteRoles.Dim, "fs-caption", "fs-env-source");
			Source.name = prefix + "-source";
			Source.style.display = DisplayStyle.None;
			tide.Add(Source);
			Add(tide);

			var current = Group("fs-env-group--current");
			current.Add(PaletteRoles.Label("SET", PaletteRoles.Dim, "fs-caption", "fs-env-caption"));
			Set = Value(prefix + "-set", "fs-env-value--bearing");
			current.Add(Set);
			current.Add(PaletteRoles.Label("°T", PaletteRoles.Dim, "fs-unit", "fs-unit--tight", "fs-env-unit"));
			current.Add(PaletteRoles.Label("DRIFT", PaletteRoles.Dim, "fs-caption", "fs-env-caption", "fs-env-caption--inner"));
			Drift = Value(prefix + "-drift", "fs-env-value--speed");
			current.Add(Drift);
			current.Add(PaletteRoles.Label("kn", PaletteRoles.Dim, "fs-unit", "fs-unit--spaced", "fs-env-unit"));
			Add(current);

			var wind = Group("fs-env-group--wind");
			WindCaption = PaletteRoles.Label(WindFromCaption, PaletteRoles.Dim, "fs-caption", "fs-env-caption");
			WindCaption.name = prefix + "-wind-caption";
			wind.Add(WindCaption);
			WindFrom = Value(prefix + "-wind-from", "fs-env-value--bearing");
			wind.Add(WindFrom);
			WindFromUnit = PaletteRoles.Label("°T", PaletteRoles.Dim, "fs-unit", "fs-unit--tight", "fs-env-unit");
			WindFromUnit.name = prefix + "-wind-from-unit";
			wind.Add(WindFromUnit);
			WindSpeed = Value(prefix + "-wind-speed", "fs-env-value--wind");
			WindSpeed.AddToClassList("fs-env-value--inner");
			wind.Add(WindSpeed);
			WindSpeedUnit = PaletteRoles.Label("kn", PaletteRoles.Dim, "fs-unit", "fs-unit--spaced", "fs-env-unit");
			WindSpeedUnit.name = prefix + "-wind-speed-unit";
			wind.Add(WindSpeedUnit);
			Add(wind);
		}

		/// <summary>"CALM" or "UNIFORM", in place of the trend and the datum, for runs without a pack day; hidden otherwise.</summary>
		public Label Source { get; }

		public Label Tide { get; }

		public Label TideUnit { get; }

		public TrendArrow TideArrow { get; }

		public Label TideTrend { get; }

		public Label TideDatum { get; }

		public Label Set { get; }

		public Label Drift { get; }

		/// <summary>"WIND FROM", or "WIND" before "NO WIND DATA".</summary>
		public Label WindCaption { get; }

		public Label WindFrom { get; }

		public Label WindFromUnit { get; }

		public Label WindSpeed { get; }

		public Label WindSpeedUnit { get; }

		/// <summary>Shows <paramref name="display"/>. Text and visibility change only when they differ, so an unchanged frame costs nothing.</summary>
		public void Show(EnvironmentDisplay display)
		{
			PaletteRoles.SetText(Source, display.Source);
			var sourceShown = display.Source.Length > 0;
			if (sourceShown != _sourceShown)
			{
				_sourceShown = sourceShown;
				Source.style.display = sourceShown ? DisplayStyle.Flex : DisplayStyle.None;
				TideDatum.style.display = sourceShown ? DisplayStyle.None : DisplayStyle.Flex;
			}

			PaletteRoles.SetText(Tide, display.Tide);
			PaletteRoles.SetText(TideUnit, display.TideUnit);
			TideArrow.Show(display.Trend);
			var trend = EnvironmentFormat.TrendText(display.Trend);
			PaletteRoles.SetText(TideTrend, trend);
			var trendShown = trend.Length > 0;
			if (trendShown != _trendShown)
			{
				_trendShown = trendShown;
				TideTrend.style.display = trendShown ? DisplayStyle.Flex : DisplayStyle.None;
			}

			PaletteRoles.SetText(Set, display.Set);
			PaletteRoles.SetText(Drift, display.Drift);
			PaletteRoles.SetText(WindFrom, display.WindFrom);
			PaletteRoles.SetText(WindSpeed, display.WindSpeed);
			if (display.WindMissing != _windShownMissing)
			{
				_windShownMissing = display.WindMissing;
				var numbers = _windShownMissing ? DisplayStyle.None : DisplayStyle.Flex;
				PaletteRoles.SetText(WindCaption, _windShownMissing ? WindCaptionMissing : WindFromCaption);
				WindFromUnit.style.display = numbers;
				WindSpeed.style.display = numbers;
				WindSpeedUnit.style.display = numbers;
				WindFrom.EnableInClassList("fs-env-value--message", _windShownMissing);
			}
		}

		private static Label Value(string valueName, string widthClass)
		{
			var value = PaletteRoles.Label(InstrumentFormat.NoValue, PaletteRoles.Text, "fs-value-s", "fs-env-value", widthClass);
			value.name = valueName;
			return value;
		}

		private static VisualElement Group(string className)
		{
			var group = new VisualElement { pickingMode = PickingMode.Ignore };
			group.AddToClassList("fs-env-group");
			group.AddToClassList(className);
			return group;
		}
	}

	/// <summary>
	/// A coverage warning: filled caution while the environment is making a value up now, outlined once it has
	/// stopped (earlier in this run), hidden when it never happened. It carries its own fill, so it stays readable
	/// on the console, an instrument face or a flashing alarm. Never flashes: it is a caveat, not an alarm.
	/// </summary>
	public sealed class CoverageTag : Label, IPaletteAware
	{
		private InstrumentPalette _palette = InstrumentPalette.Day;

		public CoverageTag(string text)
		{
			this.text = text;
			pickingMode = PickingMode.Ignore;
			AddToClassList("fs-label");
			AddToClassList("fs-caption");
			AddToClassList("fs-coverage-tag");
			Restyle();
		}

		public CoverageIndication Indication { get; private set; }

		public void Show(CoverageIndication indication)
		{
			if (indication == Indication)
			{
				return;
			}

			Indication = indication;
			Restyle();
		}

		public void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette ?? InstrumentPalette.Day;
			Restyle();
		}

		private void Restyle()
		{
			var live = Indication == CoverageIndication.Live;
			style.display = Indication == CoverageIndication.None ? DisplayStyle.None : DisplayStyle.Flex;
			style.backgroundColor = live ? _palette.Caution : _palette.Face;
			style.color = live ? _palette.CautionText : _palette.Text;
			PaletteRoles.SetBorderColor(this, _palette.Caution);
		}
	}
}
