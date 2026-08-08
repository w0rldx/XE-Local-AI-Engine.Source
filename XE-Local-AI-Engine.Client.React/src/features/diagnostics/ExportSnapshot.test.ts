import { describe, expect, it } from "vitest";

import { SCHEMA_VERSION, type Snapshot } from "@/core/diagnostics/Diagnostics";
import { importSnapshot, parseSnapshotZip, serializeSnapshotZip } from "@/features/diagnostics/ExportSnapshot";

const snapshot: Snapshot = {
	id: "abc-123",
	createdAt: 1_700_000_000_000,
	schemaVersion: SCHEMA_VERSION,
	kind: "error",
	error: { message: "boom", source: "uncaught", stack: "at x" },
	breadcrumbs: [{ id: "c1", timestamp: 1, category: "navigation", to: "/home" }],
	network: [{ transport: "axios", method: "GET", url: "/api/x", status: 500, traceId: "trace-1" }],
	env: { route: "/home", appVersion: "1.0.0", userAgent: "test", viewport: { width: 800, height: 600 }, locale: "en" },
	state: { developerMode: false },
};

describe("snapshot export/import round-trip", () => {
	it("yields an equal snapshot after serialize → parse", () => {
		const zipped = serializeSnapshotZip(snapshot);
		expect(parseSnapshotZip(zipped)).toEqual(snapshot);
	});

	it("re-imports a snapshot from a File", async () => {
		const zipped = serializeSnapshotZip(snapshot);
		const file = new File([new Uint8Array(zipped)], "xe-snapshot.zip", { type: "application/zip" });
		expect(await importSnapshot(file)).toEqual(snapshot);
	});

	it("rejects an unsupported schema version", () => {
		const zipped = serializeSnapshotZip({ ...snapshot, schemaVersion: SCHEMA_VERSION + 1 });
		expect(() => parseSnapshotZip(zipped)).toThrow(/schema version/i);
	});

	it("rejects an archive missing required fields", () => {
		const zipped = serializeSnapshotZip({ ...snapshot, id: 42 as unknown as string });
		expect(() => parseSnapshotZip(zipped)).toThrow(/required fields/i);
	});
});
