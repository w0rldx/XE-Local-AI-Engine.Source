import { describe, expect, it } from "vitest";

import {
	downloadPercent,
	formatBytes,
	isArtifactDownloading,
	isRuntimeInstalling,
	mergeTrainingLogs,
	toBaseArtifactView,
	toRuntimeStatusView,
	trainingLogEntries,
} from "@/features/training/models/TrainingModels";

describe("mergeTrainingLogs", () => {
	it("deduplicates overlapping slices by sequence so a hub replay does not double lines", () => {
		const first = trainingLogEntries(0, ["a", "b", "c"]);
		const overlapping = trainingLogEntries(2, ["c", "d"]);

		expect(mergeTrainingLogs(first, overlapping).map((entry) => entry.message)).toEqual(["a", "b", "c", "d"]);
	});

	it("orders by sequence regardless of the order slices arrive in", () => {
		expect(mergeTrainingLogs(trainingLogEntries(5, ["late"]), trainingLogEntries(0, ["early"])).map((e) => e.message)).toEqual([
			"early",
			"late",
		]);
	});

	it("drops entries with an unusable sequence rather than sorting them to the front", () => {
		expect(mergeTrainingLogs([{ sequence: -1, message: "bad" }, ...trainingLogEntries(0, ["good"])]).map((e) => e.message)).toEqual([
			"good",
		]);
	});
});

describe("downloadPercent", () => {
	it("returns null when the total is unknown, so the bar stays indeterminate instead of guessing", () => {
		expect(downloadPercent({ completedBytes: 10, totalBytes: null, fileIndex: 1, fileCount: 2 })).toBeNull();
		expect(downloadPercent(null)).toBeNull();
	});

	it("clamps at 100 so a resumed transfer reporting more than the declared total cannot overshoot", () => {
		expect(downloadPercent({ completedBytes: 300, totalBytes: 200, fileIndex: 1, fileCount: 1 })).toBe(100);
		expect(downloadPercent({ completedBytes: 50, totalBytes: 200, fileIndex: 1, fileCount: 1 })).toBe(25);
	});
});

describe("phase predicates", () => {
	it("treats only the in-flight phases as installing", () => {
		expect(isRuntimeInstalling("InstallingPackages")).toBe(true);
		expect(isRuntimeInstalling("Verifying")).toBe(true);
		expect(isRuntimeInstalling("Ready")).toBe(false);
		expect(isRuntimeInstalling("Idle")).toBe(false);
		expect(isRuntimeInstalling("Failed")).toBe(false);
	});

	it("treats only Downloading as an in-flight artifact", () => {
		expect(isArtifactDownloading("Downloading")).toBe(true);
		expect(isArtifactDownloading("Ready")).toBe(false);
	});
});

describe("view mapping", () => {
	it("maps the nullable fields to null rather than leaving them undefined", () => {
		const view = toRuntimeStatusView({ phase: "Idle", isRunning: false, terminal: true, logStartSequence: 0, logLines: [] });

		expect(view.phase).toBe("Idle");
		expect(view.logLines).toEqual([]);
		expect(view.sanitizedError).toBeNull();
		expect(view.installed).toBeNull();
	});

	it("maps an installed runtime without losing the optional version fields", () => {
		const view = toRuntimeStatusView({
			phase: "Ready",
			isRunning: false,
			terminal: true,
			logStartSequence: 0,
			logLines: [],
			installed: {
				pythonVersion: "3.13.15",
				torchVersion: "2.11.0+cu128",
				uvVersion: "0.12.5",
				contractVersion: 1,
				installedAtUtc: 1_700_000_000_000,
			},
		});

		expect(view.installed?.pythonVersion).toBe("3.13.15");
		expect(view.installed?.torchVersion).toBe("2.11.0+cu128");
		expect(view.installed?.deviceName).toBeNull();
	});

	it("maps a base artifact with its license and progress", () => {
		const view = toBaseArtifactView({
			id: "a",
			repoId: "unsloth/Llama-3.2-1B-Instruct",
			revision: "main",
			status: "Downloading",
			totalBytes: 0,
			files: [],
			version: 1,
			createdAtUtc: 0,
			updatedAtUtc: 0,
			license: { repoId: "unsloth/Llama-3.2-1B-Instruct", license: "llama3.2", isGated: true, fetchedAtUtc: 1 },
			progress: { completedBytes: 5, totalBytes: 10, fileIndex: 1, fileCount: 3 },
		});

		expect(view.license?.license).toBe("llama3.2");
		expect(view.license?.isGated).toBe(true);
		expect(view.progress?.fileCount).toBe(3);
		expect(view.files).toEqual([]);
	});
});

describe("formatBytes", () => {
	it("renders whole bytes without a decimal and larger units with one", () => {
		expect(formatBytes(0)).toBe("0 B");
		expect(formatBytes(512)).toBe("512 B");
		expect(formatBytes(1536)).toBe("1.5 KB");
		expect(formatBytes(7_500_000_000)).toBe("7.0 GB");
	});
});
