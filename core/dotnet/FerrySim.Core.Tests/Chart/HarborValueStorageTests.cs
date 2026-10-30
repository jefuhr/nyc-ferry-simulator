using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FerrySim.Core.Serialization;
using FerrySim.Core.World;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Chart
{
	// Failure modes: reordered fields, mutable backing storage, changed null/absent semantics,
	// changed dictionary exceptions/equality, and drift in nested real runtime evidence.
	public class HarborValueStorageTests
	{
		[TestCase(0)]
		[TestCase(1)]
		[TestCase(5)]
		[TestCase(16)]
		[TestCase(17)]
		public void ObjectStoragePreservesOrderedImmutableDictionaryBehavior(int count)
		{
			var source = new Dictionary<string, HarborValue>(StringComparer.Ordinal);
			for (var i = 0; i < count; i++) source.Add("field-" + (count - i), HarborValue.FromNumber(i));
			var original = source.ToArray(); var value = HarborValue.FromFields(source); source.Clear();
			Assert.That(value.Fields.ToArray(), Is.EqualTo(original));
			Assert.That(value.Fields.Keys.ToArray(), Is.EqualTo(original.Select(p => p.Key)));
			Assert.That(value.Fields.Values.ToArray(), Is.EqualTo(original.Select(p => p.Value)));
			Assert.That(value["absent"], Is.SameAs(HarborValue.Null));
			Assert.That(value.IsNull, Is.False);
			foreach (var pair in original)
			{
				Assert.That(value[pair.Key], Is.SameAs(pair.Value)); Assert.That(value.Fields.ContainsKey(pair.Key), Is.True);
				Assert.That(value.Fields.TryGetValue(pair.Key, out var actual), Is.True); Assert.That(actual, Is.SameAs(pair.Value));
			}
			Assert.That(value.Fields.TryGetValue("missing", out var missing), Is.False); Assert.That(missing, Is.Null);
			Assert.Throws<ArgumentNullException>(() => value.Fields.ContainsKey(null));
			Assert.Throws<ArgumentNullException>(() => value.Fields.TryGetValue(null, out _));
			Assert.Throws<ArgumentNullException>(() => { var ignored = value[null]; });
			Assert.Throws<KeyNotFoundException>(() => { var ignored = value.Fields["missing"]; });
			var dictionary = (IDictionary<string, HarborValue>)value.Fields;
			Assert.That(dictionary.IsReadOnly, Is.True);
			Assert.Throws<NotSupportedException>(() => dictionary.Add("new", HarborValue.Null));
			Assert.Throws<NotSupportedException>(() => dictionary.Remove("missing"));
			Assert.Throws<NotSupportedException>(() => dictionary.Clear());
			Assert.Throws<NotSupportedException>(() => dictionary["new"] = HarborValue.Null);
			var copy = new KeyValuePair<string, HarborValue>[count + 2]; dictionary.CopyTo(copy, 1);
			Assert.That(copy.Skip(1).Take(count), Is.EqualTo(original));
			Assert.That(value.Equals(HarborValue.FromFields(new Dictionary<string, HarborValue>())), Is.False);
			Assert.That(value.ToString(), Is.EqualTo("FerrySim.Core.World.HarborValue"));
		}

		[Test]
		public void BulkDictionaryExceptionBehaviorMatchesOriginalStorage()
		{
			var fields = new Dictionary<string, HarborValue> { ["one"] = HarborValue.Null, ["two"] = HarborValue.FromText("two") };
			var old = (ICollection<KeyValuePair<string, HarborValue>>)new System.Collections.ObjectModel.ReadOnlyDictionary<string, HarborValue>(new Dictionary<string, HarborValue>(fields, StringComparer.Ordinal));
			var current = (ICollection<KeyValuePair<string, HarborValue>>)HarborValue.FromFields(fields).Fields;
			foreach (var length in new[] { -1, 0, 1, 2 }) foreach (var index in new[] { -1, 0, 1, 3 })
			{
				var destination = length < 0 ? null : new KeyValuePair<string, HarborValue>[length];
				var expected = Catch(() => old.CopyTo(destination, index)); var actual = Catch(() => current.CopyTo(destination, index));
				Assert.That(actual?.GetType(), Is.EqualTo(expected?.GetType()));
				Assert.That(actual?.Message, Is.EqualTo(expected?.Message));
			}
		}
		private static Exception Catch(Action action) { try { action(); return null; } catch (Exception exception) { return exception; } }

		private sealed class ReferenceKeys : IEqualityComparer<string>
		{
			public bool Equals(string a, string b) => ReferenceEquals(a, b);
			public int GetHashCode(string value) => RuntimeHelpers.GetHashCode(value);
		}
		[Test]
		public void SourceComparerDoesNotChangeOrdinalCopyValidation()
		{
			var source = new Dictionary<string, HarborValue>(new ReferenceKeys())
			{
				[new string('a', 1)] = HarborValue.Null,
				[new string('a', 1)] = HarborValue.Null
			};
			var expected = Catch(() => new Dictionary<string, HarborValue>(source, StringComparer.Ordinal));
			var actual = Catch(() => HarborValue.FromFields(source));
			Assert.That(actual?.GetType(), Is.EqualTo(expected.GetType()));
			Assert.That(actual?.Message, Is.EqualTo(expected.Message));
		}

		[Test]
		public void ValueFactoriesPreserveKindsAndNullExceptions()
		{
			Assert.Throws<ArgumentNullException>(() => HarborValue.FromFields(null));
			Assert.Throws<ArgumentNullException>(() => HarborValue.FromText(null));
			Assert.Throws<ArgumentException>(() => HarborValue.FromNumber(double.NaN));
			Assert.Throws<ArgumentException>(() => HarborValue.FromNumber(double.PositiveInfinity));
			Assert.That(HarborValue.Null[null], Is.SameAs(HarborValue.Null));
			var fields = new Dictionary<string, HarborValue>(StringComparer.OrdinalIgnoreCase) { ["Mixed"] = null };
			var value = HarborValue.FromFields(fields);
			Assert.That(value.Fields.ContainsKey("mixed"), Is.False);
			Assert.That(value["Mixed"], Is.Null);
			Assert.That(value["mixed"], Is.SameAs(HarborValue.Null));
		}

		[Test, Explicit("Requires copied real runtime pack; compares canonical ordered evidence with the pre-change snapshot.")]
		public void RuntimePackEvidenceMatchesBaseline()
		{
			var runtime = HarborRuntimePackJson.Load(Path.Combine(RepoPaths.Root, "data/processed/harbor/runtime"));
			using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
			var count = 0;
			void Value(HarborValue value)
			{
				count++;
				if (value.IsNull) writer.Write((byte)0);
				else if (value.Text != null) { writer.Write((byte)1); writer.Write(value.Text); }
				else if (value.Number.HasValue) { writer.Write((byte)2); writer.Write(BitConverter.DoubleToInt64Bits(value.Number.Value)); }
				else if (value.Boolean.HasValue) { writer.Write((byte)3); writer.Write(value.Boolean.Value); }
				else if (value.Items != null) { writer.Write((byte)4); writer.Write(value.Items.Count); foreach (var item in value.Items) Value(item); }
				else { writer.Write((byte)5); writer.Write(value.Fields.Count); foreach (var field in value.Fields) { writer.Write(field.Key); Value(field.Value); Assert.That(value[field.Key], Is.SameAs(field.Value)); } }
			}
			void Record(string group, string id, HarborValue value) { writer.Write(group); writer.Write(id); Value(value); }
			Record("manifest", runtime.Digest, runtime.Manifest);
			foreach (var aid in runtime.Aids) Record("aid", aid.Id, aid.Record);
			foreach (var bridge in runtime.Bridges)
			{
				Record("bridge", bridge.Id, bridge.Record);
				foreach (var span in bridge.Spans) Record("span", span.Id, span.Record);
				foreach (var pier in bridge.Piers) Record("pier", pier.Id, pier.Record);
			}
			foreach (var stop in runtime.Stops) { Record("stop", stop.Id, stop.Record); foreach (var berth in stop.Berths) Record("berth", berth.Id, berth.Record); }
			foreach (var barge in runtime.Barges) Record("barge", barge.Id, barge.Record);
			foreach (var footprint in runtime.Footprints) Record("footprint", footprint.Id, footprint.Record);
			writer.Flush();
			var digest = Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
			TestContext.Progress.WriteLine($"runtime_evidence_sha256={digest} values={count} bytes={stream.Length} runtime_digest={runtime.Digest}");
			// Recorded on 42fada7 HarborValue before the approved internal-storage edit.
			Assert.That(digest, Is.EqualTo("3b4c4c09cc77076e8520d9c96ee0dd618f127c05cb8b579728951fd9ec5af4f9"));
			Assert.That(count, Is.EqualTo(353668));
			Assert.That(stream.Length, Is.EqualTo(4221774));
		}

		[Test, Explicit("Requires copied real runtime pack; run by exact test filter.")]
		public void RuntimeLoadMeasurements()
		{
			var selected = Environment.GetEnvironmentVariable("FERRYSIM_CHART_MEASUREMENT_RUN");
			if (selected != null) MeasureRuntime(int.Parse(selected));
			else for (var run = 1; run <= 3; run++) MeasureRuntime(run);
		}
		[MethodImpl(MethodImplOptions.NoInlining)]
		private static void MeasureRuntime(int run)
		{
			GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); var before = GC.GetTotalMemory(true);
			var heapBefore = GC.GetGCMemoryInfo();
			var watch = new Stopwatch(); var allocatedBefore = GC.GetTotalAllocatedBytes(true); var gen2Before = GC.CollectionCount(2);
			watch.Start(); var runtime = HarborRuntimePackJson.Load(Path.Combine(RepoPaths.Root, "data/processed/harbor/runtime")); watch.Stop();
			var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore; var gen2 = GC.CollectionCount(2) - gen2Before;
			var retained = GC.GetTotalMemory(true) - before;
			var heapAfter = GC.GetGCMemoryInfo();
			var heapUsed = heapAfter.HeapSizeBytes - heapAfter.FragmentedBytes - heapBefore.HeapSizeBytes + heapBefore.FragmentedBytes;
			TestContext.Progress.WriteLine($"reader=runtime run={run} elapsed_ms={watch.Elapsed.TotalMilliseconds:F3} allocated_bytes={allocated} retained_bytes={retained} gen2={gen2} heap_used_delta_bytes={heapUsed}");
			GC.KeepAlive(runtime);
		}
	}
}
