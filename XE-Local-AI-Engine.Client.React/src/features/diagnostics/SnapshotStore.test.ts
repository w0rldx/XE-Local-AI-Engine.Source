import "fake-indexeddb/auto";

import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { SCHEMA_VERSION, type Snapshot } from "@/core/diagnostics/Diagnostics";
import {
	clearSnapshots,
	getSnapshot,
	listSnapshots,
	MAX_SNAPSHOTS,
	MAX_TOTAL_BYTES,
	saveSnapshot,
} from "@/features/diagnostics/SnapshotStore";

function makeSnapshot(id: string, createdAt: number, padBytes = 0): Snapshot {
	return {
		id,
		createdAt,
		schemaVersion: SCHEMA_VERSION,
		kind: "manual",
		breadcrumbs: [],
		network: [],
		env: { route: "/", appVersion: "test", userAgent: "test", viewport: { width: 1, height: 1 }, locale: "en" },
		...(padBytes > 0 ? { state: { pad: "x".repeat(padBytes) } } : {}),
	};
}

function totalBytes(snapshots: readonly Snapshot[]): number {
	return snapshots.reduce((sum, snapshot) => sum + new TextEncoder().encode(JSON.stringify(snapshot)).length, 0);
}

beforeEach(() => clearSnapshots());
afterEach(() => clearSnapshots());

describe("snapshot store retention", () => {
	it("evicts the oldest snapshots once over the count cap", async () => {
		const overflow = 5;
		for (let index = 0; index < MAX_SNAPSHOTS + overflow; index += 1) {
			// biome-ignore lint/performance/noAwaitInLoops: writes must be ordered so createdAt is strictly increasing for the eviction assertion.
			await saveSnapshot(makeSnapshot(`id-${index}`, 1000 + index));
		}

		const all = await listSnapshots();
		expect(all).toHaveLength(MAX_SNAPSHOTS);
		// newest first; the `overflow` oldest were evicted.
		expect(all[0]?.id).toBe(`id-${MAX_SNAPSHOTS + overflow - 1}`);
		expect(await getSnapshot("id-0")).toBeUndefined();
		expect(await getSnapshot(`id-${overflow}`)).toBeDefined();
	});

	it("evicts the oldest snapshots once over the byte cap, in one transaction, keeping the newest", async () => {
		const bigPad = 12 * 1024 * 1024; // ~12MB each → three exceeds the 25MB cap.
		await saveSnapshot(makeSnapshot("big-0", 2000, bigPad));
		await saveSnapshot(makeSnapshot("big-1", 2001, bigPad));
		await saveSnapshot(makeSnapshot("big-2", 2002, bigPad));

		const all = await listSnapshots();
		expect(totalBytes(all)).toBeLessThanOrEqual(MAX_TOTAL_BYTES);
		// The just-saved snapshot is always retained; the oldest is evicted to fit the cap.
		expect(all.map((snapshot) => snapshot.id)).toContain("big-2");
		expect(await getSnapshot("big-0")).toBeUndefined();
	});

	it("round-trips a single snapshot through get/list", async () => {
		const snapshot = makeSnapshot("solo", 3000);
		await saveSnapshot(snapshot);
		expect(await getSnapshot("solo")).toEqual(snapshot);
		expect(await listSnapshots()).toHaveLength(1);
	});
});
