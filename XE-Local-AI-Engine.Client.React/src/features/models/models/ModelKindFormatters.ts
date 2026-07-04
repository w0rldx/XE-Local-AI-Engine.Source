// Shared presentation helpers for a local model's ModelKind (Chat / Embedding / Unknown) and its raw Ollama
// capabilities. Kept in a non-component module so both the installed-models table and the details dialog can import
// them without tripping the "components-only export" lint rule (mirrors ModelFitFormatters.ts).

type Translate = (key: string, fallback: string) => string;

// Effective-kind badge color: chat-capable models stand out (blue), embedding models are visually distinct (grape),
// reranker (cross-encoder) models get their own hue (orange), and an unclassified/unknown model is muted (gray).
export function kindBadgeColor(kind: string): string {
	switch (kind) {
		case "Chat":
			return "blue";
		case "Embedding":
			return "grape";
		case "Reranker":
			return "orange";
		default:
			return "gray";
	}
}

// Localized label for a ModelKind enum string. Falls back to the raw value for any unexpected/future kind.
export function kindLabel(t: Translate, kind: string): string {
	switch (kind) {
		case "Chat":
			return t("pages.models.type.kind.chat", "Chat");
		case "Embedding":
			return t("pages.models.type.kind.embedding", "Embedding");
		case "Reranker":
			return t("pages.models.type.kind.reranker", "Reranker");
		case "Unknown":
			return t("pages.models.type.kind.unknown", "Unknown");
		default:
			return kind;
	}
}

// Localized label for a raw Ollama capability string, falling back to the raw value for capabilities without a label.
export function capabilityLabel(t: Translate, capability: string): string {
	switch (capability) {
		case "tools":
			return t("pages.models.type.capability.tools", "Tools");
		case "vision":
			return t("pages.models.type.capability.vision", "Vision");
		case "thinking":
			return t("pages.models.type.capability.thinking", "Thinking");
		default:
			return capability;
	}
}

// The three classifications the operator can pick from when overriding a model's kind. Mirrors
// the persisted ModelKind enum names — the value is sent verbatim to the override endpoint.
const overridableKinds = ["Chat", "Embedding", "Reranker", "Unknown"] as const;

// Override options for a given model: the fixed overridable kinds, plus the model's current effective kind when it is
// not already in the list. This keeps the Select's value (the effective model.kind) always matching an option, so a
// future effective kind (e.g. Vision/Reranker) renders as a real, selectable entry instead of a blank Select.
export function buildKindOptions(t: Translate, currentKind: string): { value: string; label: string }[] {
	const kinds = overridableKinds.some((kind) => kind === currentKind) ? overridableKinds : [...overridableKinds, currentKind];
	return kinds.map((kind) => ({ value: kind, label: kindLabel(t, kind) }));
}
