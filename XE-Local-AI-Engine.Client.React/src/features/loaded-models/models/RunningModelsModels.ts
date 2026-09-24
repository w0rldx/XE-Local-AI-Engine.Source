import type {
	XeLocalAiEngineClientEndpointsModelFitV1EjectRunningModelResponse,
	XeLocalAiEngineClientEndpointsModelFitV1RunningModelResponse,
} from "@/core/api/generated";

// Domain view-model for one running (loaded) model the llama.cpp server-process supervisor reports, with a role
// (chat/embedding), a liveness flag, and a free-form detail. Relocated from the model-fit advisor to the Loaded
// Models page. isResponsive + detail surface liveness for the eject UI.
export interface RunningModel {
	readonly modelName: string;
	readonly role: string;
	readonly isResponsive: boolean;
	readonly detail: string;
	// Stable code for `detail` the panel translates; null for a value this build does not know (render `detail` then).
	readonly detailCode: RunningModelDetailCode | null;
}

const runningModelDetailCodes = ["responsive", "unresponsive", "exited"] as const;
export type RunningModelDetailCode = (typeof runningModelDetailCodes)[number];

function toDetailCode(value: string | null | undefined): RunningModelDetailCode | null {
	return (runningModelDetailCodes as readonly string[]).includes(value ?? "") ? (value as RunningModelDetailCode) : null;
}

// Maps the generated (OpenAPI) running-model response to the stricter domain view-model. The generated fields are all
// optional (`x?: T`), so the mapper coalesces every field to a required value with a safe default.
export function toRunningModel(dto: XeLocalAiEngineClientEndpointsModelFitV1RunningModelResponse): RunningModel {
	return {
		modelName: dto.modelName ?? "",
		role: dto.role ?? "",
		isResponsive: dto.isResponsive ?? false,
		detail: dto.detail ?? "",
		detailCode: toDetailCode(dto.detailCode),
	};
}

// What a graceful eject actually did. Mirrors the backend LlamaServerEjectOutcome wire values: "ejected"
// (idle/drained cleanly), "timed_out_still_busy" (in-flight work did not drain and no force was set — left running),
// "forced" (torn down despite in-flight work), "not_running" (nothing loaded — idempotent no-op).
export const ejectRunningModelOutcomes = ["ejected", "timed_out_still_busy", "forced", "not_running"] as const;
export type EjectRunningModelOutcome = (typeof ejectRunningModelOutcomes)[number];

export interface EjectRunningModelResult {
	readonly modelName: string;
	readonly role: string;
	readonly outcome: EjectRunningModelOutcome;
}

// Narrows the generated `outcome` string to the domain union with a runtime guard, so an unrecognised value degrades to
// a safe "ejected" rather than smuggling an out-of-union value into the toast switch.
function toEjectOutcome(value: string | undefined): EjectRunningModelOutcome {
	return (ejectRunningModelOutcomes as readonly string[]).includes(value ?? "") ? (value as EjectRunningModelOutcome) : "ejected";
}

export function toEjectRunningModelResult(
	dto: XeLocalAiEngineClientEndpointsModelFitV1EjectRunningModelResponse | undefined,
): EjectRunningModelResult {
	return {
		modelName: dto?.modelName ?? "",
		role: dto?.role ?? "",
		outcome: toEjectOutcome(dto?.outcome),
	};
}
