import "fake-indexeddb/auto";

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { recordError, SCHEMA_VERSION } from "@/core/diagnostics/Diagnostics";
import { REDACTED } from "@/core/diagnostics/Redact";
import { startRrwebRecording, stopRrwebRecording } from "@/core/diagnostics/RrwebRecorder";
import { captureSnapshot, installAutoCapture, registerSnapshotStateProvider } from "@/features/diagnostics/BuildSnapshot";
import { clearSnapshots, getSnapshot, listSnapshots, subscribeSnapshots } from "@/features/diagnostics/SnapshotStore";

// Capture the options rrweb's `record` is called with, without loading the real library (hermetic).
const { recordMock } = vi.hoisted(() => ({ recordMock: vi.fn() }));
vi.mock("rrweb", () => ({
	record: (options: unknown) => {
		recordMock(options);
		return () => undefined;
	},
}));

interface CapturedRecordOptions {
	emit: (event: unknown, isCheckout?: boolean) => void;
	packFn: (event: { type: number; data: unknown; timestamp: number }) => string;
}

beforeEach(() => {
	recordMock.mockClear();
	stopRrwebRecording();
	useDeveloperModeStore.setState({ developerMode: false });
	return clearSnapshots();
});
afterEach(() => {
	stopRrwebRecording();
	useDeveloperModeStore.setState({ developerMode: false });
	return clearSnapshots();
});

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

	it("attaches the recorder's rrweb segment while Developer Mode is recording, and nothing otherwise", async () => {
		const withoutRecording = await captureSnapshot("manual");
		expect(withoutRecording.rrweb).toBeUndefined();

		useDeveloperModeStore.setState({ developerMode: true });
		await startRrwebRecording();
		const options = recordMock.mock.calls.at(-1)?.[0] as CapturedRecordOptions | undefined;
		if (!options) {
			throw new Error("record was not called");
		}
		options.emit(options.packFn({ type: 2, data: { node: {} }, timestamp: Date.now() }), true);

		const snapshot = await captureSnapshot("manual");

		expect(snapshot.rrweb).toHaveLength(1);
		expect(await getSnapshot(snapshot.id)).toEqual(snapshot);
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
