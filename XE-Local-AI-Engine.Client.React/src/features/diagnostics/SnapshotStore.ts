// Lane B: IndexedDB snapshot store (plan §6, §7.4).
//
// Local-only persistence for diagnostics snapshots. Retention (count + total bytes) is enforced
// INSIDE the same readwrite transaction as the write, so eviction is atomic and free of TOCTOU
// races on the byte cap. Nothing here ever leaves the browser (plan §3 invariant).

import { type DBSchema, type IDBPDatabase, type IDBPObjectStore, openDB } from "idb";

import type { Snapshot } from "@/core/diagnostics/Diagnostics";

const DB_NAME = "xe-diagnostics";
const STORE_NAME = "snapshots";
const DB_VERSION = 1;
const CREATED_AT_INDEX = "createdAt";

/** Hard cap on retained snapshots (plan §6). */
export const MAX_SNAPSHOTS = 25;

/** Total-bytes cap across all retained snapshots (plan §6). */
export const MAX_TOTAL_BYTES = 25 * 1024 * 1024;

interface DiagnosticsDb extends DBSchema {
	snapshots: {
		key: string;
		value: Snapshot;
		indexes: { createdAt: number };
	};
}

type SnapshotStoreHandle = IDBPObjectStore<DiagnosticsDb, ["snapshots"], "snapshots", "readwrite">;

let dbPromise: Promise<IDBPDatabase<DiagnosticsDb>> | undefined;
let persistenceRequested = false;

/** Listeners notified after any mutation so the TanStack Query cache can invalidate. */
type ChangeListener = () => void;
const changeListeners = new Set<ChangeListener>();

/** Subscribe to store mutations (save/delete/clear). Returns an unsubscribe function. */
export function subscribeSnapshots(listener: ChangeListener): () => void {
	changeListeners.add(listener);
	return () => {
		changeListeners.delete(listener);
	};
}

function emitChange(): void {
	for (const listener of changeListeners) {
		listener();
	}
}

function getDb(): Promise<IDBPDatabase<DiagnosticsDb>> {
	if (!dbPromise) {
		dbPromise = openDB<DiagnosticsDb>(DB_NAME, DB_VERSION, {
			upgrade(db) {
				const store = db.createObjectStore(STORE_NAME, { keyPath: "id" });
				store.createIndex(CREATED_AT_INDEX, "createdAt");
			},
		});
	}
	return dbPromise;
}

/** Ask the browser to keep this origin's storage persistent. Best-effort, requested once. */
async function requestPersistence(): Promise<void> {
	if (persistenceRequested) {
		return;
	}
	persistenceRequested = true;
	try {
		await globalThis.navigator?.storage?.persist?.();
	} catch {
		// Persistence is best-effort; ignore unavailable APIs or denied requests.
	}
}

/** Approximate the persisted size of a snapshot via its UTF-8 JSON byte length. */
function estimateBytes(snapshot: Snapshot): number {
	return new TextEncoder().encode(JSON.stringify(snapshot)).length;
}

/**
 * Evict oldest snapshots until both caps hold, inside the caller's transaction. Reads the index
 * ascending (oldest first) and deletes from the front, never dropping the sole remaining snapshot
 * even if it alone exceeds the byte cap (keeps the just-saved one).
 */
async function enforceRetention(store: SnapshotStoreHandle): Promise<void> {
	const ascending = await store.index(CREATED_AT_INDEX).getAll();
	let count = ascending.length;
	let totalBytes = ascending.reduce((sum, snapshot) => sum + estimateBytes(snapshot), 0);

	for (const victim of ascending) {
		if (count <= 1 || (count <= MAX_SNAPSHOTS && totalBytes <= MAX_TOTAL_BYTES)) {
			break;
		}
		// biome-ignore lint/performance/noAwaitInLoops: sequential deletes inside the one readwrite transaction keep eviction atomic (plan §6 — no TOCTOU on the byte cap).
		await store.delete(victim.id);
		count -= 1;
		totalBytes -= estimateBytes(victim);
	}
}

/** Persist a snapshot and enforce retention atomically, then notify subscribers. */
export async function saveSnapshot(snapshot: Snapshot): Promise<void> {
	await requestPersistence();
	const db = await getDb();
	const tx = db.transaction(STORE_NAME, "readwrite");
	await tx.store.put(snapshot);
	await enforceRetention(tx.store);
	await tx.done;
	emitChange();
}

/** All snapshots, newest first. */
export async function listSnapshots(): Promise<Snapshot[]> {
	const db = await getDb();
	const ascending = await db.getAllFromIndex(STORE_NAME, CREATED_AT_INDEX);
	return ascending.reverse();
}

/** A single snapshot by id, or undefined when absent. */
export async function getSnapshot(id: string): Promise<Snapshot | undefined> {
	const db = await getDb();
	return db.get(STORE_NAME, id);
}

/** Delete one snapshot, then notify subscribers. */
export async function deleteSnapshot(id: string): Promise<void> {
	const db = await getDb();
	await db.delete(STORE_NAME, id);
	emitChange();
}

/** Remove every snapshot, then notify subscribers. */
export async function clearSnapshots(): Promise<void> {
	const db = await getDb();
	await db.clear(STORE_NAME);
	emitChange();
}
