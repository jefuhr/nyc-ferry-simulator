// Private reconstruction backend adapted from the coordinator's synthetic spike.
// Invoke through reference.py recon build so consent and lineage are checked.
import Foundation
#if canImport(RealityKit) && os(macOS)
import RealityKit

func fail(_ message: String, _ code: Int32 = 1) -> Never {
	fputs(message + "\n", stderr)
	exit(code)
}

func privateOutput(_ url: URL) throws {
	var parent = url.standardizedFileURL
	let manager = FileManager.default
	while parent.path != "/" {
		if manager.fileExists(atPath: parent.appendingPathComponent(".git").path) {
			fail("Private reconstruction outputs must be outside every Git repository", 2)
		}
		if let attributes = try? manager.attributesOfItem(atPath: parent.path),
			attributes[.type] as? FileAttributeType == .typeSymbolicLink {
			fail("Output paths must not contain symlinks", 2)
		}
		parent.deleteLastPathComponent()
	}
	if manager.fileExists(atPath: url.path) { fail("Output already exists", 2) }
}

@available(macOS 14.0, *)
func reconstruct() throws {
	let args = CommandLine.arguments
	guard args.count == 6 else { fail("usage: recon frames mesh.usdz poses.json medium|full|raw sequential|unordered", 2) }
	guard PhotogrammetrySession.isSupported else { fail("PhotogrammetrySession is unavailable on this machine", 3) }
	let input = URL(fileURLWithPath: args[1], isDirectory: true)
	let output = URL(fileURLWithPath: args[2])
	let poseOutput = URL(fileURLWithPath: args[3])
	try privateOutput(output)
	try privateOutput(poseOutput)
	let detail: PhotogrammetrySession.Request.Detail
	switch args[4] {
	case "medium": detail = .medium
	case "full": detail = .full
	case "raw": detail = .raw
	default: fail("Measuring requires medium detail or higher", 2)
	}
	guard ["sequential", "unordered"].contains(args[5]) else { fail("Invalid sample ordering", 2) }
	var configuration = PhotogrammetrySession.Configuration()
	configuration.sampleOrdering = args[5] == "unordered" ? .unordered : .sequential
	configuration.featureSensitivity = .high
	let session = try PhotogrammetrySession(input: input, configuration: configuration)
	Task {
		do {
			var meshComplete = false
			var posesComplete = false
			for try await event in session.outputs {
				switch event {
				case .requestComplete(_, let result):
					switch result {
					case .modelFile: meshComplete = true
					case .poses(let poses):
						guard !poses.posesBySample.isEmpty else { fail("No camera poses were reconstructed") }
						let rows: [[String: Any]] = poses.posesBySample.keys.sorted().map { id in
							let pose = poses.posesBySample[id]!
							let t = pose.translation
							let q = pose.rotation.vector
							return ["sample_id": id, "frame": poses.urlsBySample[id]?.lastPathComponent ?? "unknown",
								"translation": [t.x, t.y, t.z], "rotation_xyzw": [q.x, q.y, q.z, q.w]]
						}
						let data = try JSONSerialization.data(withJSONObject: ["schema_version": 1, "detail": args[4], "frame": "RealityKit reconstruction coordinates; model units until scaled", "poses": rows], options: [.sortedKeys, .prettyPrinted])
						try data.write(to: poseOutput, options: [.atomic])
						try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: poseOutput.path)
						posesComplete = true
					default: break
					}
				case .requestError: fail("Photogrammetry request failed; no successful reconstruction claimed")
				case .processingCancelled: fail("Photogrammetry processing cancelled")
				case .processingComplete:
					guard meshComplete && posesComplete else { fail("Reconstruction finished without both mesh and camera poses") }
					try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: output.path)
					print("Reconstructed mesh and camera poses at \(args[4]) detail")
					exit(0)
				default: break
				}
			}
			fail("Photogrammetry output stream ended before processing completed")
		} catch { fail("Photogrammetry output or pose serialization failed") }
	}
	try session.process(requests: [.modelFile(url: output, detail: detail), .poses])
	RunLoop.main.run()
}

if #available(macOS 14.0, *) {
	do { try reconstruct() } catch { fail("PhotogrammetrySession could not start") }
} else { fail("PhotogrammetrySession camera poses require macOS 14 or later", 3) }
#else
fputs("RealityKit PhotogrammetrySession API is unavailable on this machine\n", stderr)
exit(3)
#endif
