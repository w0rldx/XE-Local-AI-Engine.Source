import type {
	LlamaCppSourceBuildDescriptor,
	LlamaCppSourceBuildPrerequisiteItem,
	LlamaCppSourceBuildStatus,
	SourceBuildDraft,
} from "@/features/node-settings/models/SourceBuildModels";

/**
 * whisper.cpp has no Vulkan lane here — the prerequisite probe branches on CUDA alone — so this is a narrowing of the
 * llama.cpp/stable-diffusion.cpp backend union rather than a third copy of it. Every shared helper in
 * `SourceBuildModels` keeps working on it, because a narrower backend is still one of theirs.
 */
export type WhisperSourceBackend = "cpu" | "cuda";

// Wire-identical to the other two source-build lanes (the backends copy one shape per ADR 0012 D1), so the shared
// descriptor / prerequisite / status types are aliased rather than retyped. Only the backend union and the activity
// counters actually differ.
export type WhisperSourceBuildDescriptor = LlamaCppSourceBuildDescriptor;
export type WhisperSourceBuildPrerequisiteItem = LlamaCppSourceBuildPrerequisiteItem;
export type WhisperSourceBuildStatus = LlamaCppSourceBuildStatus;

export interface WhisperSourceBuildDraft extends SourceBuildDraft {
	readonly backend: WhisperSourceBackend;
}

export interface WhisperSourceBuildPrerequisites {
	readonly backend: WhisperSourceBackend;
	readonly canBuild: boolean;
	readonly items: readonly WhisperSourceBuildPrerequisiteItem[];
}

export type WhisperManagedRuntimeValidity = "active" | "invalid";

/**
 * What currently holds the transcription runtime. The first counter is named for transcriptions rather than jobs —
 * the image runtime's equivalent counts generation jobs — so the two cannot be aliased.
 */
export interface WhisperRuntimeActivity {
	readonly activeTranscriptionCount: number;
	readonly spawnReadinessCount: number;
	readonly residentProcessCount: number;
	readonly mutationReserved: boolean;
	readonly evictionReserved: boolean;
	readonly isBusy: boolean;
}

export interface WhisperManagedRuntime {
	readonly validity: WhisperManagedRuntimeValidity;
	readonly desiredBackend: WhisperSourceBackend;
	readonly sourceRepository: string;
	readonly sourceCommit: string;
	readonly sourceSelection: "official" | "custom";
	readonly sourceRevisionMode: "enginePinned" | "defaultBranch" | "explicitCommit";
	readonly sourceRequestedCommit: string | null;
	readonly installedAtUtc: number;
	readonly invalidReason: string | null;
}

export interface WhisperRuntimeStatus {
	readonly managedRuntime: WhisperManagedRuntime | null;
	readonly activity: WhisperRuntimeActivity;
}

export const idleWhisperRuntimeActivity: WhisperRuntimeActivity = {
	activeTranscriptionCount: 0,
	spawnReadinessCount: 0,
	residentProcessCount: 0,
	mutationReserved: false,
	evictionReserved: false,
	isBusy: false,
};

/**
 * Mirrors `WhisperRuntimeActivityGate.TryAcquireEvictionReservation`: an eject may tear down a resident daemon, which
 * is its whole point, but never one that is mid-transcription or mid-spawn.
 */
export function canEjectWhisperRuntime(activity: WhisperRuntimeActivity): boolean {
	return (
		activity.residentProcessCount > 0 &&
		activity.activeTranscriptionCount === 0 &&
		activity.spawnReadinessCount === 0 &&
		!activity.mutationReserved &&
		!activity.evictionReserved
	);
}
