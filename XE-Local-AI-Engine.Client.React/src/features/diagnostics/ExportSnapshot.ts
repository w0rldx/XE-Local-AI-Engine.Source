// Snapshot export/import.
//
// Export serializes a snapshot to JSON, zips it with fflate, and triggers a local file download.
// Import reverses that and validates the shape before handing a Snapshot back to the panel. Both
// directions are local-only — no network effect.

import { strFromU8, strToU8, unzipSync, zipSync } from "fflate";

import { SCHEMA_VERSION, type Snapshot } from "@/core/diagnostics/Diagnostics";

const ARCHIVE_ENTRY = "snapshot.json";

/** Serialize a snapshot to a zipped JSON archive (pure; reused by `exportSnapshot` and tests). */
export function serializeSnapshotZip(snapshot: Snapshot): Uint8Array {
	const json = JSON.stringify(snapshot);
	return zipSync({ [ARCHIVE_ENTRY]: strToU8(json) });
}

/** Parse + validate a zipped JSON archive back into a Snapshot. Throws on a malformed/unsupported archive. */
export function parseSnapshotZip(bytes: Uint8Array): Snapshot {
	const unzipped = unzipSync(bytes);
	const entry = unzipped[ARCHIVE_ENTRY] ?? Object.values(unzipped)[0];
	if (!entry) {
		throw new Error("Snapshot archive is empty.");
	}
	const parsed: unknown = JSON.parse(strFromU8(entry));
	return validateSnapshot(parsed);
}

/** Narrow an untrusted parsed value to a Snapshot, rejecting unsupported/incomplete shapes. */
function validateSnapshot(value: unknown): Snapshot {
	if (typeof value !== "object" || value === null) {
		throw new Error("Snapshot is not an object.");
	}
	const candidate = value as Record<string, unknown>;
	if (candidate["schemaVersion"] !== SCHEMA_VERSION) {
		throw new Error(`Unsupported snapshot schema version: ${String(candidate["schemaVersion"])}`);
	}
	const kind = candidate["kind"];
	const env = candidate["env"];
	const hasRequiredFields =
		typeof candidate["id"] === "string" &&
		typeof candidate["createdAt"] === "number" &&
		(kind === "error" || kind === "manual") &&
		Array.isArray(candidate["breadcrumbs"]) &&
		Array.isArray(candidate["network"]) &&
		typeof env === "object" &&
		env !== null;
	if (!hasRequiredFields) {
		throw new Error("Snapshot is missing required fields.");
	}
	return value as Snapshot;
}

/** Serialize a snapshot to a zip and trigger a local download. */
export function exportSnapshot(snapshot: Snapshot): void {
	const zipped = serializeSnapshotZip(snapshot);
	// Blob expects a BlobPart-typed view; copy into a fresh Uint8Array to satisfy the ArrayBuffer-backed type.
	const blob = new Blob([new Uint8Array(zipped)], { type: "application/zip" });
	const url = URL.createObjectURL(blob);
	try {
		const anchor = document.createElement("a");
		anchor.href = url;
		anchor.download = `xe-snapshot-${snapshot.id}-${snapshot.createdAt}.zip`;
		document.body.append(anchor);
		anchor.click();
		anchor.remove();
	} finally {
		URL.revokeObjectURL(url);
	}
}

/** Read a user-selected `.zip` file, validate it, and return the contained Snapshot. */
export async function importSnapshot(file: File): Promise<Snapshot> {
	const buffer = await file.arrayBuffer();
	return parseSnapshotZip(new Uint8Array(buffer));
}
