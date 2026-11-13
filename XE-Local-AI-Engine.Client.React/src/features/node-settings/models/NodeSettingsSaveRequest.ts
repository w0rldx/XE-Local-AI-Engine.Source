import { z } from "zod";

import type { XeLocalAiEngineClientEndpointsNodeSettingsV1SaveNodeSettingsRequest as SaveNodeSettingsRequest } from "@/core/api/generated";
import type { NodeSettingsFieldBounds, NumericBounds } from "@/features/node-settings/models/NodeSettingsFieldBounds";
import {
	type ExternalAccessPreset,
	featureSwitchFields,
	nodeSettingsDisplayScale,
	type NodeSettingsFieldsForm,
	nodeSettingsScaleOf,
	type TunableField,
	tunableFields,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";
import {
	isAllowedKvCacheType,
	isAllowedSpeculativeMode,
	requiresExternalDraftModel,
} from "@/features/node-settings/models/NodeSettingsRuntimeModes";
import { canonicalRateMap, validateUsageRates } from "@/features/node-settings/models/NodeSettingsUsageRateModel";

// Resolves a controlled numeric input (number or string) to a valid integer within [min, max], or undefined when the
// value is empty / fractional / out of range. Mirrors `toValidNodeSettingsTimeoutSeconds`.
function toValidBoundedInt(value: number | string, bounds: NumericBounds): number | undefined {
	const numeric = typeof value === "number" ? value : Number(value);
	if (!Number.isInteger(numeric) || numeric < bounds.min || numeric > bounds.max) {
		return undefined;
	}
	return numeric;
}

// http/https URL validator for the SearXNG URL (mirrors the server's BeAbsoluteHttpUrl). Empty resolves to undefined;
// the caller decides what a blank means on save.
const httpUrlSchema = z
	.string()
	.trim()
	.url()
	.refine((value) => /^https?:\/\//i.test(value), { message: "protocol" });

export function validateOptionalHttpUrl(value: string): { value?: string; error?: "url" } {
	const trimmed = value.trim();
	if (trimmed.length === 0) {
		return {};
	}
	const parsed = httpUrlSchema.safeParse(trimmed);
	return parsed.success ? { value: parsed.data } : { error: "url" };
}

// A single tool-capable-model name. Models are referenced by their provider model name; reject blank/whitespace
// entries and obvious control characters so the list editor never persists junk rows.
// biome-ignore lint/suspicious/noControlCharactersInRegex: intentionally rejects ASCII control chars in a model name.
const controlCharPattern = /[\u0000-\u001f]/;
const toolCapableModelNameSchema = z
	.string()
	.trim()
	.min(1)
	.refine((value) => !controlCharPattern.test(value), { message: "control-char" });

export function validateToolCapableModels(values: readonly string[]): { value: string[]; hasInvalid: boolean } {
	const cleaned: string[] = [];
	let hasInvalid = false;
	for (const raw of values) {
		const parsed = toolCapableModelNameSchema.safeParse(raw);
		if (parsed.success) {
			cleaned.push(parsed.data);
		} else if (raw.trim().length > 0) {
			// A non-empty but invalid entry is a hard error; a purely empty row is silently dropped.
			hasInvalid = true;
		}
	}
	return { value: cleaned, hasInvalid };
}

// The PUT keeps an omitted key and CLEARS a null one back to the node's default; null is the only clear signal (the
// server refuses -1 and ""). A blank text field is therefore sent as null.
function textOrNull(value: string): string | null {
	const trimmed = value.trim();
	return trimmed.length > 0 ? trimmed : null;
}

const unsettableTunableFields: ReadonlySet<string> = new Set([
	"intentMaxUtteranceAgeSeconds",
	"reasoningBudgetMinimalTokens",
	"reasoningBudgetLowTokens",
	"reasoningBudgetMediumTokens",
	"reasoningBudgetHighTokens",
	"chatOutputCapMaxTokens",
]);

// A display value converted back to whole wire units. Blank or non-numeric input stays as it is, so validation still
// reports it instead of a blank silently becoming 0.
function toWireValue(value: number | string, scale: number): number | string {
	if (scale === 1 || (typeof value === "string" && value.trim().length === 0)) {
		return value;
	}
	const numeric = typeof value === "number" ? value : Number(value);
	return Number.isFinite(numeric) ? Math.round(numeric * scale) : value;
}

// The save turns execution previews ON. The body carries changed fields only, so a true here is always the off-to-on
// transition, the one the page confirms before saving; turning them off widens nothing and is never asked about.
export function turnsOnExecutionPreviews(body: SaveNodeSettingsRequest): boolean {
	return body.executionPreviewsEnabled === true;
}

// The save lowers the sandbox security profile. The body carries changed fields only, so "low" here is always the
// high-to-low transition (a pending node never reaches this page: the first-run chooser owns that write).
export function lowersSandboxSecurityProfile(body: SaveNodeSettingsRequest): boolean {
	return body.sandboxSecurityProfile === "low";
}

// True when a built save body carries at least one restart-gated field, so the page can tell the operator a restart is
// needed. The request keys mirror the form keys 1:1, and the body only ever holds CHANGED fields.
// `restartRequiredFields` is the node's own list (the settings response's `restartRequiredFields`, wire names equal to
// the form keys): the fields it reads once at host build, so a save persists but applies only after a node restart.
export function touchesRestartGatedField(body: SaveNodeSettingsRequest, restartRequiredFields: ReadonlySet<string>): boolean {
	return Object.entries(body).some(([key, value]) => needsRestartFor(key, value, restartRequiredFields));
}

// Development mode is mapped at startup but its request gate reads the live setting, so only turning it ON waits for
// a restart; turning it off applies at once.
function needsRestartFor(field: string, newValue: unknown, restartRequiredFields: ReadonlySet<string>): boolean {
	return restartRequiredFields.has(field) && !(field === "developmentEnabled" && newValue === false);
}

// The outcome of validating the whole form: the request body containing ONLY changed fields, plus a per-field error
// map. When `errors` is non-empty the caller must not save.
export interface NodeSettingsValidationResult {
	readonly body: SaveNodeSettingsRequest;
	readonly errors: Readonly<Record<string, string>>;
}

// A positive-long validator for the AgentHome byte caps (> 0), applied to the wire value.
function toValidPositiveLong(value: number | string): number | undefined {
	const numeric = typeof value === "number" ? value : Number(value);
	if (!Number.isInteger(numeric) || numeric <= 0) {
		return undefined;
	}
	return numeric;
}

// The bounded fields with their own row in the resolved bounds, and the ones validated against another row.
type OwnBoundsField = Exclude<keyof NodeSettingsFieldBounds, "agentHomeTimeoutSeconds" | "llamaChatCacheRamMiB" | "tunables">;
type BoundedField = OwnBoundsField | TunableField | "agentHomePrepareTimeoutSeconds" | "agentHomeCommandTimeoutSeconds";

// Builds the PUT body from the edited form, including ONLY fields that differ from the loaded baseline, and collects
// per-field validation errors. Developer-only fields are validated + included only when `includeDeveloperFields` is
// true (they are not rendered, so an off-mode save must never touch them). Error values are i18n suffix keys the page
// maps to messages. `pendingPreset` is the external-access profile the operator just picked and has not since
// overridden by hand; it makes the save a profile COMMAND rather than a field diff (see the external-access block).
export function buildNodeSettingsRequest(
	form: NodeSettingsFieldsForm,
	baseline: NodeSettingsFieldsForm,
	bounds: NodeSettingsFieldBounds,
	includeDeveloperFields: boolean,
	pendingPreset: ExternalAccessPreset | null = null,
	effectiveToolCapableModels?: readonly string[],
): NodeSettingsValidationResult {
	const body: SaveNodeSettingsRequest = {};
	const errors: Record<string, string> = {};

	// Validates one bounded-int field against the baseline; on change either records an error or sets the parsed wire
	// value. The field's display scale converts it back to the wire unit the bounds are in.
	function collectBounded(field: OwnBoundsField): void;
	function collectBounded(field: BoundedField, fieldBounds: NumericBounds): void;
	function collectBounded(field: BoundedField, fieldBounds = bounds[field as OwnBoundsField]): void {
		if (form[field] === baseline[field]) {
			return;
		}
		const parsed = toValidBoundedInt(toWireValue(form[field], nodeSettingsScaleOf(field)), fieldBounds);
		if (parsed === undefined) {
			errors[field] = "range";
			return;
		}
		body[field] = parsed;
	}

	const collectPositiveLong = (field: "agentHomeMaxSelectedFolderBytes" | "agentHomeMaxPatchBytes"): void => {
		if (form[field] === baseline[field]) {
			return;
		}
		const parsed = toValidPositiveLong(toWireValue(form[field], nodeSettingsScaleOf(field)));
		if (parsed === undefined) {
			errors[field] = "positive";
			return;
		}
		body[field] = parsed;
	};

	// defaultModelName — free text; blank clears it back to the seed.
	if (form.defaultModelName !== baseline.defaultModelName) {
		body.defaultModelName = textOrNull(form.defaultModelName);
	}

	// toolCapableModels — list editor; cleaned + validated. Compared by JSON for a stable change check.
	const toolModels = validateToolCapableModels(form.toolCapableModels);
	if (toolModels.hasInvalid) {
		errors["toolCapableModels"] = "invalid";
	} else if (JSON.stringify(toolModels.value) !== JSON.stringify(baseline.toolCapableModels)) {
		body.toolCapableModels = toolModels.value;
	}

	if (form.webSearchSearxngUrl !== baseline.webSearchSearxngUrl) {
		const searxngUrl = validateOptionalHttpUrl(form.webSearchSearxngUrl);
		if (searxngUrl.error) {
			errors["webSearchSearxngUrl"] = searxngUrl.error;
		} else {
			// A blank field is sent as null — the clear back to DuckDuckGo.
			body.webSearchSearxngUrl = searxngUrl.value ?? null;
		}
	}

	if (form.huggingFaceDefaultQuant !== baseline.huggingFaceDefaultQuant) {
		body.huggingFaceDefaultQuant = textOrNull(form.huggingFaceDefaultQuant);
	}

	collectBounded("llamaMaxLoadedProcesses");
	collectBounded("llamaIdleTimeToLiveSeconds");

	const keepWarmModelName = form.keepModelWarmModelName.trim();
	if (form.keepModelWarmEnabled && keepWarmModelName.length === 0) {
		errors["keepModelWarmModelName"] = "requiredKeepWarmModel";
	} else if (form.keepModelWarmModelName !== baseline.keepModelWarmModelName) {
		body.keepModelWarmModelName = textOrNull(keepWarmModelName);
	}

	if (form.keepModelWarmEnabled) {
		const maxLoadedProcesses = toValidBoundedInt(form.llamaMaxLoadedProcesses, bounds.llamaMaxLoadedProcesses);
		if (maxLoadedProcesses !== undefined && maxLoadedProcesses < 2) {
			errors["llamaMaxLoadedProcesses"] = "keepWarmCapacity";
		}

		// A disabled feature must always be saveable, even if the operator entered an invalid interval before turning it
		// off. The disabled interval control cannot be corrected in that state, and omission preserves the last valid value.
		collectBounded("keepModelWarmIntervalSeconds");

		const warmInterval = toValidBoundedInt(form.keepModelWarmIntervalSeconds, bounds.keepModelWarmIntervalSeconds);
		const idleTtl = toValidBoundedInt(
			toWireValue(form.llamaIdleTimeToLiveSeconds, nodeSettingsDisplayScale.llamaIdleTimeToLiveSeconds),
			bounds.llamaIdleTimeToLiveSeconds,
		);
		if (warmInterval !== undefined && idleTtl !== undefined && warmInterval >= idleTtl) {
			errors["keepModelWarmIntervalSeconds"] = "belowIdleTtl";
		}
	}
	collectBounded("maxResponseSizeMb");

	// KV-cache type. Sent whenever it differs from the baseline; an unknown value is a hard error rather than a silent
	// drop, the same way speculativeMode is handled. Changing this invalidates every frozen inference profile on the
	// node, which the field description spells out.
	if (form.kvCacheType !== baseline.kvCacheType) {
		if (!isAllowedKvCacheType(form.kvCacheType)) {
			errors["kvCacheType"] = "type";
		} else {
			body.kvCacheType = form.kvCacheType.trim();
		}
	}

	// Speculative decoding mode. Sent whenever it differs from the baseline (including a switch back to "none"). An
	// unknown mode is a hard error rather than a silent drop.
	if (form.speculativeMode !== baseline.speculativeMode) {
		if (!isAllowedSpeculativeMode(form.speculativeMode)) {
			errors["speculativeMode"] = "mode";
		} else {
			body.speculativeMode = form.speculativeMode.trim();
		}
	}

	// An external-draft mode requires a draft model; guard even when the mode itself did not change (e.g. the model was
	// cleared). draft-mtp is exempt — it drafts from the main model's own heads.
	const draftModelName = form.speculativeDraftModelName.trim();
	if (requiresExternalDraftModel(form.speculativeMode) && draftModelName.length === 0) {
		errors["speculativeDraftModelName"] = "required";
	} else if (form.speculativeDraftModelName !== baseline.speculativeDraftModelName) {
		// Empty clears the stored name (sent as null); a non-empty value is the installed model name to resolve.
		body.speculativeDraftModelName = textOrNull(draftModelName);
	}

	collectBounded("speculativeDraftMaxTokens");
	collectBounded("chatCacheReuse");

	// Knowledge-base reranker model — free model name; "Off" is sent as null (reranking disabled). Sent whenever it
	// differs from the baseline, including a switch back to "Off".
	if (form.rerankerModelName !== baseline.rerankerModelName) {
		body.rerankerModelName = textOrNull(form.rerankerModelName);
	}

	// Knowledge-base embedding pin — sent only on a change; the backend refuses a name that is not an installed embedding model.
	if (form.knowledgeEmbeddingModelName !== baseline.knowledgeEmbeddingModelName) {
		body.knowledgeEmbeddingModelName = textOrNull(form.knowledgeEmbeddingModelName);
	}

	// Fast model for automatic reasoning effort — same shape as the reranker: "Off" is sent as null. Deliberately NOT
	// restart-gated: the dispatcher reads it per send, so a save applies to the very next turn.
	if (form.autoEffortFastModelName !== baseline.autoEffortFastModelName) {
		body.autoEffortFastModelName = textOrNull(form.autoEffortFastModelName);
	}

	// Usage rates — an editable per-model rate map. Validated to non-negative numbers with non-empty names; an invalid
	// row is a hard error. Sent (as the map, or null when emptied, which clears the override) only when it differs from
	// the loaded baseline; omitting it keeps the current table.
	const rates = validateUsageRates(form.usageRates);
	if (rates.hasInvalid) {
		errors["usageRates"] = "rate";
	} else if (canonicalRateMap(rates.map) !== canonicalRateMap(validateUsageRates(baseline.usageRates).map)) {
		body.usageRates = rates.map;
	}

	// External access — the profile and the three switches are NEVER sent together. A pending preset is a command: the
	// server writes that preset's triple. Otherwise only the switches the operator changed go out, and the server stamps
	// the profile "custom" itself. The client never computes "custom", and there is no client-side profile validator —
	// the value can only come from a closed-set Select, and the request validator is the trust boundary.
	if (pendingPreset !== null) {
		body.externalAccessProfile = pendingPreset;
	} else {
		// Explicit false is meaningful here exactly as it is for keepModelWarmEnabled: omission preserves the stored
		// value, while false switches the check off.
		if (form.autoCheckApplicationUpdates !== baseline.autoCheckApplicationUpdates) {
			body.autoCheckApplicationUpdates = form.autoCheckApplicationUpdates;
		}
		if (form.autoCheckRuntimeUpdates !== baseline.autoCheckRuntimeUpdates) {
			body.autoCheckRuntimeUpdates = form.autoCheckRuntimeUpdates;
		}
		if (form.autoProvisionFirstRunModel !== baseline.autoProvisionFirstRunModel) {
			body.autoProvisionFirstRunModel = form.autoProvisionFirstRunModel;
		}
	}

	if (form.uiMode !== baseline.uiMode) {
		body.uiMode = form.uiMode;
	}

	// Only the two real literals are ever sent: the server rejects anything else, and "pending" is the first-run chooser's
	// to answer, never this page's.
	if (
		form.sandboxSecurityProfile !== baseline.sandboxSecurityProfile &&
		(form.sandboxSecurityProfile === "low" || form.sandboxSecurityProfile === "high")
	) {
		body.sandboxSecurityProfile = form.sandboxSecurityProfile;
	}

	// The switches sent as they are: omission preserves the stored value, while an explicit false turns the thing off (the
	// live keep-warm service, compaction, voice for every user on the node). The five cloud opt-ins are deliberately NOT
	// external-access preset switches, so a hand edit here never clears a pending preset.
	const plainSwitchFields = [
		"enableTools",
		"customToolsEnabled",
		"toolRelevanceEnabled",
		"webAccessEnabled",
		"keepModelWarmEnabled",
		"compactionAutoEnabled",
		"compactionDistillEnabled",
		"providerRetryEnabled",
		"voiceFeatureEnabled",
		"knowledgeAdaptiveRerankingEnabled",
		"knowledgeScheduledReindexEnabled",
		"knowledgeAgentToolsEnabled",
		"allowCloudModelAccess",
		"allowCloudModelUnattendedRuns",
		"allowCloudModelWebTools",
		"allowCloudModelMcpTools",
		"allowCloudModelSubAgents",
		"chatRetentionEnabled",
		"agentExecutionLogRetentionEnabled",
		"imageTextEncoderOnGpu",
	] as const;
	for (const field of plainSwitchFields) {
		if (form[field] !== baseline[field]) {
			body[field] = form[field];
		}
	}

	for (const field of featureSwitchFields) {
		if (form[field] !== baseline[field]) {
			body[field] = form[field];
		}
	}

	// Client mirror of the NodeSettingsPolicy coupling, blamed on the same field the server blames: the switch being
	// turned on, or — when it was already on — what took its prerequisite away.
	// The server judges the list the save leaves: a non-empty one, else the appsettings seed. Only an empty stored list lets
	// the effective list (`effectiveToolCapableModels`, GET agents/tool-capable-models) reveal that seed, so clearing a
	// non-empty list is left to the server's refusal on the field. Unknown reads as satisfied: a missed refusal still
	// comes back from the server, a false one would block every save.
	if (
		form.agentHomeEnabled &&
		toolModels.value.length === 0 &&
		baseline.toolCapableModels.length === 0 &&
		effectiveToolCapableModels?.length === 0
	) {
		errors[baseline.agentHomeEnabled ? "toolCapableModels" : "agentHomeEnabled"] ??= "requiresToolCapableModels";
	}

	// The background models: null is the "inherit the default model" signal.
	const backgroundModelFields = ["playbookAnalysisModelName", "playbookEvalModelName", "memoryExtractionModelName"] as const;
	for (const field of backgroundModelFields) {
		if (form[field] !== baseline[field]) {
			body[field] = textOrNull(form[field]);
		}
	}

	// The voice picker offers no "none", so a blank draft only ever equals a blank baseline and is never sent.
	if (form.defaultVoiceProfile !== baseline.defaultVoiceProfile && form.defaultVoiceProfile.length > 0) {
		body.defaultVoiceProfile = form.defaultVoiceProfile;
	}

	for (const field of tunableFields) {
		if (unsettableTunableFields.has(field) && String(form[field]).trim() === "") {
			if (String(baseline[field]).trim() !== "") {
				body[field] = null;
			}
			continue;
		}
		collectBounded(field, bounds.tunables[field]);
	}

	// The default result count may not exceed the maximum; the server rejects the pair too (mapped onto the same field).
	if (
		form.knowledgeSearchDefaultResults !== baseline.knowledgeSearchDefaultResults ||
		form.knowledgeSearchMaxResults !== baseline.knowledgeSearchMaxResults
	) {
		const defaultResults = toValidBoundedInt(form.knowledgeSearchDefaultResults, bounds.tunables.knowledgeSearchDefaultResults);
		const maxResults = toValidBoundedInt(form.knowledgeSearchMaxResults, bounds.tunables.knowledgeSearchMaxResults);
		if (defaultResults !== undefined && maxResults !== undefined && defaultResults > maxResults) {
			errors["knowledgeSearchDefaultResults"] = "defaultAboveMax";
		}
	}

	collectChatCacheRam(form, baseline, bounds.llamaChatCacheRamMiB, body, errors);

	collectBounded("speculativeDraftGpuLayers");

	collectBounded("huggingFaceDiskMarginBytes");

	if (form.containerRuntimeSelection !== baseline.containerRuntimeSelection) {
		body.containerRuntimeSelection = form.containerRuntimeSelection;
	}

	// The empty "default" option clears the effort back to the shipped default.
	if (form.defaultReasoningEffort !== baseline.defaultReasoningEffort) {
		body.defaultReasoningEffort = textOrNull(form.defaultReasoningEffort);
	}

	// A GUID sets the router, null clears it (alias-only routing); the picker offers only saved definitions and "none".
	if (form.intentRouterDefinitionId !== baseline.intentRouterDefinitionId) {
		body.intentRouterDefinitionId = textOrNull(form.intentRouterDefinitionId);
	}

	if (form.chatOutputCapMode !== baseline.chatOutputCapMode) {
		body.chatOutputCapMode = textOrNull(form.chatOutputCapMode);
	}

	if (includeDeveloperFields) {
		collectBounded("agentHomeMaxRunSeconds");
		collectBounded("agentHomeRunRetentionDays");
		collectBounded("agentHomeRunRetentionMaxRuns");
		collectBounded("agentHomeRunRetentionMaxTotalBytes");
		collectBounded("agentHomeMaxInnerToolCalls");
		collectBounded("agentHomePatchApplyTimeoutSeconds");
		// A whole run must be allowed at least one full command; the server enforces the same on the merged values.
		if (
			form.agentHomeMaxRunSeconds !== baseline.agentHomeMaxRunSeconds ||
			form.agentHomeCommandTimeoutSeconds !== baseline.agentHomeCommandTimeoutSeconds
		) {
			const maxRun = toValidBoundedInt(
				toWireValue(form.agentHomeMaxRunSeconds, nodeSettingsDisplayScale.agentHomeMaxRunSeconds),
				bounds.agentHomeMaxRunSeconds,
			);
			const commandTimeout = toValidBoundedInt(
				toWireValue(form.agentHomeCommandTimeoutSeconds, nodeSettingsDisplayScale.agentHomeCommandTimeoutSeconds),
				bounds.agentHomeTimeoutSeconds,
			);
			if (maxRun !== undefined && commandTimeout !== undefined && maxRun < commandTimeout) {
				errors["agentHomeMaxRunSeconds"] = "belowCommandTimeout";
			}
		}
		collectBounded("orchestrationIdleTimeoutSeconds");
		collectBounded("agentHomePrepareTimeoutSeconds", bounds.agentHomeTimeoutSeconds);
		collectBounded("agentHomeCommandTimeoutSeconds", bounds.agentHomeTimeoutSeconds);
		collectBounded("maxPendingToolCallAgeMinutes");
		collectBounded("detachedGraceSeconds");
		collectPositiveLong("agentHomeMaxSelectedFolderBytes");
		collectPositiveLong("agentHomeMaxPatchBytes");
	}

	return { body, errors };
}

// Prompt-cache RAM: compares what each side MEANS (auto / off / a size), so switching Custom on and back off without a
// net change sends nothing. Automatic is sent as null, which clears the stored size.
function collectChatCacheRam(
	form: NodeSettingsFieldsForm,
	baseline: NodeSettingsFieldsForm,
	bounds: NumericBounds,
	body: SaveNodeSettingsRequest,
	errors: Record<string, string>,
): void {
	const wireOf = (draft: NodeSettingsFieldsForm): number | string | null => {
		switch (draft.llamaChatCacheRamMode) {
			case "auto":
				return null;
			case "off":
				return 0;
			default:
				return draft.llamaChatCacheRamMiB;
		}
	};
	const wire = wireOf(form);
	if (wire === wireOf(baseline)) {
		return;
	}
	if (form.llamaChatCacheRamMode !== "custom") {
		body.llamaChatCacheRamMiB = wire as number | null;
		return;
	}
	// A custom size is at least 1 MiB; 0 is the Off mode.
	const parsed = toValidBoundedInt(form.llamaChatCacheRamMiB, { min: Math.max(1, bounds.min), max: bounds.max });
	if (parsed === undefined) {
		errors["llamaChatCacheRamMiB"] = "range";
		return;
	}
	body.llamaChatCacheRamMiB = parsed;
}

// What the save bar reports: every field the next Save would send, plus every edited field that currently fails
// validation (it is still an unsaved change, it just cannot go out yet), and the restart-gated subset of those.
export interface NodeSettingsPendingChanges {
	readonly changed: readonly (keyof NodeSettingsFieldsForm)[];
	readonly restartRequired: readonly (keyof NodeSettingsFieldsForm)[];
}

export function summarizePendingChanges(
	result: NodeSettingsValidationResult,
	form: NodeSettingsFieldsForm,
	baseline: NodeSettingsFieldsForm,
	restartRequiredFields: ReadonlySet<string>,
): NodeSettingsPendingChanges {
	const changed = new Set(Object.keys(result.body) as (keyof NodeSettingsFieldsForm)[]);
	for (const key of Object.keys(result.errors)) {
		const field = key as keyof NodeSettingsFieldsForm;
		if (JSON.stringify(form[field]) !== JSON.stringify(baseline[field])) {
			changed.add(field);
		}
	}
	const list = [...changed];
	return { changed: list, restartRequired: list.filter((field) => needsRestartFor(field, form[field], restartRequiredFields)) };
}
