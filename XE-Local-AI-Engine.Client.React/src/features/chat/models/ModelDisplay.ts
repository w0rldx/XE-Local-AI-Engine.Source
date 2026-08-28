// One derivation of "how do we name this model on screen", shared by every surface that names the active model: the
// composer's picker trigger, the picker's option rows and the context-usage meter's label.
//
// WHY it exists: those three sites each carried their own `displayName ?? label ?? value` chain, and all three then
// clamped the result to one line. A catalog id such as `unsloth/Qwen3-8B-Instruct-GGUF:Q4_K_M` clamps to
// "unsloth/Qwen3-8B-Inst…", which is the half of the string that identifies the PUBLISHER, not the model — so the
// operator could not tell which model was selected, which is the whole point of the control. Shortening from the
// structure of the id (org prefix, repo-kind suffix, quantization tag) keeps the identifying half and moves the
// stripped metadata onto a second line instead of throwing it away.

import type { ModelOption } from "@/features/chat/models/ChatModels";

/** How one model option is named across the picker trigger, the option rows and the context meter. */
export interface ModelDisplay {
	/** Line one: the shortest string that still identifies the model. Never empty. */
	primary: string;
	/** Line two: where the turn runs (connection / provider) or what the weights are (size · quant). */
	secondary?: string;
	/** The untruncated identity, for the tooltip: friendly label, raw id and serving provider, without repeats. */
	full: string;
}

// The publisher/repository prefix of a Hugging Face style id, and the `ext:{connectionId}/` namespace of an external
// model. Both answer "where did this come from", which the picker already says in its section heading.
const ORG_PREFIX = /^.*\//;

// A trailing quantization tag, in the two separators the catalog uses (`repo-GGUF:Q4_K_M`, `model-Q4_K_M`). Matched
// case-sensitively on purpose: the markers are uppercase by convention, and a loose match would eat a real name.
const QUANT_SUFFIX = /[:_-](IQ\d+[A-Z0-9_]*|Q\d+[A-Z0-9_]*|MXFP\d+[A-Z0-9_]*|BF16|FP16|FP8|F16|F32)$/;

// "-GGUF" says the repository packages GGUF weights. Every model in this list is one, so it distinguishes nothing.
const REPO_KIND_SUFFIX = /-GGUF$/i;

// Characters, not pixels: the trigger and the option rows both clamp with CSS, so this only has to stop a
// pathologically long id from pushing the ellipsis past the point where the name is still recognisable.
const MAX_PRIMARY_LENGTH = 30;

// Cloud providers as the catalog tags them, in the spelling an operator would recognise.
const PROVIDER_LABELS: Record<string, string> = {
	CodexOAuth: "OpenAI Codex",
	AzureFoundry: "Azure Foundry",
};

/**
 * Shortens `value` from the middle, keeping both ends. The LAST resort: an id that survives prefix and suffix
 * stripping and is still too long has no structure left to exploit, and its head and tail are more identifying than
 * its head alone.
 */
export function middleEllipsis(value: string, maxLength: number = MAX_PRIMARY_LENGTH): string {
	if (value.length <= maxLength || maxLength < 3) {
		return value;
	}

	const kept = maxLength - 1;
	const head = Math.ceil(kept / 2);
	return `${value.slice(0, head)}…${value.slice(value.length - (kept - head))}`;
}

/**
 * Names a bare model id — no option, no operator label — the same way {@link deriveModelDisplay} names an option.
 * Used for the "Local default" sentinel, whose concrete model the runtime resolves at send time and which therefore
 * has no option of its own to read a display from.
 */
export function deriveModelIdDisplay(rawId: string): ModelDisplay {
	const id = rawId.trim();
	if (id.length === 0) {
		return { primary: "", full: "" };
	}

	const withoutOrg = id.replace(ORG_PREFIX, "");
	const base = withoutOrg.length > 0 ? withoutOrg : id;
	const quant = QUANT_SUFFIX.exec(base);
	const withoutQuant = quant === null ? base : base.slice(0, quant.index);
	const name = withoutQuant.replace(REPO_KIND_SUFFIX, "");

	return {
		primary: middleEllipsis(name.length > 0 ? name : base),
		secondary: quant?.[1],
		full: id,
	};
}

/**
 * Names one picker option. `primary` prefers the operator's own label (Azure deployments, external models) and
 * otherwise shortens the id; `secondary` says where the turn runs for a cloud or external model and what the weights
 * are for a local one; `full` is the untruncated identity for a tooltip.
 *
 * Takes the same `(option, fallback)` shape the picker's old `display()` did, so a call site with no selection yet
 * still renders its placeholder.
 */
export function deriveModelDisplay(option: ModelOption | undefined, fallback: string): ModelDisplay {
	if (option === undefined) {
		return { primary: fallback, full: fallback };
	}

	const rawId = option.value.trim() || option.label.trim();
	const label = option.displayName?.trim() ?? "";
	const fromId = deriveModelIdDisplay(rawId);
	const primary = label.length > 0 ? middleEllipsis(label) : fromId.primary;

	// A connection name beats a provider tag: every external model shares the one `external` provider, so the tag
	// cannot say which of the operator's endpoints a turn goes to — and that is exactly what line two is for.
	const providerLabel = option.externalConnectionName?.trim() || PROVIDER_LABELS[option.provider ?? ""];
	const secondary = providerLabel ?? option.statusLabel?.trim() ?? fromId.secondary;

	// Deduplicated: when the operator's label IS the id (cloud options set displayName = the deployment name),
	// repeating it in the tooltip says nothing.
	const fullParts = [label, rawId, providerLabel].filter(
		(part, index, parts): part is string => Boolean(part) && parts.indexOf(part) === index,
	);

	return {
		primary: primary.length > 0 ? primary : fallback,
		secondary: secondary !== undefined && secondary.length > 0 ? secondary : undefined,
		full: fullParts.length > 0 ? fullParts.join(" · ") : fallback,
	};
}
