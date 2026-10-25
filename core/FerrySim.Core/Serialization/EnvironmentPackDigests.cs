using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FerrySim.Core.Serialization
{
	/// <summary>
	/// The digests of the environment pack format (ferrysim-env/1, digests as revised 2026-10-25), over raw file bytes
	/// so every byte of a pack is covered. Each pack folder has a <c>DIGEST</c> file: 64 lower-case hex characters and
	/// a newline.
	/// <list type="bullet">
	/// <item>Depth digest: SHA-256 over <c>manifest.json</c>, NUL, the depth manifest's bytes, NUL; then for each grid in
	/// <c>grids</c> order its file name, NUL, the file's bytes, NUL.</item>
	/// <item>Day digest: SHA-256 over <c>manifest.json</c>, NUL, the day manifest's bytes, NUL; then currents.csv,
	/// water_levels.csv and wind.csv in that order, each as name, NUL, bytes, NUL; then the depth digest's 64 hex
	/// characters and a NUL.</item>
	/// </list>
	/// </summary>
	public static class EnvironmentPackDigests
	{
		/// <summary>The manifest in each pack folder.</summary>
		public const string ManifestFile = "manifest.json";

		/// <summary>The digest file in each pack folder.</summary>
		public const string DigestFile = "DIGEST";

		/// <summary>The three series files of a day, in digest order.</summary>
		public static readonly IReadOnlyList<string> SeriesFiles = new[] { "currents.csv", "water_levels.csv", "wind.csv" };

		/// <summary>Lower-case hex SHA-256 of some bytes.</summary>
		public static string Sha256Hex(byte[] bytes)
		{
			using (var sha = SHA256.Create())
			{
				return Hex(sha.ComputeHash(bytes));
			}
		}

		/// <summary>The depth digest from the depth manifest's bytes and each grid's file name and bytes, in grid order.</summary>
		public static string DepthDigest(byte[] manifest, IReadOnlyList<string> gridFileNames, IReadOnlyList<byte[]> gridFiles)
		{
			if (gridFileNames.Count != gridFiles.Count)
			{
				throw new ArgumentException("One file's bytes per grid file name is needed.");
			}

			using (var stream = new MemoryStream())
			{
				Append(stream, ManifestFile, manifest);
				for (var i = 0; i < gridFiles.Count; i++)
				{
					Append(stream, gridFileNames[i], gridFiles[i]);
				}

				return Sha256Hex(stream.ToArray());
			}
		}

		/// <summary>The day digest from the day manifest's bytes, the three series files' bytes (in <see cref="SeriesFiles"/> order) and the depth digest.</summary>
		public static string DayDigest(byte[] manifest, IReadOnlyList<byte[]> seriesFiles, string depthDigest)
		{
			if (seriesFiles.Count != SeriesFiles.Count)
			{
				throw new ArgumentException("The day digest covers exactly the three series files.");
			}

			using (var stream = new MemoryStream())
			{
				Append(stream, ManifestFile, manifest);
				for (var i = 0; i < SeriesFiles.Count; i++)
				{
					Append(stream, SeriesFiles[i], seriesFiles[i]);
				}

				var depth = Encoding.ASCII.GetBytes(depthDigest);
				stream.Write(depth, 0, depth.Length);
				stream.WriteByte(0);
				return Sha256Hex(stream.ToArray());
			}
		}

		/// <summary>The bytes of a <c>DIGEST</c> file for a digest.</summary>
		public static byte[] DigestFileBytes(string digest) => Encoding.ASCII.GetBytes(digest + "\n");

		/// <summary>The digest a <c>DIGEST</c> file holds, or null when it is not exactly 64 lower-case hex characters and a newline.</summary>
		public static string ParseDigestFile(byte[] bytes)
		{
			if (bytes == null || bytes.Length != 65 || bytes[64] != (byte)'\n')
			{
				return null;
			}

			var text = Encoding.ASCII.GetString(bytes, 0, 64);
			return IsHexDigest(text) ? text : null;
		}

		/// <summary>True for exactly 64 lower-case hex characters.</summary>
		public static bool IsHexDigest(string text)
		{
			if (text == null || text.Length != 64)
			{
				return false;
			}

			foreach (var ch in text)
			{
				if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>
		/// Python's <c>repr</c> of a finite double, which the format's series files use for numbers: the shortest digits
		/// that round-trip, in fixed notation when the decimal exponent is from −4 to 15 (with ".0" for whole numbers) and
		/// as d.ddde±XX otherwise.
		/// </summary>
		public static string PythonFloat(double value)
		{
			if (double.IsNaN(value) || double.IsInfinity(value))
			{
				throw new ArgumentException("Only finite numbers are written to environment packs.");
			}

			if (value == 0.0)
			{
				return BitConverter.DoubleToInt64Bits(value) < 0 ? "-0.0" : "0.0";
			}

			// "R" gives the shortest round-trip digits on .NET Core 3.0 and later: d[.ddd][E±x].
			var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
			var exponentAt = text.IndexOfAny(new[] { 'E', 'e' });
			var exponent = exponentAt < 0 ? 0 : int.Parse(text.Substring(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
			var mantissa = exponentAt < 0 ? text : text.Substring(0, exponentAt);
			var point = mantissa.IndexOf('.');
			var whole = point < 0 ? mantissa : mantissa.Substring(0, point);
			var all = whole + (point < 0 ? string.Empty : mantissa.Substring(point + 1));
			var leading = 0;
			while (leading < all.Length && all[leading] == '0')
			{
				leading++;
			}

			// value = 0.d₁d₂… × 10^decimalPoint
			var digits = all.Substring(leading).TrimEnd('0');
			var decimalPoint = whole.Length - leading + exponent;
			var builder = new StringBuilder();
			if (value < 0.0)
			{
				builder.Append('-');
			}

			if (decimalPoint > -4 && decimalPoint <= 16)
			{
				if (decimalPoint <= 0)
				{
					builder.Append("0.").Append('0', -decimalPoint).Append(digits);
				}
				else if (decimalPoint < digits.Length)
				{
					builder.Append(digits, 0, decimalPoint).Append('.').Append(digits, decimalPoint, digits.Length - decimalPoint);
				}
				else
				{
					builder.Append(digits).Append('0', decimalPoint - digits.Length).Append(".0");
				}

				return builder.ToString();
			}

			builder.Append(digits[0]);
			if (digits.Length > 1)
			{
				builder.Append('.').Append(digits, 1, digits.Length - 1);
			}

			var power = decimalPoint - 1;
			builder.Append('e').Append(power < 0 ? '-' : '+').Append(Math.Abs(power).ToString("00", CultureInfo.InvariantCulture));
			return builder.ToString();
		}

		private static void Append(Stream stream, string name, byte[] bytes)
		{
			var nameBytes = Encoding.UTF8.GetBytes(name);
			stream.Write(nameBytes, 0, nameBytes.Length);
			stream.WriteByte(0);
			stream.Write(bytes, 0, bytes.Length);
			stream.WriteByte(0);
		}

		private static string Hex(byte[] bytes)
		{
			var builder = new StringBuilder(bytes.Length * 2);
			foreach (var b in bytes)
			{
				builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
			}

			return builder.ToString();
		}
	}
}
