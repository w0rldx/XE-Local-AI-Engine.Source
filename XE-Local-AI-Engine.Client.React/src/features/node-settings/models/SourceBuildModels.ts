export type LlamaCppSourceBackend = "cpu" | "vulkan" | "cuda";
export type LlamaCppSourceSelection = "official" | "custom";
export type LlamaCppSourceRevisionMode = "enginePinned" | "defaultBranch" | "explicitCommit";

export const sourceBuildBackends: readonly LlamaCppSourceBackend[] = ["cpu", "vulkan", "cuda"];

export interface LlamaCppSourceBuildDescriptor {
	readonly backend: LlamaCppSourceBackend;
	readonly source: LlamaCppSourceSelection;
	readonly repository: string;
	readonly revisionMode: LlamaCppSourceRevisionMode;
	readonly requestedCommit: string | null;
	readonly resolvedCommit: string | null;
}

export interface LlamaCppSourceBuildPrerequisites {
	readonly backend: LlamaCppSourceBackend;
	readonly canBuild: boolean;
	readonly items: readonly { readonly key: string; readonly satisfied: boolean; readonly detail: string }[];
}

export interface LlamaCppSourceBuildStatus {
	readonly phase: string;
	readonly isRunning: boolean;
	readonly terminal: boolean;
	readonly logLines: readonly string[];
	readonly sanitizedError: string | null;
	readonly currentBuild: LlamaCppSourceBuildDescriptor | null;
}

export interface SourceBuildDraft {
	readonly backend: LlamaCppSourceBackend;
	readonly source: LlamaCppSourceSelection;
	readonly repository: string;
	readonly commit: string;
	readonly acknowledgeCustomSourceRisk: boolean;
}

const commitPattern = /^[0-9a-fA-F]{40}$/;
const repositoryPattern = /^https:\/\/github\.com\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+(?:\.git)?$/;

export function sourceBuildValidationError(draft: SourceBuildDraft): string | null {
	if (draft.commit.trim().length > 0 && !commitPattern.test(draft.commit.trim())) {
		return "Commit must be a full 40-character hexadecimal SHA.";
	}
	if (draft.source === "custom") {
		if (!repositoryPattern.test(draft.repository.trim())) {
			return "Use a canonical public GitHub HTTPS repository URL.";
		}
		if (!draft.acknowledgeCustomSourceRisk) {
			return "Acknowledge that custom repository code executes with the app user's privileges.";
		}
	}
	return null;
}

export function sourceBuildRequest(draft: SourceBuildDraft) {
	return {
		backend: draft.backend,
		source: draft.source,
		repository: draft.source === "custom" ? draft.repository.trim() : null,
		commit: draft.commit.trim().length > 0 ? draft.commit.trim().toLowerCase() : null,
		acknowledgeCustomSourceRisk: draft.source === "custom" && draft.acknowledgeCustomSourceRisk,
	};
}
