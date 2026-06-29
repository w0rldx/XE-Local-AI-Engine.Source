// Public surface of the local-only diagnostics core (Lane A).
//
// Lane B will consume: the `types.ts` contract (Snapshot/SnapshotInput/Breadcrumb/NetworkEntry),
// `getBreadcrumbs()` (read the ring on capture), `clearBreadcrumbs()`, `buildSnapshotInput()`
// (buffer + env → SnapshotInput; the bundler adds state + rrweb), and `SCHEMA_VERSION`.
// Lane C consumes the same contract + types for the panel. Lane D fills `Snapshot.rrweb`.

// Buffer accessors (Lane B reads these on capture).
export { clear as clearBreadcrumbs, getAll as getBreadcrumbs, push as pushBreadcrumb } from "@/core/diagnostics/BreadcrumbBuffer";
export { onAppError, rootErrorHandlers } from "@/core/diagnostics/collectors/ReactErrors";
export { withDiagnostics } from "@/core/diagnostics/collectors/Zustand";
export { collectEnv } from "@/core/diagnostics/Env";
export { generateId } from "@/core/diagnostics/Ids";
// Bootstrap + React error wiring (used by Main.tsx / App.tsx).
export { installCollectors } from "@/core/diagnostics/InstallCollectors";
export type { ErrorRecordedListener } from "@/core/diagnostics/RecordError";
// Manual-capture / cross-cutting helpers.
export { onErrorRecorded, recordError } from "@/core/diagnostics/RecordError";
// rrweb DOM-replay segment (Lane D) — Lane B's bundler calls getRrwebSegment() to attach it.
export { getRrwebSegment, startRrwebRecording, stopRrwebRecording } from "@/core/diagnostics/RrwebRecorder";
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
