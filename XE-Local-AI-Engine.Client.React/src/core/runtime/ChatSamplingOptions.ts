// Shared per-call sampling overrides. All fields are optional; absent = use model default.
// This is the in-app shape the dialog + store work with — seed is a number here for the NumberInput UX. The
// serialized wire shape (`WireSamplingOptions`) matches the backend SamplingOptions record, where seed is a string.

export interface ChatSamplingOptions {
	temperature?: number;
	topP?: number;
	topK?: number;
	minP?: number;
	maxOutputTokens?: number;
	repeatPenalty?: number;
	repeatLastN?: number;
	presencePenalty?: number;
	frequencyPenalty?: number;
	seed?: number;
	stop?: string[];
	numCtx?: number;
	reasoningBudgetTokens?: number;
}

// The serialized wire shape sent to the backend. Identical to ChatSamplingOptions except seed is a string: a seed is
// an unconstrained 64-bit value, so it rides the wire as a string to avoid the precision loss a JSON number suffers
// above 2^53 (mirrors the backend SamplingOptions.Seed / SeedValue contract).
export type WireSamplingOptions = Omit<ChatSamplingOptions, "seed"> & { seed?: string };

// Per-field range metadata drives the dialog inputs. labelKey is the i18n key for the field label.
export interface SamplingFieldMeta {
	key: keyof ChatSamplingOptions;
	labelKey: string;
	descriptionKey: string;
	// The FieldInfo card text: what the knob does, its typical value and when to change it.
	infoKey: string;
	min: number;
	max: number;
	step: number;
	decimalScale?: number;
	allowDecimal: boolean;
	// When true the dialog renders a Slider paired with a NumberInput; false = number-only (e.g. seed).
	slider: boolean;
	// True for knobs only the local runtime honors. llama.cpp gets them via DeferredLlamaServerChatClient's body
	// patch, but the OpenAI-shaped cloud paths (Codex/Azure) have no wire field for them, so the dialog disables these inputs while a cloud model is selected rather than pretending they apply.
	localRuntimeOnly?: boolean;
}

export const samplingFieldGroups: { groupKey: string; fields: SamplingFieldMeta[] }[] = [
	{
		groupKey: "pages.chat.samplingOptions.groupSampling",
		fields: [
			{
				key: "temperature",
				labelKey: "pages.chat.samplingOptions.temperature",
				descriptionKey: "pages.chat.samplingOptions.temperatureDescription",
				infoKey: "pages.chat.samplingOptions.temperatureInfo",
				min: 0,
				max: 2,
				step: 0.05,
				decimalScale: 2,
				allowDecimal: true,
				slider: true,
			},
			{
				key: "topP",
				labelKey: "pages.chat.samplingOptions.topP",
				descriptionKey: "pages.chat.samplingOptions.topPDescription",
				infoKey: "pages.chat.samplingOptions.topPInfo",
				min: 0,
				max: 1,
				step: 0.05,
				decimalScale: 2,
				allowDecimal: true,
				slider: true,
			},
			{
				key: "topK",
				labelKey: "pages.chat.samplingOptions.topK",
				descriptionKey: "pages.chat.samplingOptions.topKDescription",
				infoKey: "pages.chat.samplingOptions.topKInfo",
				min: 1,
				max: 200,
				step: 1,
				allowDecimal: false,
				slider: true,
				localRuntimeOnly: true,
			},
			{
				key: "minP",
				labelKey: "pages.chat.samplingOptions.minP",
				descriptionKey: "pages.chat.samplingOptions.minPDescription",
				infoKey: "pages.chat.samplingOptions.minPInfo",
				min: 0,
				max: 1,
				step: 0.05,
				decimalScale: 2,
				allowDecimal: true,
				slider: true,
				localRuntimeOnly: true,
			},
		],
	},
	{
		groupKey: "pages.chat.samplingOptions.groupPenalties",
		fields: [
			{
				key: "repeatPenalty",
				labelKey: "pages.chat.samplingOptions.repeatPenalty",
				descriptionKey: "pages.chat.samplingOptions.repeatPenaltyDescription",
				infoKey: "pages.chat.samplingOptions.repeatPenaltyInfo",
				min: 0,
				max: 2,
				step: 0.05,
				decimalScale: 2,
				allowDecimal: true,
				slider: true,
				localRuntimeOnly: true,
			},
			{
				key: "repeatLastN",
				labelKey: "pages.chat.samplingOptions.repeatLastN",
				descriptionKey: "pages.chat.samplingOptions.repeatLastNDescription",
				infoKey: "pages.chat.samplingOptions.repeatLastNInfo",
				min: -1,
				max: 512,
				step: 1,
				allowDecimal: false,
				slider: true,
				localRuntimeOnly: true,
			},
			{
				key: "presencePenalty",
				labelKey: "pages.chat.samplingOptions.presencePenalty",
				descriptionKey: "pages.chat.samplingOptions.presencePenaltyDescription",
				infoKey: "pages.chat.samplingOptions.presencePenaltyInfo",
				min: -2,
				max: 2,
				step: 0.05,
				decimalScale: 2,
				allowDecimal: true,
				slider: true,
			},
			{
				key: "frequencyPenalty",
				labelKey: "pages.chat.samplingOptions.frequencyPenalty",
				descriptionKey: "pages.chat.samplingOptions.frequencyPenaltyDescription",
				infoKey: "pages.chat.samplingOptions.frequencyPenaltyInfo",
				min: -2,
				max: 2,
				step: 0.05,
				decimalScale: 2,
				allowDecimal: true,
				slider: true,
			},
		],
	},
	{
		groupKey: "pages.chat.samplingOptions.groupLimits",
		fields: [
			{
				key: "maxOutputTokens",
				labelKey: "pages.chat.samplingOptions.maxOutputTokens",
				descriptionKey: "pages.chat.samplingOptions.maxOutputTokensDescription",
				infoKey: "pages.chat.samplingOptions.maxOutputTokensInfo",
				min: 1,
				max: 131072,
				step: 128,
				allowDecimal: false,
				slider: true,
			},
			{
				key: "seed",
				labelKey: "pages.chat.samplingOptions.seed",
				descriptionKey: "pages.chat.samplingOptions.seedDescription",
				infoKey: "pages.chat.samplingOptions.seedInfo",
				min: -1,
				max: 2147483647,
				step: 1,
				allowDecimal: false,
				slider: false,
			},
			{
				key: "numCtx",
				labelKey: "pages.chat.samplingOptions.numCtx",
				descriptionKey: "pages.chat.samplingOptions.numCtxDescription",
				infoKey: "pages.chat.samplingOptions.numCtxInfo",
				min: 512,
				max: 131072,
				step: 512,
				allowDecimal: false,
				slider: true,
			},
		],
	},
];

// The per-send thinking budget, deliberately outside samplingFieldGroups: only chat and Direct Inference offer it, and
// only for a budget-capable llama.cpp model. Bounds mirror ReasoningBudgets.MinTokens/MaxTokens, which every input path
// enforces.
export const reasoningBudgetField: SamplingFieldMeta = {
	key: "reasoningBudgetTokens",
	labelKey: "pages.chat.samplingOptions.reasoningBudgetTokens",
	descriptionKey: "pages.chat.samplingOptions.reasoningBudgetTokensDescription",
	infoKey: "pages.chat.samplingOptions.reasoningBudgetTokensInfo",
	min: 128,
	max: 131072,
	step: 128,
	allowDecimal: false,
	slider: false,
	localRuntimeOnly: true,
};

// Clamps a field's configured max down to maxContextTokens for the two context-sensitive params.
// Exported so consumers (dialog, tests) use the same logic without duplicating it.
export function clampFieldMax(meta: SamplingFieldMeta, maxContextTokens: number | undefined): number {
	if (maxContextTokens != null && (meta.key === "maxOutputTokens" || meta.key === "numCtx")) {
		return Math.min(meta.max, maxContextTokens);
	}
	return meta.max;
}

// Coerces a raw value to a finite number, returning undefined when it is not representable as one.
// Defends against strings that slipped into the store from partial NumberInput input or stale localStorage.
function toFiniteNumber(raw: unknown): number | undefined {
	if (raw == null) {
		return undefined;
	}
	const n = Number(raw);
	return Number.isFinite(n) ? n : undefined;
}

// The reverse of toWireSamplingOptions, for settings stored on the node: null and non-finite entries stay unset, and
// the string seed becomes a number again (the dialog bounds it well inside the safe-integer range).
export function fromWireSamplingOptions(
	wire: { [K in keyof WireSamplingOptions]?: WireSamplingOptions[K] | null },
): ChatSamplingOptions {
	const result: ChatSamplingOptions = {};
	for (const key of [
		"temperature",
		"topP",
		"topK",
		"minP",
		"maxOutputTokens",
		"repeatPenalty",
		"repeatLastN",
		"presencePenalty",
		"frequencyPenalty",
		"seed",
		"numCtx",
		"reasoningBudgetTokens",
	] as const) {
		const n = toFiniteNumber(wire[key]);
		if (n !== undefined) {
			result[key] = n;
		}
	}
	if (wire.stop != null && wire.stop.length > 0) {
		result.stop = [...wire.stop];
	}
	return result;
}

// Wire-safe serialization: coerces every numeric field to a real JS number and drops any that are
// not finite (covers string-typed store entries from partial Mantine NumberInput input). Drops null/
// undefined values and empty arrays. Returns undefined when nothing is set, signaling the caller to
// omit the samplingOptions field entirely (the byte-identical invariant: when all fields are null the
// wire payload must match the default non-developer path exactly).
export function toWireSamplingOptions(opts: ChatSamplingOptions): WireSamplingOptions | undefined {
	const result: WireSamplingOptions = {};
	let hasAny = false;

	const setNum = <K extends keyof Omit<ChatSamplingOptions, "stop" | "seed">>(key: K, raw: ChatSamplingOptions[K]): void => {
		const n = toFiniteNumber(raw);
		if (n !== undefined) {
			(result as Record<string, number>)[key] = n;
			hasAny = true;
		}
	};

	setNum("temperature", opts.temperature);
	setNum("topP", opts.topP);
	setNum("topK", opts.topK);
	setNum("minP", opts.minP);
	setNum("maxOutputTokens", opts.maxOutputTokens);
	setNum("repeatPenalty", opts.repeatPenalty);
	setNum("repeatLastN", opts.repeatLastN);
	setNum("presencePenalty", opts.presencePenalty);
	setNum("frequencyPenalty", opts.frequencyPenalty);
	setNum("numCtx", opts.numCtx);
	setNum("reasoningBudgetTokens", opts.reasoningBudgetTokens);

	// Seed rides the wire as a precision-safe string (see the backend SeedValue contract). The dialog bounds it to a
	// finite integer, so serialize the finite value and drop a non-finite/blank entry — matching the numeric fields.
	const seed = toFiniteNumber(opts.seed);
	if (seed !== undefined) {
		result.seed = String(seed);
		hasAny = true;
	}

	if (opts.stop != null && opts.stop.length > 0) {
		result.stop = opts.stop;
		hasAny = true;
	}

	return hasAny ? result : undefined;
}
