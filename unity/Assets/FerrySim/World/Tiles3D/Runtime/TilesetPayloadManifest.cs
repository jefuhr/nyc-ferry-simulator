using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace FerrySim.World.Tiles3D
{
	/// <summary>
	/// <c>harbor3d/payload.json</c>: what a probe build copied into StreamingAssets, written by the build and checked by the
	/// player before it loads anything. Paths are relative to the harbor3d folder, with forward slashes. Field names are
	/// the file's.
	/// </summary>
	[Serializable]
	public sealed class TilesetPayloadManifest
	{
		public const string FileName = "payload.json";

		/// <summary><c>full</c> (the whole harbor) or <c>slice-and-neighbours</c>.</summary>
		public string scope;

		/// <summary>Why that scope: the measured zip size against the limit, or an override.</summary>
		public string reason;

		public string[] tiles = Array.Empty<string>();
		public PayloadFile[] files = Array.Empty<PayloadFile>();

		/// <summary>The SHA-256 of the pipeline's index.json the files were copied from.</summary>
		public string source_index_sha256;

		public long TotalBytes
		{
			get
			{
				var total = 0L;
				foreach (var file in files)
				{
					total += file.bytes;
				}

				return total;
			}
		}

		/// <summary>
		/// Checks every listed file under <paramref name="folder"/> for its size and SHA-256. Returns the problems, one line
		/// each, and counts the missing and the mismatched.
		/// </summary>
		public List<string> Check(string folder, out int missing, out int mismatched)
		{
			var problems = new List<string>();
			missing = 0;
			mismatched = 0;
			using (var sha = SHA256.Create())
			{
				foreach (var file in files)
				{
					var path = Path.Combine(folder, file.path);
					if (!File.Exists(path))
					{
						missing++;
						problems.Add($"missing: {file.path}");
						continue;
					}

					var bytes = File.ReadAllBytes(path);
					var digest = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
					if (bytes.LongLength != file.bytes || digest != file.sha256)
					{
						mismatched++;
						problems.Add($"differs: {file.path} ({bytes.LongLength} bytes, {digest.Substring(0, 12)}; payload.json has {file.bytes} bytes, {file.sha256.Substring(0, Math.Min(12, file.sha256.Length))})");
					}
				}
			}

			return problems;
		}
	}

	[Serializable]
	public sealed class PayloadFile
	{
		public string path;
		public long bytes;
		public string sha256;
	}
}
