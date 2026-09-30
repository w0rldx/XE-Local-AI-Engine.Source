// Public surface of the local-only diagnostics core, consumed by the snapshot bundler and the diagnostics panel:
// the `Types` contract (Snapshot/SnapshotInput/Breadcrumb/NetworkEntry), `buildSnapshotInput()` (buffer + env →
// SnapshotInput; the bundler adds state + rrweb), and `SCHEMA_VERSION`.

export { onAppError, rootErrorHandlers } from "@/core/diagnostics/collectors/ReactErrors";
export { generateId } from "@/core/diagnostics/Ids";
// Bootstrap + React error wiring (used by Main.tsx / App.tsx).
export { installCollectors } from "@/core/diagnostics/InstallCollectors";
export type { ErrorRecordedListener } from "@/core/diagnostics/RecordError";
// Manual-capture / cross-cutting helpers.
export { onErrorRecorded, recordError } from "@/core/diagnostics/RecordError";
// Snapshot assembly seam.
export { buildSnapshotInput } from "@/core/diagnostics/SnapshotInput";
export type {
	Breadcrumb,
	BreadcrumbCategory,
	BreadcrumbInput,
	ConsoleBreadcrumb,
	ErrorBreadcrumb,
	ErrorSource,
	LifecycleBreadcrumb,
	NavigationBreadcrumb,
	NetworkBreadcrumb,
	NetworkEntry,
	NetworkTransport,
	RrwebPackedEvent,
	Snapshot,
	SnapshotEnv,
	SnapshotError,
	SnapshotInput,
	SnapshotKind,
	SnapshotState,
	SnapshotViewport,
	StateBreadcrumb,
	StateDiffField,
} from "@/core/diagnostics/Types";
export { SCHEMA_VERSION } from "@/core/diagnostics/Types";
