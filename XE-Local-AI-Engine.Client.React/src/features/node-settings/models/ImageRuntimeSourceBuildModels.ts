import type {
	LlamaCppSourceBackend,
	LlamaCppSourceBuildDescriptor,
	LlamaCppSourceBuildPrerequisiteItem,
	LlamaCppSourceBuildPrerequisites,
	LlamaCppSourceBuildStatus,
	SourceBuildDraft,
} from "@/features/node-settings/models/SourceBuildModels";

export type ImageRuntimeSourceBackend = LlamaCppSourceBackend;
export type ImageRuntimeSourceBuildDescriptor = LlamaCppSourceBuildDescriptor;
export type ImageRuntimeSourceBuildPrerequisiteItem = LlamaCppSourceBuildPrerequisiteItem;
export type ImageRuntimeSourceBuildPrerequisites = LlamaCppSourceBuildPrerequisites;
export type ImageRuntimeSourceBuildStatus = LlamaCppSourceBuildStatus;
export type ImageRuntimeSourceBuildDraft = SourceBuildDraft;

export type ImageManagedRuntimeValidity = "active" | "invalid";

export interface ImageRuntimeActivity {
	readonly activeJobCount: number;
	readonly spawnReadinessCount: number;
	readonly residentProcessCount: number;
	readonly mutationReserved: boolean;
	readonly evictionReserved: boolean;
	readonly isBusy: boolean;
}

export interface ImageManagedRuntime {
	readonly validity: ImageManagedRuntimeValidity;
	readonly desiredBackend: ImageRuntimeSourceBackend;
	readonly sourceRepository: string;
	readonly sourceCommit: string;
	readonly sourceSelection: "official" | "custom";
	readonly sourceRevisionMode: "enginePinned" | "defaultBranch" | "explicitCommit";
	readonly sourceRequestedCommit: string | null;
	readonly installedAtUtc: number;
	readonly invalidReason: string | null;
}

export interface ImageRuntimeStatus {
	readonly managedRuntime: ImageManagedRuntime | null;
	readonly activity: ImageRuntimeActivity;
}

export const idleImageRuntimeActivity: ImageRuntimeActivity = {
	activeJobCount: 0,
	spawnReadinessCount: 0,
	residentProcessCount: 0,
	mutationReserved: false,
	evictionReserved: false,
	isBusy: false,
};

export function canEjectImageRuntime(activity: ImageRuntimeActivity): boolean {
	return (
		activity.residentProcessCount > 0 &&
		activity.activeJobCount === 0 &&
		activity.spawnReadinessCount === 0 &&
		!activity.mutationReserved &&
		!activity.evictionReserved
	);
}
