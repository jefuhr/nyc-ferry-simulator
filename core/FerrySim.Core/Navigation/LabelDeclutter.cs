using System;

namespace FerrySim.Core.Navigation
{
	/// <summary>
	/// Greedy label placement without overlaps: labels are offered in the caller's priority order and each is kept only
	/// when its box, grown by a spacing, touches no label kept before it. Deterministic for the same offers in the same
	/// order, and allocation-free after construction: kept boxes go into fixed arrays and a fixed grid of buckets over
	/// the area given to <see cref="Begin"/>. Offers outside that area, or larger than the declared largest label, are refused.
	/// </summary>
	public sealed class LabelDeclutter
	{
		private const int MaximumGrid = 48;
		private const int EntriesPerLabel = 4;

		private readonly double[] _minX;
		private readonly double[] _minY;
		private readonly double[] _maxX;
		private readonly double[] _maxY;
		private readonly int[] _entryLabel;
		private readonly int[] _entryNext;
		private readonly int[] _head = new int[MaximumGrid * MaximumGrid];
		private int _entries;
		private int _columns = 1, _rows = 1;
		private double _left, _top, _cellWidth = 1.0, _cellHeight = 1.0, _width = 1.0, _height = 1.0, _maxLabelWidth, _maxLabelHeight;

		public LabelDeclutter(int capacity)
		{
			if (capacity <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(capacity));
			}

			_minX = new double[capacity];
			_minY = new double[capacity];
			_maxX = new double[capacity];
			_maxY = new double[capacity];
			_entryLabel = new int[capacity * EntriesPerLabel];
			_entryNext = new int[capacity * EntriesPerLabel];
			Begin(0.0, 0.0, 1.0, 1.0, 0.0, 1.0, 1.0);
		}

		/// <summary>Labels this instance can keep.</summary>
		public int Capacity => _minX.Length;

		/// <summary>Labels kept since <see cref="Begin"/>.</summary>
		public int Count { get; private set; }

		/// <summary>Offers refused since <see cref="Begin"/>: overlapping, outside, too large or beyond the capacity.</summary>
		public int Refused { get; private set; }

		/// <summary>The gap kept around every label, px.</summary>
		public double Spacing { get; private set; }

		public double MinX(int label) => _minX[Check(label)];

		public double MinY(int label) => _minY[Check(label)];

		public double MaxX(int label) => _maxX[Check(label)];

		public double MaxY(int label) => _maxY[Check(label)];

		/// <summary>
		/// Clears the placement for an area (px), a spacing (px) and the largest label that will be offered (px, before
		/// the spacing). Cells are at least a grown label wide, so a kept label registers in at most four cells.
		/// </summary>
		public void Begin(double left, double top, double width, double height, double spacing, double maxLabelWidth, double maxLabelHeight)
		{
			if (!(width > 0.0) || !(height > 0.0) || !(spacing >= 0.0) || !(maxLabelWidth > 0.0) || !(maxLabelHeight > 0.0))
			{
				throw new ArgumentException("A declutter area needs positive sides, a non-negative spacing and a positive largest label.");
			}

			_left = left;
			_top = top;
			_width = width;
			_height = height;
			Spacing = spacing;
			_maxLabelWidth = maxLabelWidth;
			_maxLabelHeight = maxLabelHeight;
			var grownWidth = maxLabelWidth + 2.0 * spacing;
			var grownHeight = maxLabelHeight + 2.0 * spacing;
			_columns = Math.Max(1, Math.Min(MaximumGrid, (int)Math.Floor(width / grownWidth)));
			_rows = Math.Max(1, Math.Min(MaximumGrid, (int)Math.Floor(height / grownHeight)));
			_cellWidth = width / _columns;
			_cellHeight = height / _rows;
			Count = 0;
			Refused = 0;
			_entries = 0;
			for (var i = 0; i < _head.Length; i++)
			{
				_head[i] = -1;
			}
		}

		/// <summary>
		/// Keeps a label box (centre and size, px) when it fits: inside the area, no larger than the declared largest
		/// label, and clear of every kept box by the spacing.
		/// </summary>
		public bool TryPlace(double centreX, double centreY, double width, double height)
		{
			var minX = centreX - width * 0.5;
			var minY = centreY - height * 0.5;
			var maxX = centreX + width * 0.5;
			var maxY = centreY + height * 0.5;
			if (Count >= Capacity || !(width > 0.0) || !(height > 0.0) || width > _maxLabelWidth + 1e-9 || height > _maxLabelHeight + 1e-9 ||
				!(minX >= _left) || !(minY >= _top) || !(maxX <= _left + _width) || !(maxY <= _top + _height))
			{
				Refused++;
				return false;
			}

			// Grown by the spacing: two labels overlap when their grown boxes touch, so the gap between them is at least the spacing.
			var gMinX = minX - Spacing;
			var gMinY = minY - Spacing;
			var gMaxX = maxX + Spacing;
			var gMaxY = maxY + Spacing;
			Cells(gMinX, gMinY, gMaxX, gMaxY, out var c0, out var r0, out var c1, out var r1);
			for (var r = r0; r <= r1; r++)
			{
				for (var c = c0; c <= c1; c++)
				{
					for (var e = _head[r * MaximumGrid + c]; e >= 0; e = _entryNext[e])
					{
						var i = _entryLabel[e];
						if (_maxX[i] > gMinX && _minX[i] < gMaxX && _maxY[i] > gMinY && _minY[i] < gMaxY)
						{
							Refused++;
							return false;
						}
					}
				}
			}

			var index = Count;
			_minX[index] = minX;
			_minY[index] = minY;
			_maxX[index] = maxX;
			_maxY[index] = maxY;
			// Register the grown box in every cell it covers (at most 2 by 2, since a cell is at least a grown label).
			Cells(gMinX, gMinY, gMaxX, gMaxY, out var kc0, out var kr0, out var kc1, out var kr1);
			kc1 = Math.Min(kc1, kc0 + 1);
			kr1 = Math.Min(kr1, kr0 + 1);
			for (var r = kr0; r <= kr1; r++)
			{
				for (var c = kc0; c <= kc1; c++)
				{
					var cell = r * MaximumGrid + c;
					_entryLabel[_entries] = index;
					_entryNext[_entries] = _head[cell];
					_head[cell] = _entries;
					_entries++;
				}
			}

			Count++;
			return true;
		}

		private void Cells(double minX, double minY, double maxX, double maxY, out int c0, out int r0, out int c1, out int r1)
		{
			c0 = Clamp((int)Math.Floor((minX - _left) / _cellWidth), _columns);
			c1 = Clamp((int)Math.Floor((maxX - _left) / _cellWidth), _columns);
			r0 = Clamp((int)Math.Floor((minY - _top) / _cellHeight), _rows);
			r1 = Clamp((int)Math.Floor((maxY - _top) / _cellHeight), _rows);
		}

		private static int Clamp(int cell, int count) => cell < 0 ? 0 : cell >= count ? count - 1 : cell;

		private int Check(int label)
		{
			if (label < 0 || label >= Count)
			{
				throw new ArgumentOutOfRangeException(nameof(label));
			}

			return label;
		}
	}
}
