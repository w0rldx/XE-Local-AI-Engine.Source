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

export type SourceBuildValidationIssue = "commit" | "repository" | "acknowledgement";

const commitPattern = /^[0-9a-fA-F]{40}$/;
const repositoryPattern = /^https:\/\/github\.com\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+(?:\.git)?$/;

export function sourceBuildValidationIssue(draft: SourceBuildDraft): SourceBuildValidationIssue | null {
	if (draft.commit.trim().length > 0 && !commitPattern.test(draft.commit.trim())) {
		return "commit";
	}
	if (draft.source === "custom") {
		if (!repositoryPattern.test(draft.repository.trim())) {
			return "repository";
		}
		if (!draft.acknowledgeCustomSourceRisk) {
			return "acknowledgement";
		}
	}
	return null;
}

export function sourceBuildValidationError(draft: SourceBuildDraft): string | null {
	const issue = sourceBuildValidationIssue(draft);
	if (issue === "commit") {
		return "Commit must be a full 40-character hexadecimal SHA.";
	}
	if (issue === "repository") {
		return "Use a canonical public GitHub HTTPS repository URL.";
	}
	if (issue === "acknowledgement") {
		return "Acknowledge that custom repository code executes with the app user's privileges.";
	}
	return null;
}

export function sourceBuildIdentity(descriptor: LlamaCppSourceBuildDescriptor | null | undefined): string | null {
	if (descriptor == null) {
		return null;
	}
	return [
		descriptor.backend,
		descriptor.source,
		descriptor.repository,
		descriptor.revisionMode,
		descriptor.requestedCommit ?? "",
	].join("|");
}

export function mergeSourceBuildLogs(persisted: readonly string[], live: readonly string[]): readonly string[] {
	if (live.length === 0) {
		return persisted;
	}
	const maxOverlap = Math.min(persisted.length, live.length);
	let overlap = 0;
	for (let length = maxOverlap; length > 0; length -= 1) {
		if (persisted.slice(-length).every((line, index) => line === live[index])) {
			overlap = length;
			break;
		}
	}
	return [...persisted, ...live.slice(overlap)];
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
