export interface ModelOption {
	value: string;
	label: string;
	displayName?: string;
	// GRADED reasoning: the model advertises the Ollama `thinking` capability, i.e. a switchable think:<level>
	// control. Drives the graded reasoning-effort menu (none/low/medium/high).
	isReasoningModel?: boolean;
	// NATIVE reasoning: the model reasons on a channel baked into its chat template with no graded switch (OpenAI
	// harmony / gpt-oss). Mutually exclusive with `isReasoningModel`. Drives its OWN picker badge, but deliberately
	// NOT the effort vocabulary — a native model keeps the binary On/Off set, where "on" means "omit the think field
	// and let the template's built-in reasoning run".
	isNativeReasoningModel?: boolean;
	// Whether a model that reasons also honors a GRADED effort level. Splits the two shapes `isReasoningModel` alone
	// conflates for an externally served model: an endpoint that reads `reasoning_effort` gets the graded menu, one
	// that reasons on its own terms gets the binary On/Off set, because offering it levels it ignores is a menu whose
	// entries do nothing. Undefined means "not declared" — every non-external provider, whose effort vocabulary is
	// decided by `isCloud` and `isReasoningModel` exactly as before.
	isReasoningEffortCapable?: boolean;
	// Whether "Answer now" can end this model's reasoning early: a graded-reasoning llama.cpp model whose template
	// has a reasoning end tag. Undefined (cloud, Ollama, external) is "no".
	isReasoningControllable?: boolean;
	// Whether the model advertises the Ollama `tools` capability. Drives whether the composer offers the
	// local-tool controls (gated together with the node-wide capability). Undefined on the local-default
	// option (the runtime picks a concrete model later), so callers treat undefined as "not tool-capable".
	isToolCapable?: boolean;
	// Whether the model has a local mmproj vision projector (backend `isMultimodalCapable`). Drives whether the
	// composer offers image attachments for this model (gated together with the node-wide capability). Undefined on
	// the local-default option (the runtime picks a concrete model later), so callers treat undefined as "not
	// multimodal".
	isMultimodal?: boolean;
	isAvailable: boolean;
	statusLabel?: string;
	// True for cloud-provider models (e.g. Codex/OpenAI). Drives the "Cloud (Codex)" section in the picker
	// and the egress cue badge. Local models never set this; absence is equivalent to false.
	isCloud?: boolean;
	// The runtime that serves this local model ("Ollama" / "llamacpp"), straight from the list entry's Provider.
	// Used to gate which selections poll the model-details endpoint. Undefined on the local-default sentinel option
	// (the runtime resolves a concrete model later) and on cloud options (gated by isCloud instead).
	provider?: string;
	// External-provider connection this model belongs to (`provider === "external"` only). The picker groups external
	// models one section per connection, which `provider` alone cannot express — every connection shares that one tag.
	externalConnectionId?: string;
	externalConnectionName?: string;
	// The connection's OPERATOR-DECLARED trust ("local" | "cloud"), straight from the list entry. Deliberately distinct
	// from `isCloud`, which selects the Codex reasoning-effort vocabulary (minimal/xhigh) that external models must
	// never be offered: this only decides which section an external model lands in and which egress cue it carries.
	declaredLocality?: string;
}
