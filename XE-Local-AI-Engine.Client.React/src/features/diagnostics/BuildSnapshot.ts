// Lane B: snapshot bundler (plan §7.4).
//
// `captureSnapshot` combines the Lane A buffer-derived `SnapshotInput` with a redacted, opted-in
// store-state map and (Lane D) an optional rrweb segment, stamps `id`/`createdAt`/`schemaVersion`,
// persists via the SnapshotStore, and returns the full Snapshot. All redaction reuses Lane A's
// pure helpers so no secret/PII ever reaches IndexedDB (plan §3, §10).

import {
	buildSnapshotInput,
	generateId,
	onErrorRecorded,
	type RrwebPackedEvent,
	SCHEMA_VERSION,
	type Snapshot,
	type SnapshotError,
	type SnapshotKind,
	type SnapshotState,
} from "@/core/diagnostics/Diagnostics";
import { redactValue } from "@/core/diagnostics/Redact";
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
 * Lane D hook: supplies the latest packed rrweb segment (Developer Mode only). Left undefined here;
 * Lane D registers a provider so this module stays free of any rrweb import.
 */
type RrwebProvider = () => readonly RrwebPackedEvent[] | undefined;
let rrwebProvider: RrwebProvider | undefined;

/** Register the rrweb segment provider (Lane D). Returns an unregister function. */
export function registerRrwebProvider(provider: RrwebProvider): () => void {
	rrwebProvider = provider;
	return () => {
		rrwebProvider = undefined;
	};
}

export interface CaptureOptions {
	/** Inject a packed rrweb segment directly (overrides any registered provider). */
	readonly rrweb?: readonly RrwebPackedEvent[];
}

/**
 * Assemble, persist, and return a snapshot. `kind` is `error` for auto-capture and `manual` for the
 * "Report a problem" button. An optional rrweb segment comes from `options` or the Lane D provider.
 */
export async function captureSnapshot(kind: SnapshotKind, error?: SnapshotError, options?: CaptureOptions): Promise<Snapshot> {
	const input = buildSnapshotInput(kind, error);
	const rrweb = options?.rrweb ?? rrwebProvider?.();

	const snapshot: Snapshot = {
		...input,
		state: collectState(),
		...(rrweb && rrweb.length > 0 ? { rrweb } : {}),
		id: generateId(),
		createdAt: Date.now(),
		schemaVersion: SCHEMA_VERSION,
	};

	await saveSnapshot(snapshot);
	return snapshot;
}

let autoCaptureTeardown: (() => void) | undefined;

/**
 * Auto-capture seam: subscribe to Lane A's deduped error recordings and capture one `error`
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
