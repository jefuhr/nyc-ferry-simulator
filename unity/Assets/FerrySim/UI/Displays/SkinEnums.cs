namespace FerrySim.UI.Displays
{
	/// <summary>Which engine a per-engine display shows. <c>{side}</c> in a skin becomes <c>port</c> or <c>starboard</c>.</summary>
	public enum DisplaySide
	{
		/// <summary>A display that belongs to no engine, such as the alarm panel. A skin that uses <c>{side}</c> needs a side.</summary>
		None = 0,
		Port = 1,
		Starboard = 2,
	}

	/// <summary>The element types of contract C6, in schema order (<c>list</c> was appended).</summary>
	public enum SkinElementType
	{
		Dial,
		Bar,
		Value,
		Lamp,
		Text,
		Softkey,
		Frame,

		/// <summary>Every alarm the readout defines, grouped by alarm group.</summary>
		List,
	}

	/// <summary>How a <c>value</c> element shows its channel.</summary>
	public enum SkinValueFormat
	{
		/// <summary>A number in the element's unit, to its decimals.</summary>
		Number,

		/// <summary>A shift-phase channel as NEUTRAL, AHEAD, ASTERN or SHIFTING.</summary>
		Gear,
	}

	/// <summary>The alarm action a softkey raises, when it has one instead of a page target.</summary>
	public enum SoftkeyAction
	{
		None,
		Acknowledge,
		Silence,

		/// <summary>Held while the key is pressed.</summary>
		LampTest,
	}
}
