// Developer-mode per-send sampling overrides. All fields are optional; absent = use model default.
// The wire shape (camelCase JSON field `samplingOptions`) matches the backend SamplingOptions record.

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
}

// Per-field range metadata drives the dialog inputs. labelKey is the i18n key for the field label.
export interface SamplingFieldMeta {
	key: keyof ChatSamplingOptions;
	labelKey: string;
	descriptionKey: string;
	min: number;
	max: number;
	step: number;
	decimalScale?: number;
	allowDecimal: boolean;
	// When true the dialog renders a Slider paired with a NumberInput; false = number-only (e.g. seed).
	slider: boolean;
}

export const samplingFieldGroups: { groupKey: string; fields: SamplingFieldMeta[] }[] = [
	{
		groupKey: "pages.chat.samplingOptions.groupSampling",
		fields: [
			{
				key: "temperature",
				labelKey: "pages.chat.samplingOptions.temperature",
				descriptionKey: "pages.chat.samplingOptions.temperatureDescription",
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
				min: 1,
				max: 200,
				step: 1,
				allowDecimal: false,
				slider: true,
			},
			{
				key: "minP",
				labelKey: "pages.chat.samplingOptions.minP",
				descriptionKey: "pages.chat.samplingOptions.minPDescription",
				min: 0,
				max: 1,
				step: 0.05,
				decimalScale: 2,
				allowDecimal: true,
				slider: true,
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
				min: 0,
				max: 2,
				step: 0.05,
				decimalScale: 2,
				allowDecimal: true,
				slider: true,
			},
			{
				key: "repeatLastN",
				labelKey: "pages.chat.samplingOptions.repeatLastN",
				descriptionKey: "pages.chat.samplingOptions.repeatLastNDescription",
				min: -1,
				max: 512,
				step: 1,
				allowDecimal: false,
				slider: true,
			},
			{
				key: "presencePenalty",
				labelKey: "pages.chat.samplingOptions.presencePenalty",
				descriptionKey: "pages.chat.samplingOptions.presencePenaltyDescription",
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
				min: 512,
				max: 131072,
				step: 512,
				allowDecimal: false,
				slider: true,
			},
		],
	},
];

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

// Wire-safe serialization: coerces every numeric field to a real JS number and drops any that are
// not finite (covers string-typed store entries from partial Mantine NumberInput input). Drops null/
// undefined values and empty arrays. Returns undefined when nothing is set, signaling the caller to
// omit the samplingOptions field entirely (the byte-identical invariant: when all fields are null the
// wire payload must match the default non-developer path exactly).
export function toWireSamplingOptions(opts: ChatSamplingOptions): ChatSamplingOptions | undefined {
	const result: ChatSamplingOptions = {};
	let hasAny = false;

	const setNum = <K extends keyof Omit<ChatSamplingOptions, "stop">>(key: K, raw: ChatSamplingOptions[K]): void => {
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
	setNum("seed", opts.seed);
	setNum("numCtx", opts.numCtx);

	if (opts.stop != null && opts.stop.length > 0) {
		result.stop = opts.stop;
		hasAny = true;
	}

	return hasAny ? result : undefined;
}
