import "fake-indexeddb/auto";

import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { recordError, SCHEMA_VERSION } from "@/core/diagnostics/Diagnostics";
import { REDACTED } from "@/core/diagnostics/Redact";
import { captureSnapshot, installAutoCapture, registerSnapshotStateProvider } from "@/features/diagnostics/BuildSnapshot";
import { clearSnapshots, getSnapshot, listSnapshots, subscribeSnapshots } from "@/features/diagnostics/SnapshotStore";

beforeEach(() => clearSnapshots());
afterEach(() => clearSnapshots());

describe("captureSnapshot", () => {
	it("builds a valid persisted snapshot with redacted opted-in state", async () => {
		const unregister = registerSnapshotStateProvider("auth", () => ({ token: "secret-token", userId: "u1" }));

		const snapshot = await captureSnapshot("manual");

		expect(snapshot.id).toBeTruthy();
		expect(snapshot.createdAt).toBeGreaterThan(0);
		expect(snapshot.schemaVersion).toBe(SCHEMA_VERSION);
		expect(snapshot.kind).toBe("manual");
		expect(snapshot.error).toBeUndefined();

		const authState = snapshot.state?.["auth"] as Record<string, unknown> | undefined;
		expect(authState?.["token"]).toBe(REDACTED);
		expect(authState?.["userId"]).toBe("u1");

		// It is persisted and retrievable.
		expect(await getSnapshot(snapshot.id)).toEqual(snapshot);

		unregister();
	});

	it("includes the supplied error on an error capture", async () => {
		const snapshot = await captureSnapshot("error", { message: "boom", source: "uncaught", stack: "at x" });
		expect(snapshot.kind).toBe("error");
		expect(snapshot.error?.message).toBe("boom");
	});

	it("auto-captures one snapshot when an error is recorded", async () => {
		const teardown = installAutoCapture();
		const changed = new Promise<void>((resolve) => {
			const unsubscribe = subscribeSnapshots(() => {
				unsubscribe();
				resolve();
			});
		});

		recordError({ message: "auto-boom", source: "uncaught", stack: "at y\nat z" });
		await changed;

		const all = await listSnapshots();
		expect(all).toHaveLength(1);
		expect(all[0]?.kind).toBe("error");
		expect(all[0]?.error?.message).toBe("auto-boom");

		teardown();
	});
});
