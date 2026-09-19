export type LlamaCppSourceBackend = "cpu" | "vulkan" | "cuda";
export type LlamaCppSourceSelection = "official" | "custom";
export type LlamaCppSourceRevisionMode = "enginePinned" | "defaultBranch" | "explicitCommit";

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
	readonly logStartSequence: number;
	readonly logLines: readonly string[];
	readonly sanitizedError: string | null;
	readonly currentBuild: LlamaCppSourceBuildDescriptor | null;
}

export interface SourceBuildLogEntry {
	readonly sequence: number;
	readonly message: string;
}

export interface SourceBuildDraft {
	readonly backend: LlamaCppSourceBackend;
	readonly source: LlamaCppSourceSelection;
	readonly repository: string;
	readonly commit: string;
	readonly acknowledgeCustomSourceRisk: boolean;
}

/**
 * How long a toolchain probe answer stays fresh, shared by all three source-build lanes.
 *
 * Each `GET .../source-build/prerequisites` really runs the tools: the node spawns `cmake --version`,
 * `gcc`, `g++`, `ninja`/`make`, `git` (and `readelf` for whisper, `nvcc`/`nvidia-smi` or `glslc`/`vulkaninfo` for an
 * accelerated backend) one after another, because "on PATH" and "runs" differ often enough to matter. That is ~6-9
 * child processes per card, and since the three cards render unconditionally on Node Settings, a zero staleTime made
 * every visit to that page re-run all of them. A toolchain changes when the operator installs something, not while
 * they navigate, so five minutes is both generous and a real bound; a build's own start path re-probes server-side
 * regardless, so a stale "can build" answer here can never let an unbuildable request through.
 */
export const sourceBuildPrerequisiteStaleTime = 5 * 60_000;

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

export function sourceBuildLogEntries(startSequence: number, logLines: readonly string[]): readonly SourceBuildLogEntry[] {
	return logLines.map((message, index) => ({ sequence: startSequence + index, message }));
}

const maxSourceBuildLogEntries = 2000;

export function mergeSourceBuildLogs(...sources: readonly (readonly SourceBuildLogEntry[])[]): readonly SourceBuildLogEntry[] {
	const bySequence = new Map<number, string>();
	for (const source of sources) {
		for (const entry of source) {
			if (Number.isSafeInteger(entry.sequence) && entry.sequence >= 0) {
				bySequence.set(entry.sequence, entry.message);
			}
		}
	}
	const merged = [...bySequence].sort(([left], [right]) => left - right).map(([sequence, message]) => ({ sequence, message }));
	return merged.slice(Math.max(0, merged.length - maxSourceBuildLogEntries));
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

// Generic in the backend so a lane with a narrower one — whisper.cpp builds CPU and CUDA only — gets a request whose
// backend still matches its own generated DTO instead of being widened to the three-way llama.cpp union.
export function sourceBuildRequest<TBackend extends LlamaCppSourceBackend>(
	draft: Omit<SourceBuildDraft, "backend"> & { readonly backend: TBackend },
) {
	return {
		backend: draft.backend,
		source: draft.source,
		repository: draft.source === "custom" ? draft.repository.trim() : null,
		commit: draft.commit.trim().length > 0 ? draft.commit.trim().toLowerCase() : null,
		acknowledgeCustomSourceRisk: draft.source === "custom" && draft.acknowledgeCustomSourceRisk,
	};
}
