using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.Chartplotter
{
	/// <summary>
	/// Writes solid-colour triangles, quads and line segments into UI Toolkit meshes in chunks, so one element can draw
	/// far more than the 65,535 vertices a single allocation holds. Positions are element-local pixels. Two ways of
	/// working: <b>direct</b> (<see cref="Begin"/> with a paint's context, every full chunk becomes one
	/// <see cref="MeshGenerationContext.Allocate"/> at once, <see cref="End"/> sends the last; the moving layer paints
	/// this way), and <b>staged</b> (<see cref="BeginStaged"/>, the chunks are kept in managed arrays across frames,
	/// <see cref="EndStaged"/> seals them, and <see cref="Upload"/> sends them at a later paint, as many times as the
	/// element is repainted; the static chart is generated this way, a slice per frame). The staging arrays are made
	/// once and grow only when a chart needs more chunks than any before it, so a generation allocates nothing after
	/// its first; a vertex is staged as 12 bytes and widened to UI Toolkit's <see cref="Vertex"/> at the upload through one managed chunk buffer. Dispose
	/// it with the element that owns it.
	/// </summary>
	public sealed class MeshWriter : IDisposable
	{
		/// <summary>Vertices per chunk: within the ushort index range with room for a last quad.</summary>
		public const int ChunkVertices = 32768;

		private const int ChunkIndices = ChunkVertices * 3;

		/// <summary>A staged vertex: the position in element pixels and the tint, 12 bytes.</summary>
		private struct Staged
		{
			public float X, Y;
			public Color32 Tint;
		}

		private readonly List<Staged[]> _chunkVertices = new List<Staged[]>();
		private readonly List<ushort[]> _chunkIndices = new List<ushort[]>();
		private readonly List<int> _chunkVertexCounts = new List<int>();
		private readonly List<int> _chunkIndexCounts = new List<int>();
		private readonly Vertex[] _widened = new Vertex[ChunkVertices];
		private NativeArray<Vertex> _nativeVertices;
		private NativeArray<ushort> _nativeIndices;
		private Staged[] _vertices;
		private ushort[] _indices;
		private int _chunk;
		private int _vertexCount;
		private int _indexCount;
		private int _sealed;
		private bool _staging;
		private MeshGenerationContext _context;

		public MeshWriter()
		{
			_nativeVertices = new NativeArray<Vertex>(ChunkVertices, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			_nativeIndices = new NativeArray<ushort>(ChunkIndices, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			EnsureChunk(0);
			_vertices = _chunkVertices[0];
			_indices = _chunkIndices[0];
		}

		/// <summary>Vertices written since <see cref="Begin"/> or <see cref="BeginStaged"/>, over every chunk.</summary>
		public int TotalVertices { get; private set; }

		/// <summary>Chunks sent to the context by the last paint (<see cref="End"/> or <see cref="Upload"/>).</summary>
		public int Chunks { get; private set; }

		/// <summary>Chunks sealed by the last staged generation.</summary>
		public int StagedChunks => _sealed;

		/// <summary>Managed bytes held by the staging arrays (all chunks ever needed, kept).</summary>
		public long StagedBytes => (long)_chunkVertices.Count * (ChunkVertices * 12L + ChunkIndices * 2L);

		/// <summary>True between <see cref="BeginStaged"/> and <see cref="EndStaged"/>.</summary>
		public bool Staging => _staging;

		/// <summary>Direct mode: writes go straight to this paint's context, chunk by chunk.</summary>
		public void Begin(MeshGenerationContext context)
		{
			_context = context;
			_staging = false;
			Reset();
		}

		/// <summary>Sends the last chunk of a direct paint. Call once at the end of it.</summary>
		public void End()
		{
			if (_vertexCount > 0 && _context != null)
			{
				UploadChunk(_context, _vertices, _indices, _vertexCount, _indexCount);
			}

			_vertexCount = 0;
			_indexCount = 0;
			_context = null;
		}

		/// <summary>Staged mode: writes fill the kept chunk arrays, across as many frames as the caller likes, until <see cref="EndStaged"/>.</summary>
		public void BeginStaged()
		{
			_context = null;
			_staging = true;
			Reset();
		}

		/// <summary>Seals the staged chunks for <see cref="Upload"/>.</summary>
		public void EndStaged()
		{
			if (_vertexCount > 0)
			{
				Seal();
			}

			_staging = false;
		}

		/// <summary>Sends every sealed chunk to a paint's context (repeatable: each repaint uploads the same chunks again).</summary>
		public void Upload(MeshGenerationContext context)
		{
			Chunks = 0;
			for (var c = 0; c < _sealed; c++)
			{
				UploadChunk(context, _chunkVertices[c], _chunkIndices[c], _chunkVertexCounts[c], _chunkIndexCounts[c]);
			}
		}

		private void Reset()
		{
			_chunk = 0;
			_vertices = _chunkVertices[0];
			_indices = _chunkIndices[0];
			_vertexCount = 0;
			_indexCount = 0;
			_sealed = 0;
			TotalVertices = 0;
			Chunks = 0;
		}

		private void EnsureChunk(int index)
		{
			while (_chunkVertices.Count <= index)
			{
				// Growth only: a chart that needs more chunks than any before it allocates them once, and they are kept.
				_chunkVertices.Add(new Staged[ChunkVertices]);
				_chunkIndices.Add(new ushort[ChunkIndices]);
				_chunkVertexCounts.Add(0);
				_chunkIndexCounts.Add(0);
			}
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
			_vertices[b] = new Staged { X = x0, Y = y0, Tint = color };
			_vertices[b + 1] = new Staged { X = x1, Y = y1, Tint = color };
			_vertices[b + 2] = new Staged { X = x2, Y = y2, Tint = color };
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
			_vertices[b] = new Staged { X = x0, Y = y0, Tint = color };
			_vertices[b + 1] = new Staged { X = x1, Y = y1, Tint = color };
			_vertices[b + 2] = new Staged { X = x2, Y = y2, Tint = color };
			_vertices[b + 3] = new Staged { X = x3, Y = y3, Tint = color };
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
			if (_vertexCount + vertices > ChunkVertices || _indexCount + indices > ChunkIndices)
			{
				if (_staging)
				{
					Seal();
				}
				else
				{
					Flush();
				}
			}
		}

		/// <summary>Direct mode: the full chunk goes to the context now and the chunk array is reused.</summary>
		private void Flush()
		{
			if (_vertexCount > 0 && _context != null)
			{
				UploadChunk(_context, _vertices, _indices, _vertexCount, _indexCount);
			}

			_vertexCount = 0;
			_indexCount = 0;
		}

		/// <summary>Staged mode: the full chunk is kept and writing moves to the next chunk array.</summary>
		private void Seal()
		{
			_chunkVertexCounts[_chunk] = _vertexCount;
			_chunkIndexCounts[_chunk] = _indexCount;
			_sealed = _chunk + 1;
			_chunk++;
			EnsureChunk(_chunk);
			_vertices = _chunkVertices[_chunk];
			_indices = _chunkIndices[_chunk];
			_vertexCount = 0;
			_indexCount = 0;
		}

		private void UploadChunk(MeshGenerationContext context, Staged[] vertices, ushort[] indices, int vertexCount, int indexCount)
		{
			var data = context.Allocate(vertexCount, indexCount);
			// The staged vertices are widened in a managed pass (no native safety checks per element), then block-copied with the indices.
			for (var i = 0; i < vertexCount; i++)
			{
				_widened[i] = new Vertex { position = new Vector3(vertices[i].X, vertices[i].Y, 0f), tint = vertices[i].Tint };
			}

			NativeArray<Vertex>.Copy(_widened, 0, _nativeVertices, 0, vertexCount);
			NativeArray<ushort>.Copy(indices, 0, _nativeIndices, 0, indexCount);
			data.SetAllVertices(_nativeVertices.Slice(0, vertexCount));
			data.SetAllIndices(_nativeIndices.Slice(0, indexCount));
			Chunks++;
		}

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
