import { describe, expect, it } from "vitest";

import {
	differingEvidenceKeys,
	diffLaunchEvidence,
	flattenEvidence,
	formatEvidenceValue,
	launchEvidenceEntries,
} from "@/features/benchmarks/models/BenchmarkLaunchEvidence";
import type { BenchmarkEvidenceObject, BenchmarkLaunchFacts } from "@/features/benchmarks/models/BenchmarkModels";
import { noBenchmarkLaunchFacts } from "@/features/benchmarks/models/BenchmarkModels";

// The compare surface must make every launch difference visible rather than hiding distinct changes behind one opaque
// "the hashes differ" message. Each case below covers a difference that a hash comparison alone would conceal.

const facts = (overrides: Partial<BenchmarkLaunchFacts> = {}): BenchmarkLaunchFacts => ({
	...noBenchmarkLaunchFacts,
	variant: "cuda",
	kvCacheType: "q8_0",
	kvCacheTypeSource: "auto",
	flashAttentionMode: "on",
	effectiveBackend: "cuda",
	placementOffloaded: 32,
	placementTotal: 32,
	executableSha256: "a".repeat(64),
	hasAuxAssets: false,
	receiptHash: "r1",
	environmentFactsHash: "e1",
	...overrides,
});

const receipt = (overrides: Record<string, unknown> = {}): BenchmarkEvidenceObject => ({
	variant: "cuda",
	os: "linux",
	executableSha256: "a".repeat(64),
	launchProjection: { contextTokens: 4096, kvCacheTypeK: "q8_0", kvCacheTypeV: "q8_0", flashAttentionMode: "on" },
	auxAssets: { hasLora: false, hasMmproj: false, hasDraft: false },
	placement: { outcome: "Full", offloadedLayers: 32, totalLayers: 32 },
	...overrides,
});

const environment = (overrides: Record<string, unknown> = {}): BenchmarkEvidenceObject => ({
	runtimeBundle: { identity: "bundle-1", fileCount: 2, files: [{ name: "llama-server", sizeBytes: 10_485_760 }] },
	hardware: {
		osDescription: "Linux 6.18 WSL2",
		ramBytes: 68_719_476_736,
		gpus: [{ name: "RTX 5090", totalBytes: 34_359_738_368 }],
	},
	llamaRuntime: { version: "b10201", variant: "cuda" },
	missing: [],
	...overrides,
});

const diffOf = (
	left: [BenchmarkLaunchFacts, BenchmarkEvidenceObject | null, BenchmarkEvidenceObject | null],
	right: [BenchmarkLaunchFacts, BenchmarkEvidenceObject | null, BenchmarkEvidenceObject | null],
): string[] => differingEvidenceKeys(diffLaunchEvidence([launchEvidenceEntries(...left), launchEvidenceEntries(...right)]));

describe("flattenEvidence", () => {
	it("walks nested objects and arrays into dotted leaf paths", () => {
		const entries = flattenEvidence({ a: { b: 1 }, list: [{ name: "x" }, { name: "y" }] }, "receipt");

		expect(entries).toEqual([
			{ key: "receipt.a.b", value: 1 },
			{ key: "receipt.list.0.name", value: "x" },
			{ key: "receipt.list.1.name", value: "y" },
		]);
	});

	it("keeps an empty container as one absent-valued field so the other side can still differ from it", () => {
		expect(flattenEvidence({ missing: [], nested: {} }, "environment")).toEqual([
			{ key: "environment.missing", value: null },
			{ key: "environment.nested", value: null },
		]);
	});
});

describe("diffLaunchEvidence", () => {
	it("surfaces a difference confined to the executable hash", () => {
		const differing = diffOf(
			[facts(), receipt(), environment()],
			[facts({ executableSha256: "b".repeat(64) }), receipt({ executableSha256: "b".repeat(64) }), environment()],
		);

		expect(differing).toEqual(["launch.executableSha256", "receipt.executableSha256"]);
	});

	it("surfaces a difference confined to the effective context", () => {
		const differing = diffOf(
			[facts(), receipt(), environment()],
			[
				facts(),
				receipt({
					launchProjection: { contextTokens: 8192, kvCacheTypeK: "q8_0", kvCacheTypeV: "q8_0", flashAttentionMode: "on" },
				}),
				environment(),
			],
		);

		expect(differing).toEqual(["receipt.launchProjection.contextTokens"]);
	});

	it("surfaces a difference confined to layer placement", () => {
		const differing = diffOf(
			[facts(), receipt(), environment()],
			[
				facts({ placementOffloaded: 20 }),
				receipt({ placement: { outcome: "Partial", offloadedLayers: 20, totalLayers: 32 } }),
				environment(),
			],
		);

		expect(differing).toEqual(["launch.placementOffloaded", "receipt.placement.outcome", "receipt.placement.offloadedLayers"]);
	});

	it("surfaces a difference confined to an attached aux asset", () => {
		const differing = diffOf(
			[facts(), receipt(), environment()],
			[
				facts({ hasAuxAssets: true }),
				receipt({ auxAssets: { hasLora: true, hasMmproj: false, hasDraft: false } }),
				environment(),
			],
		);

		expect(differing).toEqual(["launch.hasAuxAssets", "receipt.auxAssets.hasLora"]);
	});

	it("surfaces a difference confined to the captured environment", () => {
		const differing = diffOf(
			[facts(), receipt(), environment()],
			[facts(), receipt(), environment({ llamaRuntime: { version: "b10300", variant: "cuda" } })],
		);

		expect(differing).toEqual(["environment.llamaRuntime.version"]);
	});

	it("reports a field only one side recorded, without dropping it", () => {
		const rows = diffLaunchEvidence([
			launchEvidenceEntries(facts(), receipt(), null),
			launchEvidenceEntries(facts(), receipt({ benchmarkLaunchPolicy: "benchmark" }), environment()),
		]);

		expect(
			rows.some((row) => row.key === "receipt.benchmarkLaunchPolicy" && row.values[0] === null && row.differs),
		).toBe(true);
		expect(rows.some((row) => row.key === "environment.llamaRuntime.version" && row.differs)).toBe(true);
	});

	// The capture timestamp differs on every run by construction; counting it would make every comparison "differ".
	it("renders the capture timestamp without counting it as a difference", () => {
		const rows = diffLaunchEvidence([
			launchEvidenceEntries(facts(), receipt(), environment({ capturedAtUtc: 1 })),
			launchEvidenceEntries(facts(), receipt(), environment({ capturedAtUtc: 2 })),
		]);
		const captured = rows.find((row) => row.key === "environment.capturedAtUtc");

		expect(captured).toMatchObject({ values: [1, 2], differs: false });
		expect(differingEvidenceKeys(rows)).toEqual([]);
	});

	it("reports no difference between two identical launches", () => {
		expect(diffOf([facts(), receipt(), environment()], [facts(), receipt(), environment()])).toEqual([]);
	});
});

describe("formatEvidenceValue", () => {
	it("renders an absent value as an em dash", () => {
		expect(formatEvidenceValue("receipt.os", null)).toBe("—");
		expect(formatEvidenceValue("receipt.os", undefined)).toBe("—");
	});

	it("humanizes byte counts and truncates hash-like values", () => {
		expect(formatEvidenceValue("environment.hardware.ramBytes", 68_719_476_736)).toBe("64.0 GB");
		expect(formatEvidenceValue("environment.runtimeBundle.files.0.sizeBytes", 10_485_760)).toBe("10.0 MB");
		expect(formatEvidenceValue("environment.runtimeBundle.files.0.sizeBytes", 512)).toBe("512 B");
		expect(formatEvidenceValue("receipt.executableSha256", "a".repeat(64))).toBe(`${"a".repeat(12)}…`);
		expect(formatEvidenceValue("receipt.launchProjection.contextTokens", 4096)).toBe("4096");
	});
});

// E2: the engine compares a SET, not a pair. A pairwise engine run N-1 times would report each row against whichever
// run happened to be first rather than against the set — which is the difference between "these three disagree" and
// "these two disagree with the one on the left".
describe("diffLaunchEvidence across more than two runs", () => {
	const side = (version: string, backend: string) =>
		launchEvidenceEntries(facts({ effectiveBackend: backend }), receipt(), environment({ llamaRuntime: { version } }));

	it("gives every side its own column, in the order asked for", () => {
		const rows = diffLaunchEvidence([side("b1", "cuda"), side("b2", "cuda"), side("b3", "cuda")]);
		const runtime = rows.find((row) => row.key === "environment.llamaRuntime.version");

		expect(runtime?.values).toEqual(["b1", "b2", "b3"]);
	});

	it("flags a field where any one of the sides disagrees, not only the first pair", () => {
		const rows = diffLaunchEvidence([side("b1", "cuda"), side("b1", "cuda"), side("b1", "vulkan")]);

		expect(differingEvidenceKeys(rows)).toContain("launch.effectiveBackend");
	});

	it("flags nothing when every side agrees", () => {
		expect(differingEvidenceKeys(diffLaunchEvidence([side("b1", "cuda"), side("b1", "cuda"), side("b1", "cuda")]))).toEqual(
			[],
		);
	});

	// A field only one run recorded is a real difference, and it must not shift the other runs' columns: column N is
	// side N whether or not side N carries the field.
	it("pads a side that never recorded a field rather than leaving a hole", () => {
		const rows = diffLaunchEvidence([
			launchEvidenceEntries(facts(), receipt(), null),
			launchEvidenceEntries(facts(), receipt(), environment()),
			launchEvidenceEntries(facts(), receipt(), environment()),
		]);
		const runtime = rows.find((row) => row.key === "environment.llamaRuntime.version");

		expect(runtime?.values).toHaveLength(3);
		expect(runtime?.values[0]).toBeNull();
		expect(runtime?.differs).toBe(true);
	});

	it("treats one side as a degenerate case: every row renders, nothing differs", () => {
		const rows = diffLaunchEvidence([side("b1", "cuda")]);

		expect(rows.length).toBeGreaterThan(0);
		expect(differingEvidenceKeys(rows)).toEqual([]);
	});
});
