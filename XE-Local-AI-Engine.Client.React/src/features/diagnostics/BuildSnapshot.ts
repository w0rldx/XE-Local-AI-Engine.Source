// Snapshot bundler.
//
// `captureSnapshot` combines the buffer-derived `SnapshotInput` (from src/core/diagnostics) with a
// redacted, opted-in store-state map and the recorder's current rrweb segment (empty unless Developer Mode
// is recording), stamps `id`/`createdAt`/`schemaVersion`, persists via the SnapshotStore, and returns the
// full Snapshot. All redaction reuses the core diagnostics module's pure helpers so no secret/PII ever
// reaches IndexedDB.

import {
	buildSnapshotInput,
	generateId,
	onErrorRecorded,
	SCHEMA_VERSION,
	type Snapshot,
	type SnapshotError,
	type SnapshotKind,
	type SnapshotState,
} from "@/core/diagnostics/Diagnostics";
import { redactValue } from "@/core/diagnostics/Redact";
import { getRrwebSegment } from "@/core/diagnostics/RrwebRecorder";
import { saveSnapshot } from "@/features/diagnostics/SnapshotStore";

/** Reads a store's current state for inclusion in a snapshot. The result is redacted before persist. */
export type SnapshotStateProvider = () => Record<string, unknown>;

const stateProviders = new Map<string, SnapshotStateProvider>();

/**
 * Opt a store into snapshot capture under a stable name. Keep the returned state minimal — only the
 * fields useful for debugging. Returns an unregister function.
 */
export function registerSnapshotStateProvider(name: string, provider: SnapshotStateProvider): () => void {
	stateProviders.set(name, provider);
	return () => {
		stateProviders.delete(name);
	};
}

/** Gather the redacted state from every opted-in provider. */
function collectState(): SnapshotState {
	const state: Record<string, unknown> = {};
	for (const [name, provider] of stateProviders) {
		try {
			state[name] = redactValue(provider());
		} catch {
			state[name] = "[unavailable]";
		}
	}
	return state;
}

/**
 * Assemble, persist, and return a snapshot. `kind` is `error` for auto-capture and `manual` for the
 * "Report a problem" button. The rrweb segment is attached only while Developer Mode is recording.
 */
export async function captureSnapshot(kind: SnapshotKind, error?: SnapshotError): Promise<Snapshot> {
	const input = buildSnapshotInput(kind, error);
	const rrweb = getRrwebSegment();

	const snapshot: Snapshot = {
		...input,
		state: collectState(),
		...(rrweb.length > 0 ? { rrweb } : {}),
		id: generateId(),
		createdAt: Date.now(),
		schemaVersion: SCHEMA_VERSION,
	};

	await saveSnapshot(snapshot);
	return snapshot;
}

let autoCaptureTeardown: (() => void) | undefined;

/**
 * Auto-capture seam: subscribe to the core diagnostics module's deduped error recordings and capture one `error`
 * snapshot per logical error. Dedup already fired upstream (the listener only runs on a non-deduped
 * push), so there is no double capture. Idempotent; returns a teardown that unsubscribes.
 */
export function installAutoCapture(): () => void {
	if (autoCaptureTeardown) {
		return autoCaptureTeardown;
	}

	const unsubscribe = onErrorRecorded((crumb) => {
		captureSnapshot("error", crumb.error).catch(() => undefined);
	});

	autoCaptureTeardown = () => {
		unsubscribe();
		autoCaptureTeardown = undefined;
	};
	return autoCaptureTeardown;
}
