// Domain view-model for one model the optional Ollama runtime is currently holding in memory: a sanitized projection
// of its `/api/ps`-style snapshot. The Loaded Models page no longer shows Ollama; the assist feature still reads the
// names to prefer an already-warm model, so only the name is projected.
export interface LoadedModel {
	readonly modelName: string;
}

// Domain view-model for the Ollama running-models snapshot. `isAvailable:false` is the explicit unavailable state —
// the provider was unreachable, so the backend returns 200 with an empty list rather than a 500.
export interface LoadedModelsSnapshot {
	readonly isAvailable: boolean;
	// Whether the optional Ollama runtime is configured/enabled on this node at all (the XE_OLLAMA_RUNTIME_ENABLED
	// gate). When false the query stops polling entirely — no daemon will ever answer — rather than backing off forever.
	// Distinct from isAvailable, which reflects whether a configured daemon is currently reachable.
	readonly ollamaConfigured: boolean;
	readonly models: readonly LoadedModel[];
}
