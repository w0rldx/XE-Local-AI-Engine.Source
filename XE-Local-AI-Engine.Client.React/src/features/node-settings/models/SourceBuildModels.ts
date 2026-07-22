export type LlamaCppSourceBackend = "cpu" | "vulkan" | "cuda";
export type LlamaCppSourceSelection = "official" | "custom";
export type LlamaCppSourceRevisionMode = "enginePinned" | "defaultBranch" | "explicitCommit";

export const sourceBuildBackends: readonly LlamaCppSourceBackend[] = ["cpu", "vulkan", "cuda"];

export interface LlamaCppSourceBuildDescriptor {
	readonly buildId: string;
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
	readonly items: readonly LlamaCppSourceBuildPrerequisiteItem[];
}

export interface LlamaCppSourceBuildPrerequisiteItem {
	readonly key: string;
	readonly satisfied: boolean;
	readonly detail: string;
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
	return descriptor.buildId;
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

const diagnosticToolKeys = new Set(["cmake", "gcc", "g++", "git", "nvcc", "nvidia-smi", "glslc", "vulkaninfo"]);

/** Keeps command/version diagnostics without rendering the backend's English availability prose. */
export function sourceBuildPrerequisiteDiagnostic(item: LlamaCppSourceBuildPrerequisiteItem): string | null {
	if (item.key === "free-disk") {
		const sizes = item.detail.match(/\d+(?:[.,]\d+)? GB/g);
		return sizes && sizes.length >= 2 ? `${sizes[0]} / ${sizes[1]}` : null;
	}
	if (!item.satisfied) {
		return null;
	}
	if (item.key === "make-or-ninja") {
		return item.detail.startsWith("make ") ? "make" : item.detail.startsWith("ninja ") ? "ninja" : null;
	}
	if (!diagnosticToolKeys.has(item.key)) {
		return null;
	}
	const separator = item.detail.indexOf(": ");
	return separator >= 0 ? item.detail.slice(separator + 2).trim() || null : null;
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
