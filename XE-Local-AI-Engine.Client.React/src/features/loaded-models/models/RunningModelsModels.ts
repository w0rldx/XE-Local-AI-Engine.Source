import type { XeLocalAiEngineClientEndpointsModelFitV1RunningModelResponse } from "@/core/api/generated";

// Domain view-model for one running (loaded) model the llama.cpp server-process supervisor reports. This is a
// DIFFERENT runtime from the Ollama in-memory list (LoadedModel): it lists llama.cpp server processes, with a
// role (chat/embedding), a liveness flag, and a free-form detail. Relocated from the model-fit advisor so the
// Loaded Models page can surface both runtimes side by side. role distinguishes chat/embedding roles; isResponsive +
// detail surface liveness for the eject UI.
export interface RunningModel {
	readonly modelName: string;
	readonly role: string;
	readonly isResponsive: boolean;
	readonly detail: string;
}

// Maps the generated (OpenAPI) running-model response to the stricter domain view-model. The generated fields are all
// optional (`x?: T`), so the mapper coalesces every field to a required value with a safe default.
export function toRunningModel(dto: XeLocalAiEngineClientEndpointsModelFitV1RunningModelResponse): RunningModel {
	return {
		modelName: dto.modelName ?? "",
		role: dto.role ?? "",
		isResponsive: dto.isResponsive ?? false,
		detail: dto.detail ?? "",
	};
}
