import type {
	RuntimeComponentStatusDto,
	RuntimeContainerActionName,
	RuntimeLogLineDto,
	RuntimeManifestDto,
} from "@/features/runtime-manager/api/RuntimeManagerApi";

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
