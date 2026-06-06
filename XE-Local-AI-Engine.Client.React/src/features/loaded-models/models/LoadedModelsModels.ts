import { z } from "zod";

// Domain view-model for one model the local runtime is currently holding in memory (RAM/VRAM). Sanitized
// projection of the Ollama `/api/ps`-style snapshot the backend surfaces — it carries only what the page renders.
// Every size/timestamp field is nullable because the runtime may omit them (e.g. a model resident only in RAM
// reports no separate VRAM figure); the mapper coalesces the optional-field generated DTO to these shapes.
// `expiresAtUtc` is epoch milliseconds: the instant the runtime will evict the model if left idle.
export interface LoadedModel {
	readonly modelName: string;
	readonly sizeBytes: number | null;
	readonly sizeVramBytes: number | null;
	readonly expiresAtUtc: number | null;
}

// Domain view-model for the running-models snapshot. `isAvailable:false` is the explicit unavailable state —
// the provider was unreachable, so the backend returns 200 with an empty list rather than a 500 (the page polls
// and must degrade gracefully). `error` carries a sanitized reason when unavailable; `models` is empty then.
export interface LoadedModelsSnapshot {
	readonly isAvailable: boolean;
	readonly error: string | null;
	readonly models: readonly LoadedModel[];
}

// Response shape of a graceful unload (eject). The runtime sets keep_alive=0 so the model is evicted AFTER any
// in-flight generation finishes; `unloaded` reflects whether the runtime accepted the request (idempotent — a
// model that was not loaded is a no-op success).
export interface UnloadResult {
	readonly modelName: string;
	readonly unloaded: boolean;
}

// Zod schema mirroring the running-model wire shape. Used by the page tests (and any future boundary validation)
// to assert the domain view-model stays in sync with the generated DTO. All fields optional/nullable to match.
export const loadedModelSchema = z.object({
	modelName: z.string(),
	sizeBytes: z.number().nullable(),
	sizeVramBytes: z.number().nullable(),
	expiresAtUtc: z.number().nullable(),
});
