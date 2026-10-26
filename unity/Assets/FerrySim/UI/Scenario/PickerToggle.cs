using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>A single-tab-stop on/off field with the picker's shared keyboard and palette behavior.</summary>
	public sealed class PickerToggle : PickerField
	{
		private readonly Button _button;
		private readonly string _caption;
		private bool _value;

		public PickerToggle(string name, string caption) : base(name)
		{
			_caption = caption;
			_button = new Button(() => { Value = !Value; Focus(); }) { focusable = false };
			_button.AddToClassList("fs-option");
			_button.style.whiteSpace = WhiteSpace.Normal;
			_button.style.height = 42;
			Add(_button);
			Restyle();
		}

		public event Action ValueChanged;
		public bool Value
		{
			get => _value;
			set { if (_value == value) return; SetWithoutNotify(value); ValueChanged?.Invoke(); }
		}
		public void SetWithoutNotify(bool value) { _value = value; Restyle(); }
		protected override bool HandleKey(KeyCode key)
		{
			if (key != KeyCode.UpArrow && key != KeyCode.DownArrow && key != KeyCode.Space) return false;
			Value = key == KeyCode.Space ? !Value : key == KeyCode.UpArrow;
			return true;
		}
		protected override void Restyle()
		{
			_button.text = (_value ? "ON · " : "OFF · ") + _caption;
			StyleChoice(_button, _value);
		}
	}
}
