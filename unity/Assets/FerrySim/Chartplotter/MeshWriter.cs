using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.Chartplotter
{
	/// <summary>
	/// Writes solid-colour triangles, quads and line segments into a UI Toolkit mesh in chunks, so one element can draw
	/// far more than the 65,535 vertices a single allocation holds. The staging arrays are made once: filling
	/// a chunk allocates no managed memory, and each full chunk becomes one <see cref="MeshGenerationContext.Allocate"/>.
	/// Positions are element-local pixels. Dispose it with the element that owns it.
	/// </summary>
	public sealed class MeshWriter : IDisposable
	{
		/// <summary>Vertices per chunk: within the ushort index range with room for a last quad.</summary>
		public const int ChunkVertices = 32768;

		private readonly Vertex[] _vertices = new Vertex[ChunkVertices];
		private readonly ushort[] _indices = new ushort[ChunkVertices * 3];
		private NativeArray<Vertex> _nativeVertices;
		private NativeArray<ushort> _nativeIndices;
		private int _vertexCount;
		private int _indexCount;
		private MeshGenerationContext _context;

		public MeshWriter()
		{
			_nativeVertices = new NativeArray<Vertex>(ChunkVertices, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			_nativeIndices = new NativeArray<ushort>(ChunkVertices * 3, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
		}

		/// <summary>Vertices written since <see cref="Begin"/>, over every chunk.</summary>
		public int TotalVertices { get; private set; }

		/// <summary>Chunks flushed since <see cref="Begin"/>.</summary>
		public int Chunks { get; private set; }

		public void Begin(MeshGenerationContext context)
		{
			_context = context;
			_vertexCount = 0;
			_indexCount = 0;
			TotalVertices = 0;
			Chunks = 0;
		}

		/// <summary>Sends the last chunk. Call once at the end of a paint.</summary>
		public void End()
		{
			Flush();
			_context = null;
		}

		/// <summary>
		/// A triangle, in either order: UI Toolkit culls back faces, so corners are rewound clockwise on screen (y down) when needed.
		/// </summary>
		public void Triangle(float x0, float y0, float x1, float y1, float x2, float y2, Color32 color)
		{
			if ((x1 - x0) * (y2 - y0) - (y1 - y0) * (x2 - x0) < 0f)
			{
				var tx = x1; var ty = y1;
				x1 = x2; y1 = y2;
				x2 = tx; y2 = ty;
			}

			Reserve(3, 3);
			var b = _vertexCount;
			_vertices[b] = Vertex(x0, y0, color);
			_vertices[b + 1] = Vertex(x1, y1, color);
			_vertices[b + 2] = Vertex(x2, y2, color);
			_indices[_indexCount] = (ushort)b;
			_indices[_indexCount + 1] = (ushort)(b + 1);
			_indices[_indexCount + 2] = (ushort)(b + 2);
			_vertexCount += 3;
			_indexCount += 3;
			TotalVertices += 3;
		}

		/// <summary>A quad from four corners in order around it, either way round: it is rewound clockwise on screen when needed.</summary>
		public void Quad(float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3, Color32 color)
		{
			var area = (x1 - x0) * (y2 - y0) - (y1 - y0) * (x2 - x0) + (x2 - x0) * (y3 - y0) - (y2 - y0) * (x3 - x0);
			if (area < 0f)
			{
				var tx = x1; var ty = y1;
				x1 = x3; y1 = y3;
				x3 = tx; y3 = ty;
			}

			Reserve(4, 6);
			var b = _vertexCount;
			_vertices[b] = Vertex(x0, y0, color);
			_vertices[b + 1] = Vertex(x1, y1, color);
			_vertices[b + 2] = Vertex(x2, y2, color);
			_vertices[b + 3] = Vertex(x3, y3, color);
			_indices[_indexCount] = (ushort)b;
			_indices[_indexCount + 1] = (ushort)(b + 1);
			_indices[_indexCount + 2] = (ushort)(b + 2);
			_indices[_indexCount + 3] = (ushort)b;
			_indices[_indexCount + 4] = (ushort)(b + 2);
			_indices[_indexCount + 5] = (ushort)(b + 3);
			_vertexCount += 4;
			_indexCount += 6;
			TotalVertices += 4;
		}

		/// <summary>A line segment of a width in pixels, square-ended.</summary>
		public void Line(float x0, float y0, float x1, float y1, float width, Color32 color)
		{
			var dx = x1 - x0;
			var dy = y1 - y0;
			var length = Mathf.Sqrt(dx * dx + dy * dy);
			if (length <= 1e-6f)
			{
				return;
			}

			var nx = -dy / length * width * 0.5f;
			var ny = dx / length * width * 0.5f;
			Quad(x0 + nx, y0 + ny, x1 + nx, y1 + ny, x1 - nx, y1 - ny, x0 - nx, y0 - ny, color);
		}

		/// <summary>A dashed line segment; the pattern starts at the first point.</summary>
		public void DashedLine(float x0, float y0, float x1, float y1, float width, float dash, float gap, Color32 color)
		{
			var dx = x1 - x0;
			var dy = y1 - y0;
			var length = Mathf.Sqrt(dx * dx + dy * dy);
			if (length <= 1e-6f || dash <= 0f)
			{
				return;
			}

			var ux = dx / length;
			var uy = dy / length;
			for (var start = 0f; start < length; start += dash + gap)
			{
				var end = Mathf.Min(length, start + dash);
				Line(x0 + ux * start, y0 + uy * start, x0 + ux * end, y0 + uy * end, width, color);
			}
		}

		/// <summary>A filled axis-aligned rectangle.</summary>
		public void Rect(float x, float y, float width, float height, Color32 color) =>
			Quad(x, y, x + width, y, x + width, y + height, x, y + height, color);

		/// <summary>A square-sided ring (a rectangle outline) centred on a point.</summary>
		public void SquareOutline(float cx, float cy, float half, float width, Color32 color)
		{
			Line(cx - half, cy - half, cx + half, cy - half, width, color);
			Line(cx + half, cy - half, cx + half, cy + half, width, color);
			Line(cx + half, cy + half, cx - half, cy + half, width, color);
			Line(cx - half, cy + half, cx - half, cy - half, width, color);
		}

		/// <summary>A regular polygon outline (circle at 16 sides) centred on a point.</summary>
		public void CircleOutline(float cx, float cy, float radius, float width, Color32 color, int sides = 16)
		{
			var px = cx;
			var py = cy - radius;
			for (var i = 1; i <= sides; i++)
			{
				var angle = 2f * Mathf.PI * i / sides;
				var x = cx + radius * Mathf.Sin(angle);
				var y = cy - radius * Mathf.Cos(angle);
				Line(px, py, x, y, width, color);
				px = x;
				py = y;
			}
		}

		/// <summary>A filled regular polygon (circle at 16 sides) centred on a point.</summary>
		public void Disc(float cx, float cy, float radius, Color32 color, int sides = 16)
		{
			var px = cx;
			var py = cy - radius;
			for (var i = 1; i <= sides; i++)
			{
				var angle = 2f * Mathf.PI * i / sides;
				var x = cx + radius * Mathf.Sin(angle);
				var y = cy - radius * Mathf.Cos(angle);
				Triangle(cx, cy, px, py, x, y, color);
				px = x;
				py = y;
			}
		}

		private void Reserve(int vertices, int indices)
		{
			if (_vertexCount + vertices > ChunkVertices || _indexCount + indices > _indices.Length)
			{
				Flush();
			}
		}

		private void Flush()
		{
			if (_vertexCount == 0 || _context == null)
			{
				_vertexCount = 0;
				_indexCount = 0;
				return;
			}

			var data = _context.Allocate(_vertexCount, _indexCount);
			// Managed staging is written element by element without native safety checks; one block copy per chunk moves it over.
			NativeArray<Vertex>.Copy(_vertices, 0, _nativeVertices, 0, _vertexCount);
			NativeArray<ushort>.Copy(_indices, 0, _nativeIndices, 0, _indexCount);
			data.SetAllVertices(_nativeVertices.Slice(0, _vertexCount));
			data.SetAllIndices(_nativeIndices.Slice(0, _indexCount));
			Chunks++;
			_vertexCount = 0;
			_indexCount = 0;
		}

		private static Vertex Vertex(float x, float y, Color32 color) => new Vertex { position = new Vector3(x, y, 0f), tint = color };

		public void Dispose()
		{
			if (_nativeVertices.IsCreated)
			{
				_nativeVertices.Dispose();
			}

			if (_nativeIndices.IsCreated)
			{
				_nativeIndices.Dispose();
			}
		}
	}
}
