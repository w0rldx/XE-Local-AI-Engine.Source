import type { RuntimeLogLineDto } from "@/features/runtime-manager/api/RuntimeLogStream";

export type RuntimeContainerActionName = "start" | "stop" | "restart";

// Stricter domain view-models the runtime-manager page renders. The generated status response has all-optional
// fields (`x?: T`); RuntimeManagerMappers coalesces every field to a required value so the page never null-checks
// the wire shape.
export interface RuntimeComponentStatusDto {
	name: string;
	desiredState: string;
	health: string;
	imageReference: string;
	digestVerified: boolean;
	observedAt: string;
	diagnostics: string[];
}

export interface HostAgentStatusDto {
	state: string;
	desiredState: string;
	runtimeLifecycle: string;
	bootstrapModelReady: boolean;
	webUiUrl: string;
	observedAt: string;
	diagnostics: string[];
}

export interface HostCapabilitiesDto {
	cpuAvailable: boolean;
	nvidiaGpuInference: boolean;
	gpuRuntimeConfigured: boolean;
	amdGpuStatus: string;
	runtimeDiskBytes: number;
	observedAt: string;
	diagnostics: string[];
}

export interface RuntimeManifestEnvironmentDto {
	name: string;
	value: string;
}

export interface RuntimeManifestVolumeDto {
	source: string;
	target: string;
	readOnly: boolean;
}

export interface RuntimeManifestContainerDto {
	name: string;
	image: string;
	network: string;
	environment: RuntimeManifestEnvironmentDto[];
	volumes: RuntimeManifestVolumeDto[];
}

export interface RuntimeManifestDto {
	available: boolean;
	schemaVersion: number | null;
	runtimeMode: string;
	bootstrapModel: string;
	defaultChatModel: string;
	maxRuntimeDiskGb: number | null;
	stopDrainTimeoutSeconds: number | null;
	containers: RuntimeManifestContainerDto[];
	diagnostics: string[];
}

export interface RuntimeManagerStatusViewModel {
	status: HostAgentStatusDto;
	capabilities: HostCapabilitiesDto;
	components: RuntimeComponentStatusDto[];
	manifest: RuntimeManifestDto;
}

export const runtimeEmptyValue = "—";

export function formatRuntimeBoolean(value: boolean): string {
	return value ? "Yes" : "No";
}

export function formatRuntimeText(value: string | null | undefined): string {
	return value?.trim() || runtimeEmptyValue;
}

export function formatRuntimeTimestamp(value: string | null | undefined): string {
	if (!value) {
		return "Not reported";
	}

	const date = new Date(value);
	if (Number.isNaN(date.getTime()) || date.getTime() === 0) {
		return "Not reported";
	}

	return date.toLocaleString();
}

export function formatRuntimeBytes(bytes: number | null | undefined): string {
	if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes < 0) {
		return runtimeEmptyValue;
	}

	if (bytes >= 1_073_741_824) {
		return `${(bytes / 1_073_741_824).toFixed(1)} GB`;
	}

	if (bytes >= 1_048_576) {
		return `${(bytes / 1_048_576).toFixed(1)} MB`;
	}

	if (bytes >= 1024) {
		return `${(bytes / 1024).toFixed(1)} KB`;
	}

	return `${bytes} B`;
}

export function getRuntimeStatusColor(state: string | undefined): "green" | "red" | "yellow" | "gray" {
	switch (state) {
		case "Running":
			return "green";
		case "Failed":
			return "red";
		case "Starting":
		case "Stopping":
		case "Degraded":
			return "yellow";
		default:
			return "gray";
	}
}

export function getComponentHealthColor(health: string | undefined): "green" | "red" | "yellow" | "gray" {
	switch (health) {
		case "Healthy":
			return "green";
		case "Unhealthy":
			return "red";
		case "Starting":
			return "yellow";
		default:
			return "gray";
	}
}

export function sortRuntimeComponents(components: RuntimeComponentStatusDto[]): RuntimeComponentStatusDto[] {
	return components.toSorted((left, right) => left.name.localeCompare(right.name));
}

export function manifestSummary(manifest: RuntimeManifestDto): string {
	if (!manifest.available) {
		return "Runtime manifest unavailable";
	}

	return `${manifest.runtimeMode} runtime · ${manifest.containers.length} container${manifest.containers.length === 1 ? "" : "s"}`;
}

export function runtimeContainerActionLabel(action: RuntimeContainerActionName): string {
	switch (action) {
		case "start":
			return "Start";
		case "stop":
			return "Stop";
		case "restart":
			return "Restart";
		default:
			return action;
	}
}

export function formatRuntimeLogLine(line: RuntimeLogLineDto): string {
	return `[${formatRuntimeTimestamp(line.observedAt)}] ${line.containerName}/${line.stream}: ${line.line}`;
}
