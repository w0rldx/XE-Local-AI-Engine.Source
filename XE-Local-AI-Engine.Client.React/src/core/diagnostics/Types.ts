// Shared local-only diagnostics contract (Lane A).
//
// This module is the single source of truth for the snapshot/breadcrumb shapes that the
// downstream lanes import:
//   - Lane B (IndexedDB store + snapshot bundler) consumes `Snapshot`, `SnapshotInput`, `Breadcrumb`.
//   - Lane C (Diagnostics UI panel + export/import) renders `Snapshot` / `NetworkEntry` / `Breadcrumb`.
//   - Lane D (rrweb replay) fills `Snapshot.rrweb`.
//
// Invariant (plan §3): everything in here is already REDACTED. Secrets/PII never reach these
// structures in cleartext — redaction runs at capture time (see `redact.ts`), not at export.

/** Bump when the persisted snapshot shape changes in a breaking way (Lane B migration trigger). */
export const SCHEMA_VERSION = 1;

/** Whether a snapshot was produced automatically by an error or manually by the user. */
export type SnapshotKind = "error" | "manual";

/** Which collector observed the error. Drives dedup attribution and UI labelling. */
export type ErrorSource = "boundary" | "uncaught" | "unhandledrejection" | "reporting" | "manual";

/** The three FE transports, each of which carries a W3C `traceparent` (plan §4). */
export type NetworkTransport = "axios" | "signalr" | "fetch";

export interface SnapshotError {
	readonly message: string;
	readonly stack?: string;
	/** React component stack (only present for `boundary` source). */
	readonly componentStack?: string;
	readonly source: ErrorSource;
}

/**
 * One observed network call. Bodies are intentionally absent from the contract — they are dropped
 * at capture (plan §10) so chat/message/agent payloads can never be persisted.
 */
export interface NetworkEntry {
	readonly transport: NetworkTransport;
	readonly method: string;
	/** Redacted URL — query tokens stripped (see `redactUrl`). */
	readonly url: string;
	readonly status?: number;
	readonly durationMs?: number;
	/** FE-generated W3C trace id (32 hex), the join key against backend logs. */
	readonly traceId?: string;
}

/** A single top-level store-state change recorded by the zustand middleware. */
export interface StateDiffField {
	readonly key: string;
	readonly from: unknown;
	readonly to: unknown;
}

export type BreadcrumbCategory = "navigation" | "network" | "console" | "error" | "state" | "lifecycle";

interface BreadcrumbBase {
	readonly id: string;
	/** epoch ms */
	readonly timestamp: number;
	readonly category: BreadcrumbCategory;
}

export interface NavigationBreadcrumb extends BreadcrumbBase {
	readonly category: "navigation";
	readonly from?: string;
	readonly to: string;
}

export interface NetworkBreadcrumb extends BreadcrumbBase {
	readonly category: "network";
	readonly entry: NetworkEntry;
}

export interface ConsoleBreadcrumb extends BreadcrumbBase {
	readonly category: "console";
	readonly level: "warn" | "error";
	readonly message: string;
	/** Redacted interpolated args. */
	readonly args?: readonly unknown[];
}

export interface ErrorBreadcrumb extends BreadcrumbBase {
	readonly category: "error";
	readonly error: SnapshotError;
}

export interface StateBreadcrumb extends BreadcrumbBase {
	readonly category: "state";
	readonly store: string;
	readonly action?: string;
	/** Redacted shallow diff of changed top-level keys. */
	readonly diff: readonly StateDiffField[];
}

export interface LifecycleBreadcrumb extends BreadcrumbBase {
	readonly category: "lifecycle";
	readonly message: string;
	readonly data?: Readonly<Record<string, unknown>>;
}

/** Ordered, redacted event recorded into the ring buffer. */
export type Breadcrumb =
	| NavigationBreadcrumb
	| NetworkBreadcrumb
	| ConsoleBreadcrumb
	| ErrorBreadcrumb
	| StateBreadcrumb
	| LifecycleBreadcrumb;

/** A breadcrumb as supplied by a collector — `id`/`timestamp` are stamped by the buffer on `push`. */
export type BreadcrumbInput =
	| Omit<NavigationBreadcrumb, "id" | "timestamp">
	| Omit<NetworkBreadcrumb, "id" | "timestamp">
	| Omit<ConsoleBreadcrumb, "id" | "timestamp">
	| Omit<ErrorBreadcrumb, "id" | "timestamp">
	| Omit<StateBreadcrumb, "id" | "timestamp">
	| Omit<LifecycleBreadcrumb, "id" | "timestamp">;

export interface SnapshotViewport {
	readonly width: number;
	readonly height: number;
}

export interface SnapshotEnv {
	readonly route: string;
	readonly appVersion: string;
	readonly userAgent: string;
	readonly viewport: SnapshotViewport;
	readonly locale: string;
}

/** Redacted store state captured at bundle time (Lane B fills the values). */
export type SnapshotState = Readonly<Record<string, unknown>>;

/** A single packed rrweb event (rrweb `pack()` returns a string). Lane D owns the contents. */
export type RrwebPackedEvent = string;

/**
 * Everything a collector/buffer can supply for a snapshot. Lane B's bundler combines this with a
 * generated `id`/`createdAt`/`schemaVersion` to produce a persisted {@link Snapshot}.
 */
export interface SnapshotInput {
	readonly kind: SnapshotKind;
	readonly error?: SnapshotError;
	readonly breadcrumbs: readonly Breadcrumb[];
	readonly network: readonly NetworkEntry[];
	readonly env: SnapshotEnv;
	readonly state?: SnapshotState;
	readonly rrweb?: readonly RrwebPackedEvent[];
}

/** A persisted diagnostics snapshot (IndexedDB `keyPath: "id"`, plan §6). */
export interface Snapshot extends SnapshotInput {
	readonly id: string;
	readonly createdAt: number;
	readonly schemaVersion: number;
}
