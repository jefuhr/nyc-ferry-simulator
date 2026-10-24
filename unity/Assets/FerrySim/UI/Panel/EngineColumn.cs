using FerrySim.Core.Contracts;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// Everything about one engine in one place, so command and response can be compared at a glance:
	/// the lever command, the gear the clutch is actually in, engine rpm and which way the shaft turns.
	/// The port column has the tachometer outboard on the left; the starboard column mirrors it, so both
	/// levers sit toward the middle like the twin control head.
	/// </summary>
	public sealed class EngineColumn : VisualElement
	{
		private readonly string _prefix;

		public EngineColumn(EngineSide side)
		{
			Side = side;
			_prefix = side == EngineSide.Port ? "port" : "stbd";
			name = _prefix + "-engine";
			AddToClassList("fs-engine");
			AddToClassList(side == EngineSide.Port ? "fs-engine--port" : "fs-engine--stbd");
			AddToClassList(PaletteRoles.FaceBackground);
			pickingMode = PickingMode.Ignore;

			var header = Row("fs-engine-header");
			header.Add(new SideAccent(side));
			header.Add(PaletteRoles.Label(side == EngineSide.Port ? "PORT ENGINE" : "STBD ENGINE", PaletteRoles.Text, "fs-caption", "fs-strong"));
			Add(header);

			var body = Row("fs-engine-body");
			Tachometer = new Tachometer { name = _prefix + "-tach" };
			body.Add(Tachometer);
			var leverGroup = new VisualElement { pickingMode = PickingMode.Ignore };
			leverGroup.AddToClassList("fs-lever-group");
			leverGroup.Add(PaletteRoles.Label("LEVER", PaletteRoles.Dim, "fs-caption"));
			Lever = new LeverIndicator { name = _prefix + "-lever" };
			leverGroup.Add(Lever);
			LeverPercent = PaletteRoles.Label(InstrumentFormat.NoValue, PaletteRoles.Text, "fs-value-s");
			LeverPercent.name = _prefix + "-lever-percent";
			leverGroup.Add(LeverPercent);
			LeverDirection = PaletteRoles.Label(InstrumentFormat.NoValue, PaletteRoles.Dim, "fs-caption");
			LeverDirection.name = _prefix + "-lever-direction";
			leverGroup.Add(LeverDirection);
			body.Add(leverGroup);
			Add(body);

			var footer = Row("fs-engine-footer");
			Gear = new GearBadge { name = _prefix + "-gear" };
			footer.Add(Gear);
			var shaft = Row("fs-shaft");
			shaft.Add(PaletteRoles.Label("SHAFT", PaletteRoles.Dim, "fs-caption"));
			ShaftArrow = new ShaftArrow { name = _prefix + "-shaft-arrow" };
			shaft.Add(ShaftArrow);
			ShaftRpm = PaletteRoles.Label(InstrumentFormat.NoValue, PaletteRoles.Text, "fs-value-s", "fs-shaft-value");
			ShaftRpm.name = _prefix + "-shaft-rpm";
			shaft.Add(ShaftRpm);
			shaft.Add(PaletteRoles.Label("rpm", PaletteRoles.Dim, "fs-unit"));
			footer.Add(shaft);
			Add(footer);
		}

		public EngineSide Side { get; }

		public Tachometer Tachometer { get; }

		public LeverIndicator Lever { get; }

		public Label LeverPercent { get; }

		public Label LeverDirection { get; }

		public GearBadge Gear { get; }

		public ShaftArrow ShaftArrow { get; }

		public Label ShaftRpm { get; }

		public void SetScales(InstrumentScales scales)
		{
			Tachometer.SetScales(scales);
			Lever.SetClutchIn(scales.ClutchInLever);
		}

		public void Show(EngineTelemetry engine, double lever)
		{
			Tachometer.SetEngineSpeed(engine.EngineSpeed);
			var gear = GearReadout.From(engine);
			Gear.Show(gear);
			ShaftArrow.Show(gear.Shaft);
			PaletteRoles.SetText(ShaftRpm, gear.ShaftRpm);
			Lever.SetLever(lever);
			PaletteRoles.SetText(LeverPercent, InstrumentFormat.LeverPercent(lever));
			PaletteRoles.SetText(LeverDirection, InstrumentFormat.LeverDirection(lever));
		}

		public void ShowNoData()
		{
			Tachometer.SetEngineSpeed(double.NaN);
			Gear.Show(InstrumentFormat.NoValue, GearTone.Unknown);
			ShaftArrow.Show(ShaftRotation.Unknown);
			PaletteRoles.SetText(ShaftRpm, InstrumentFormat.NoValue);
			Lever.SetLever(double.NaN);
			PaletteRoles.SetText(LeverPercent, InstrumentFormat.NoValue);
			PaletteRoles.SetText(LeverDirection, InstrumentFormat.NoValue);
		}

		private static VisualElement Row(string className)
		{
			var row = new VisualElement { pickingMode = PickingMode.Ignore };
			row.AddToClassList(className);
			return row;
		}
	}

	/// <summary>A small block in the side's color (red port, green starboard by day) next to a column title.</summary>
	public sealed class SideAccent : VisualElement, IPaletteAware
	{
		private readonly EngineSide _side;

		public SideAccent(EngineSide side)
		{
			_side = side;
			pickingMode = PickingMode.Ignore;
			AddToClassList("fs-accent");
			ApplyPalette(InstrumentPalette.Day);
		}

		public void ApplyPalette(InstrumentPalette palette) =>
			style.backgroundColor = _side == EngineSide.Port ? palette.Port : palette.Starboard;
	}
}
