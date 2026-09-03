import type {
	XeLocalAiEngineClientEndpointsTrainingBaseArtifactsV1BaseArtifactResponse as BaseArtifactResponse,
	XeLocalAiEngineClientEndpointsTrainingRuntimeV1TrainingRuntimePrerequisitesResponse as PrerequisitesResponse,
	XeLocalAiEngineClientEndpointsTrainingRuntimeV1TrainingRuntimeStatusResponse as RuntimeStatusResponse,
} from "@/core/api/generated";

export interface TrainingRuntimePrerequisiteItemView {
	readonly key: string;
	readonly satisfied: boolean;
	readonly detail: string;
}

export interface TrainingRuntimePrerequisitesView {
	readonly canInstall: boolean;
	readonly items: readonly TrainingRuntimePrerequisiteItemView[];
}

export interface InstalledTrainingRuntimeView {
	readonly uvVersion: string;
	readonly pythonVersion: string;
	readonly contractVersion: number;
	readonly installedAtUtc: number;
	readonly torchVersion: string | null;
	readonly unslothVersion: string | null;
	readonly deviceName: string | null;
}

export interface TrainingRuntimeStatusView {
	readonly phase: string;
	readonly isRunning: boolean;
	readonly terminal: boolean;
	readonly logStartSequence: number;
	readonly logLines: readonly string[];
	readonly sanitizedError: string | null;
	readonly installed: InstalledTrainingRuntimeView | null;
}

export interface BaseArtifactFileView {
	readonly role: string;
	readonly fileName: string;
	readonly sizeBytes: number;
	readonly sha256: string | null;
}

export interface BaseArtifactLicenseView {
	readonly repoId: string;
	readonly license: string | null;
	readonly isGated: boolean;
	readonly fetchedAtUtc: number;
}

export interface BaseArtifactProgressView {
	readonly completedBytes: number;
	readonly totalBytes: number | null;
	readonly fileIndex: number;
	readonly fileCount: number;
}

export interface BaseArtifactView {
	readonly id: string;
	readonly repoId: string;
	readonly revision: string;
	readonly status: string;
	readonly totalBytes: number;
	readonly files: readonly BaseArtifactFileView[];
	readonly license: BaseArtifactLicenseView | null;
	readonly errorMessage: string | null;
	readonly progress: BaseArtifactProgressView | null;
}

export function toRuntimeStatusView(response: RuntimeStatusResponse): TrainingRuntimeStatusView {
	const installed = response.installed;
	return {
		phase: response.phase,
		isRunning: response.isRunning,
		terminal: response.terminal,
		logStartSequence: response.logStartSequence,
		logLines: response.logLines,
		sanitizedError: response.sanitizedError ?? null,
		installed:
			installed == null
				? null
				: {
						uvVersion: installed.uvVersion,
						pythonVersion: installed.pythonVersion,
						contractVersion: installed.contractVersion,
						installedAtUtc: installed.installedAtUtc,
						torchVersion: installed.torchVersion ?? null,
						unslothVersion: installed.unslothVersion ?? null,
						deviceName: installed.deviceName ?? null,
					},
	};
}

export function toPrerequisitesView(response: PrerequisitesResponse): TrainingRuntimePrerequisitesView {
	return {
		canInstall: response.canInstall,
		items: response.items.map((item) => ({
			key: item.key,
			satisfied: item.satisfied,
			detail: item.detail,
		})),
	};
}

export function toBaseArtifactView(response: BaseArtifactResponse): BaseArtifactView {
	const progress = response.progress;
	const license = response.license;
	return {
		id: response.id,
		repoId: response.repoId,
		revision: response.revision,
		status: response.status,
		totalBytes: response.totalBytes,
		files: response.files.map((file) => ({
			role: file.role,
			fileName: file.fileName,
			sizeBytes: file.sizeBytes,
			sha256: file.sha256 ?? null,
		})),
		license:
			license == null
				? null
				: {
						repoId: license.repoId,
						license: license.license ?? null,
						isGated: license.isGated,
						fetchedAtUtc: license.fetchedAtUtc,
					},
		errorMessage: response.errorMessage ?? null,
		progress:
			progress == null
				? null
				: {
						completedBytes: progress.completedBytes,
						totalBytes: progress.totalBytes ?? null,
						fileIndex: progress.fileIndex,
						fileCount: progress.fileCount,
					},
	};
}

export interface TrainingLogEntry {
	readonly sequence: number;
	readonly message: string;
}

export function trainingLogEntries(startSequence: number, logLines: readonly string[]): readonly TrainingLogEntry[] {
	return logLines.map((message, index) => ({ sequence: startSequence + index, message }));
}

const maxTrainingLogEntries = 2000;

/**
 * Merges log slices by sequence number. The install streams appended lines with a starting offset, so a reconnect
 * that replays an overlapping window must not duplicate them — keying on the sequence makes the merge idempotent.
 */
export function mergeTrainingLogs(...sources: readonly (readonly TrainingLogEntry[])[]): readonly TrainingLogEntry[] {
	const bySequence = new Map<number, string>();
	for (const source of sources) {
		for (const entry of source) {
			if (Number.isSafeInteger(entry.sequence) && entry.sequence >= 0) {
				bySequence.set(entry.sequence, entry.message);
			}
		}
	}
	const merged = [...bySequence].sort(([left], [right]) => left - right).map(([sequence, message]) => ({ sequence, message }));
	return merged.slice(Math.max(0, merged.length - maxTrainingLogEntries));
}

/** True while the runtime install is mid-flight — the phases between Idle/Ready/Failed. */
export function isRuntimeInstalling(phase: string): boolean {
	return phase === "AcquiringUv" || phase === "ProvisioningPython" || phase === "InstallingPackages" || phase === "Verifying";
}

export function isArtifactDownloading(status: string): boolean {
	return status === "Downloading";
}

/** Percentage complete, or null when the total is unknown — a bar that guesses is worse than one that admits it. */
export function downloadPercent(progress: BaseArtifactProgressView | null): number | null {
	if (progress == null || progress.totalBytes == null || progress.totalBytes <= 0) {
		return null;
	}
	return Math.min(100, Math.round((progress.completedBytes / progress.totalBytes) * 100));
}

export function formatBytes(bytes: number): string {
	if (bytes <= 0) {
		return "0 B";
	}
	const units = ["B", "KB", "MB", "GB", "TB"];
	const exponent = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)));
	const value = bytes / 1024 ** exponent;
	return `${value.toFixed(exponent === 0 ? 0 : 1)} ${units[exponent]}`;
}
