using System;
using System.Collections.Generic;
using FerrySim.Controls;
using UnityEngine.InputSystem;

namespace FerrySim.Prompts.Tests.EditMode
{
	/// <summary>An <see cref="IBindingService"/> whose display strings the test sets. Counts lookups so tests can tell a rebuild from a frame.</summary>
	internal sealed class FakeBindings : IBindingService
	{
		private readonly Dictionary<string, string> _keys = new Dictionary<string, string>();

		public event Action BindingsChanged;

		public int DisplayStringCalls { get; private set; }

		public List<InputActionAsset> Registered { get; } = new List<InputActionAsset>();

		public int Subscribers => BindingsChanged?.GetInvocationList().Length ?? 0;

		/// <summary>Sets what <see cref="DisplayString"/> returns; null or empty unbinds. Raises <see cref="BindingsChanged"/> when asked.</summary>
		public FakeBindings Set(string actionId, string scheme, string text, bool raise = false)
		{
			_keys[actionId + "|" + scheme] = text ?? string.Empty;
			if (raise)
			{
				BindingsChanged?.Invoke();
			}

			return this;
		}

		public void Raise() => BindingsChanged?.Invoke();

		public void Register(InputActionAsset instance) => Registered.Add(instance);

		public IReadOnlyList<BindingRow> Rows { get; } = Array.Empty<BindingRow>();

		public string DisplayString(string actionId, string scheme)
		{
			DisplayStringCalls++;
			return _keys.TryGetValue(actionId + "|" + scheme, out var text) ? text : string.Empty;
		}

		public bool IsRebinding => false;

		public void BeginRebind(string actionId, int slot, string scheme, Action<RebindOutcome> completed) => completed?.Invoke(new RebindOutcome(RebindResult.Cancelled, null));

		public void CancelRebind()
		{
		}

		public bool TryApply(string actionId, int slot, string scheme, string path, out string conflictActionId)
		{
			conflictActionId = null;
			return false;
		}

		public void Reset(string actionId)
		{
		}

		public void ResetAll()
		{
		}

		public bool TryReset(string actionId, out string conflictActionId)
		{
			conflictActionId = null;
			return true;
		}
	}
}
