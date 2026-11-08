using System;
using System.IO;
using System.Text;

namespace FerrySim.Audio.EditorTools
{
	/// <summary>Copies PCM/IEEE-float WAV sample frames without decoding, normalizing or changing channel data.</summary>
	public static class WaveLoop
	{
		public static double Duration(string path)
		{
			Read(path, out var format, out var data);
			return data.Length / (double)BitConverter.ToUInt16(format, 12) / BitConverter.ToInt32(format, 4);
		}

		public static void Trim(string input, string output, int firstFrame, int endFrame)
		{
			Read(input, out var format, out var data);
			var align = BitConverter.ToUInt16(format, 12);
			if (firstFrame < 0 || endFrame <= firstFrame || endFrame > data.Length / align)
				throw new ArgumentException("Loop sample frames exceed WAV data: " + input);
			var count = checked((endFrame - firstFrame) * align);
			using (var stream = new MemoryStream())
			{
				using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
				{
					writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(20 + format.Length + (format.Length & 1) + count + (count & 1));
					writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(format.Length); writer.Write(format);
					if ((format.Length & 1) != 0) writer.Write((byte)0);
					writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(count); writer.Write(data, checked(firstFrame * align), count);
					if ((count & 1) != 0) writer.Write((byte)0);
				}
				var bytes = stream.ToArray();
				if (File.Exists(output) && Equal(File.ReadAllBytes(output), bytes)) return;
				File.WriteAllBytes(output, bytes);
			}
		}

		private static bool Equal(byte[] left, byte[] right)
		{ if (left.Length != right.Length) return false; for (var i = 0; i < left.Length; i++) if (left[i] != right[i]) return false; return true; }

		private static void Read(string path, out byte[] format, out byte[] data)
		{
			format = data = null;
			using (var reader = new BinaryReader(File.OpenRead(path)))
			{
				if (reader.BaseStream.Length < 12 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new ArgumentException("Expected RIFF WAV: " + path);
				reader.ReadUInt32();
				if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new ArgumentException("Expected WAVE: " + path);
				while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
				{
					var id = Encoding.ASCII.GetString(reader.ReadBytes(4)); var size = reader.ReadUInt32();
					if (size > int.MaxValue || reader.BaseStream.Position + size > reader.BaseStream.Length) throw new ArgumentException("Truncated WAV chunk: " + path);
					if (id == "fmt ") format = reader.ReadBytes((int)size);
					else if (id == "data") data = reader.ReadBytes((int)size);
					else reader.BaseStream.Position += size;
					if ((size & 1) != 0 && reader.BaseStream.Position < reader.BaseStream.Length) reader.BaseStream.Position++;
				}
			}
			if (format == null || format.Length < 16 || data == null || BitConverter.ToUInt16(format, 12) == 0 || BitConverter.ToInt32(format, 4) <= 0)
				throw new ArgumentException("WAV is missing valid format/sample data: " + path);
			var encoding = BitConverter.ToUInt16(format, 0);
			if (encoding != 1 && encoding != 3 && encoding != 65534) throw new ArgumentException("Loop trimming needs uncompressed PCM or IEEE-float WAV: " + path);
		}
	}
}
