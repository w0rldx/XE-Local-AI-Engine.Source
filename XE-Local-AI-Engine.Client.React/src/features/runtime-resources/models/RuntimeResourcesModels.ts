import type {
	XeLocalAiEngineClientEndpointsModelFitV1RuntimeResidentKindDto,
	XeLocalAiEngineClientEndpointsModelFitV1RuntimeResidentStateDto,
	XeLocalAiEngineClientEndpointsModelFitV1RuntimeResidentsResponse,
	XeLocalAiEngineClientEndpointsModelFitV1RuntimeResourcesResponse,
	XeLocalAiEngineClientEndpointsTranscriptionV1TranscriptionBackendDto,
} from "@/core/api/generated";

// Whole-machine memory as the node samples it live: physical RAM plus one entry per NVIDIA GPU. The bars show the
// entire capacity, not what XE itself holds — other processes count as used too.
export interface MemoryUsage {
	readonly totalBytes: number;
	readonly usedBytes: number;
	readonly availableBytes: number;
}

export interface GpuMemoryUsage extends MemoryUsage {
	readonly index: number;
}

export interface RuntimeResources {
	readonly ram: MemoryUsage;
	// Empty means VRAM usage is UNKNOWN (non-NVIDIA machine or a failed probe), never zero.
	readonly gpus: readonly GpuMemoryUsage[];
}

// A bar at or above this share of its capacity switches to the warning colour.
export const HIGH_USAGE_PERCENT = 90;

export function toRuntimeResources(dto: XeLocalAiEngineClientEndpointsModelFitV1RuntimeResourcesResponse): RuntimeResources {
	return {
		ram: {
			totalBytes: dto.totalRamBytes,
			usedBytes: Math.max(0, dto.totalRamBytes - dto.availableRamBytes),
			availableBytes: dto.availableRamBytes,
		},
		gpus: dto.gpus.map((gpu) => ({
			index: gpu.index,
			totalBytes: gpu.totalVramBytes,
			usedBytes: gpu.usedVramBytes,
			availableBytes: gpu.availableVramBytes,
		})),
	};
}

/** Used share of the capacity as a 0-100 integer; 0 for a zero capacity so the bar never divides by zero. */
export function usagePercent(usage: MemoryUsage): number {
	return usage.totalBytes > 0 ? Math.min(100, Math.round((usage.usedBytes / usage.totalBytes) * 100)) : 0;
}

export type ResidentRuntime = XeLocalAiEngineClientEndpointsModelFitV1RuntimeResidentKindDto;
export type RuntimeResidentState = XeLocalAiEngineClientEndpointsModelFitV1RuntimeResidentStateDto;

// A stable-diffusion.cpp or whisper.cpp process the engine holds in memory. Both runtimes eject as a whole, so a row
// carries no role. `modelId` is null while the process is still starting (or whisper is switching models); `backend`
// is only ever set for transcription.
export interface RuntimeResident {
	readonly runtime: ResidentRuntime;
	readonly modelId: string | null;
	readonly state: RuntimeResidentState;
	readonly backend: XeLocalAiEngineClientEndpointsTranscriptionV1TranscriptionBackendDto | null;
	readonly canEject: boolean;
}

export function toRuntimeResidents(dto: XeLocalAiEngineClientEndpointsModelFitV1RuntimeResidentsResponse): RuntimeResident[] {
	return dto.items.map((item) => ({
		runtime: item.runtime,
		modelId: item.modelId ?? null,
		state: item.state,
		backend: item.backend ?? null,
		canEject: item.canEject,
	}));
}
