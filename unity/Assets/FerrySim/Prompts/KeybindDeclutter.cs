using UnityEngine;

namespace FerrySim.Prompts
{
	/// <summary>
	/// Chooses where each popup plate goes, in panel pixels (y down), so no two shown plates overlap.
	/// <para>
	/// Each item has an anchor (its control's projection), a plate size and a priority (its distance from the
	/// view centre, 0 at the centre). Items are placed in priority order. The first choice, "home", centres the
	/// plate a leader's length above the anchor (below it when there is no room above). An item whose home is
	/// taken, or that would cover the pivot dot of an item placed before it, tries a few spots nearby, one plate's
	/// width or height away, and is dropped if all are taken. At most
	/// <c>maxShown</c> items are shown.
	/// </para>
	/// <para>
	/// Hysteresis keeps a still or slowly turning view steady: an item shown last time ranks as if it were nearer
	/// the centre (more so at home), so two items swap places, or a newcomer displaces one at the cap, only when the
	/// order changes clearly; and a spot an item held last time only needs to be free, while any other spot needs a
	/// clear gap, so an item at the edge of a collision does not flip between spots. Allocation-free after
	/// construction.
	/// </para>
	/// </summary>
	internal sealed class KeybindDeclutter
	{
		/// <summary>Clear space, in panel pixels, needed to move into a spot an item did not hold last time.</summary>
		public const float Gap = 4f;

		/// <summary>Rank bonus (fraction of the half diagonal) for having been shown, and again for having been at home.</summary>
		public const float PriorityHysteresis = 0.03f;

		/// <summary>Half the size of the square around a placed item's pivot (its dot) that later plates keep clear of.</summary>
		public const float PivotRadius = 4.5f;

		/// <summary>Home, then nearby spots in plate widths (x) and heights (y), y up the screen being negative.</summary>
		private static readonly Vector2[] Offsets =
		{
			new Vector2(0f, 0f),
			new Vector2(0f, -1f),
			new Vector2(-1f, 0f),
			new Vector2(1f, 0f),
			new Vector2(-1f, -1f),
			new Vector2(1f, -1f),
			new Vector2(0f, -2f),
		};

		private readonly Vector2[] _anchor;
		private readonly Vector2[] _size;
		private readonly float[] _priority;
		private readonly float[] _rank;
		private readonly bool[] _candidate;
		private readonly bool[] _shown;
		private readonly bool[] _wasShown;
		private readonly int[] _slot;
		private readonly int[] _wasSlot;
		private readonly Rect[] _placed;
		private readonly int[] _order;
		private readonly int[] _placedItems;
		private int _placedCount;

		public KeybindDeclutter(int capacity)
		{
			_anchor = new Vector2[capacity];
			_size = new Vector2[capacity];
			_priority = new float[capacity];
			_rank = new float[capacity];
			_candidate = new bool[capacity];
			_shown = new bool[capacity];
			_wasShown = new bool[capacity];
			_slot = new int[capacity];
			_wasSlot = new int[capacity];
			_placed = new Rect[capacity];
			_order = new int[capacity];
			_placedItems = new int[capacity];
			Reset();
		}

		public int Capacity => _anchor.Length;

		/// <summary>Forgets every item and the previous solve (a new set of controls).</summary>
		public void Reset()
		{
			for (var i = 0; i < _anchor.Length; i++)
			{
				_candidate[i] = false;
				_shown[i] = false;
				_slot[i] = -1;
				_placed[i] = default;
			}
		}

		/// <summary>Takes an item out of this solve (hidden: behind, off screen, facing away or unbound).</summary>
		public void Clear(int index) => _candidate[index] = false;

		/// <summary>Puts an item into this solve. The item stays a candidate until <see cref="Clear"/>.</summary>
		public void Set(int index, Vector2 anchor, Vector2 size, float priority)
		{
			_candidate[index] = true;
			_anchor[index] = anchor;
			_size[index] = size;
			_priority[index] = priority;
		}

		/// <summary>Places items 0..count-1 on a screen of the given size. Returns how many are shown.</summary>
		public int Solve(int count, Vector2 screen, float leader, int maxShown, float margin)
		{
			var candidates = 0;
			for (var i = 0; i < count; i++)
			{
				_wasShown[i] = _shown[i];
				_wasSlot[i] = _shown[i] ? _slot[i] : -1;
				_shown[i] = false;
				_slot[i] = -1;
				if (!_candidate[i])
				{
					continue;
				}

				var rank = _priority[i];
				if (_wasShown[i])
				{
					rank -= PriorityHysteresis;
					if (_wasSlot[i] == 0)
					{
						rank -= PriorityHysteresis;
					}
				}

				_rank[i] = rank;
				// Insertion sort by rank, ties by index, so the order is deterministic.
				var at = candidates++;
				while (at > 0 && (_rank[_order[at - 1]] > rank || (_rank[_order[at - 1]] == rank && _order[at - 1] > i)))
				{
					_order[at] = _order[at - 1];
					at--;
				}

				_order[at] = i;
			}

			_placedCount = 0;
			for (var k = 0; k < candidates && _placedCount < maxShown; k++)
			{
				var i = _order[k];
				var previous = _wasSlot[i];
				var chosen = -1;
				Rect rect = default;
				if (TryPlace(i, 0, previous, screen, leader, margin, out rect))
				{
					chosen = 0;
				}
				else if (previous > 0 && TryPlace(i, previous, previous, screen, leader, margin, out rect))
				{
					chosen = previous;
				}
				else
				{
					for (var s = 1; s < Offsets.Length; s++)
					{
						if (s != previous && TryPlace(i, s, previous, screen, leader, margin, out rect))
						{
							chosen = s;
							break;
						}
					}
				}

				if (chosen < 0)
				{
					continue;
				}

				_shown[i] = true;
				_slot[i] = chosen;
				_placed[i] = rect;
				_placedItems[_placedCount++] = i;
			}

			return _placedCount;
		}

		public bool Shown(int index) => _shown[index];

		/// <summary>Which spot the item took: 0 home, 1.. nudged, -1 not shown.</summary>
		public int Slot(int index) => _slot[index];

		public Rect Placed(int index) => _placed[index];

		private bool TryPlace(int i, int slot, int previous, Vector2 screen, float leader, float margin, out Rect rect)
		{
			rect = RectFor(i, slot, screen, leader, margin);
			var clearance = slot == previous ? 0f : Gap;
			for (var p = 0; p < _placedCount; p++)
			{
				var placed = _placedItems[p];
				var other = _placed[placed];
				if (rect.xMin < other.xMax + clearance && other.xMin < rect.xMax + clearance &&
					rect.yMin < other.yMax + clearance && other.yMin < rect.yMax + clearance)
				{
					return false;
				}

				// Nor on the pivot dot of a plate placed before it, so every leader ends on a visible dot.
				var pivot = _anchor[placed];
				if (rect.xMin < pivot.x + PivotRadius + clearance && pivot.x - PivotRadius < rect.xMax + clearance &&
					rect.yMin < pivot.y + PivotRadius + clearance && pivot.y - PivotRadius < rect.yMax + clearance)
				{
					return false;
				}
			}

			return true;
		}

		private Rect RectFor(int i, int slot, Vector2 screen, float leader, float margin)
		{
			var size = _size[i];
			var anchor = _anchor[i];
			var offset = Offsets[slot];
			var x = anchor.x - size.x * 0.5f + offset.x * (size.x + Gap);
			float y;
			if (anchor.y - leader - size.y >= margin)
			{
				y = anchor.y - leader - size.y + offset.y * (size.y + Gap);
			}
			else
			{
				// No room above the control: hang the plate below it, nudging further down.
				y = anchor.y + leader - offset.y * (size.y + Gap);
			}

			x = Mathf.Max(margin, Mathf.Min(x, screen.x - margin - size.x));
			y = Mathf.Max(margin, Mathf.Min(y, screen.y - margin - size.y));
			return new Rect(x, y, size.x, size.y);
		}
	}
}
