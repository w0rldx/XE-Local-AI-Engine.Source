import type {
	XeLocalAiEngineClientEndpointsRuntimeManagerV1RuntimeManagerStatusResponse,
	XeLocalAiEngineClientEndpointsRuntimeManagerV1RuntimeManifestContainerResponse,
	XeLocalAiEngineClientEndpointsRuntimeManagerV1RuntimeManifestResponse,
	XeLocalAiEngineHostAgentAbstractionsContractsHostAgentStatusDto,
	XeLocalAiEngineHostAgentAbstractionsContractsHostCapabilitiesDto,
	XeLocalAiEngineHostAgentAbstractionsContractsRuntimeComponentStatusDto,
} from "@/core/api/generated";
import type {
	HostAgentStatusDto,
	HostCapabilitiesDto,
	RuntimeComponentStatusDto,
	RuntimeManagerStatusViewModel,
	RuntimeManifestContainerDto,
	RuntimeManifestDto,
} from "@/features/runtime-manager/models/RuntimeManagerModel";

// Maps the generated (OpenAPI) runtime-manager status response to the stricter domain view-model the page depends
// on. The generated types are the single source of truth for the wire shape; their fields are all optional
// (`x?: T`), so each field coalesces to a required value with a sensible default. The generated enum unions share
// the SAME values as their string equivalents, so they map through unchanged. Only status / capabilities /
// components / manifest are surfaced — modelProviderHealth and models are not rendered by this page.

function toComponent(
	dto: XeLocalAiEngineHostAgentAbstractionsContractsRuntimeComponentStatusDto,
): RuntimeComponentStatusDto {
	return {
		name: dto.name ?? "",
		desiredState: dto.desiredState ?? "",
		health: dto.health ?? "Unknown",
		imageReference: dto.imageReference ?? "",
		digestVerified: dto.digestVerified ?? false,
		observedAt: dto.observedAt ?? "",
		diagnostics: dto.diagnostics ?? [],
	};
}

function toStatus(dto: XeLocalAiEngineHostAgentAbstractionsContractsHostAgentStatusDto | undefined): HostAgentStatusDto {
	return {
		state: dto?.state ?? "Unknown",
		desiredState: dto?.desiredState ?? "",
		runtimeLifecycle: dto?.runtimeLifecycle ?? "",
		bootstrapModelReady: dto?.bootstrapModelReady ?? false,
		webUiUrl: dto?.webUiUrl ?? "",
		observedAt: dto?.observedAt ?? "",
		diagnostics: dto?.diagnostics ?? [],
	};
}

function toCapabilities(
	dto: XeLocalAiEngineHostAgentAbstractionsContractsHostCapabilitiesDto | undefined,
): HostCapabilitiesDto {
	return {
		cpuAvailable: dto?.cpuAvailable ?? false,
		nvidiaGpuInference: dto?.nvidiaGpuInference ?? false,
		gpuRuntimeConfigured: dto?.gpuRuntimeConfigured ?? false,
		amdGpuStatus: dto?.amdGpuStatus ?? "",
		runtimeDiskBytes: dto?.runtimeDiskBytes ?? 0,
		observedAt: dto?.observedAt ?? "",
		diagnostics: dto?.diagnostics ?? [],
	};
}

function toManifestContainer(
	dto: XeLocalAiEngineClientEndpointsRuntimeManagerV1RuntimeManifestContainerResponse,
): RuntimeManifestContainerDto {
	return {
		name: dto.name ?? "",
		image: dto.image ?? "",
		network: dto.network ?? "",
		environment: (dto.environment ?? []).map((entry) => ({ name: entry.name ?? "", value: entry.value ?? "" })),
		volumes: (dto.volumes ?? []).map((volume) => ({
			source: volume.source ?? "",
			target: volume.target ?? "",
			readOnly: volume.readOnly ?? false,
		})),
	};
}

function toManifest(dto: XeLocalAiEngineClientEndpointsRuntimeManagerV1RuntimeManifestResponse | undefined): RuntimeManifestDto {
	return {
		available: dto?.available ?? false,
		schemaVersion: dto?.schemaVersion ?? null,
		runtimeMode: dto?.runtimeMode ?? "",
		bootstrapModel: dto?.bootstrapModel ?? "",
		defaultChatModel: dto?.defaultChatModel ?? "",
		maxRuntimeDiskGb: dto?.maxRuntimeDiskGb ?? null,
		stopDrainTimeoutSeconds: dto?.stopDrainTimeoutSeconds ?? null,
		containers: (dto?.containers ?? []).map(toManifestContainer),
		diagnostics: dto?.diagnostics ?? [],
	};
}

export function toRuntimeManagerStatusViewModel(
	dto: XeLocalAiEngineClientEndpointsRuntimeManagerV1RuntimeManagerStatusResponse,
): RuntimeManagerStatusViewModel {
	return {
		status: toStatus(dto.status),
		capabilities: toCapabilities(dto.capabilities),
		components: (dto.components ?? []).map(toComponent),
		manifest: toManifest(dto.manifest),
	};
}
