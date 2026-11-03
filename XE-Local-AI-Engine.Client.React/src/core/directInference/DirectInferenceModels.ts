// Hand-written mirror of the chat hub's `StreamDirectInference` contract (DirectInferenceContracts.cs). Hub payloads are not
// in the OpenAPI document, so nothing generates these; keep the member names in step with the C# records.

import type { WireSamplingOptions } from "@/core/runtime/ChatSamplingOptions";

/**
 * The cap the server applies to a llama.cpp model when a request sets none; thinking counts toward it. Other models get no
 * default. `DirectInferenceServiceTests.ThePageStatesTheServersDefaultCap` holds this to `DirectInferenceService.DefaultMaxOutputTokens`.
 */
export const directInferenceDefaultMaxOutputTokens = 16384;

/** The most alternatives per entry the server accepts. `DirectInferenceServiceTests.ThePageStatesTheServersTopLogprobsLimit` holds this to `DirectInferenceService.MaxTopLogprobs`. */
export const directInferenceMaxTopLogprobs = 20;

// Accepted ceiling: only this many entries of each kind, reasoning and answer, are kept per run, so a long answer cannot
// grow the page or the copied JSON without bound, and long thinking cannot crowd out the answer. The view says what it left out.
export const directInferenceMaxKeptLogprobEntries = 500;

export interface DirectInferenceRequest {
	modelId: string;
	systemPrompt?: string;
	prompt: string;
	samplingOptions?: WireSamplingOptions;
	topLogprobs?: number;
}

export interface DirectInferenceUsage {
	inputTokens?: number | null;
	outputTokens?: number | null;
	totalTokens?: number | null;
	reasoningTokens?: number | null;
	cachedInputTokens?: number | null;
}

export interface DirectInferenceTimings {
	promptTokens?: number | null;
	promptMs?: number | null;
	generationTokens?: number | null;
	generationMs?: number | null;
	cachedPromptTokens?: number | null;
}

export interface DirectInferenceLogprobAlternative {
	text?: string | null;
	logprob: number;
}

/** One entry of the model's pre-sampling probabilities. Not a token: see `LlamaServerLogprobs` in the provider. */
export interface DirectInferenceLogprobEntry {
	text?: string | null;
	logprob: number;
	isReasoning: boolean;
	alternatives: DirectInferenceLogprobAlternative[];
}

export type DirectInferenceEventType = "delta" | "usage" | "timings" | "logprobs" | "done" | "error";

export interface DirectInferenceEvent {
	type: DirectInferenceEventType;
	textDelta?: string | null;
	reasoningDelta?: string | null;
	usage?: DirectInferenceUsage | null;
	timings?: DirectInferenceTimings | null;
	logprobs?: DirectInferenceLogprobEntry[] | null;
	elapsedMs?: number | null;
	firstTokenMs?: number | null;
	finishReason?: string | null;
	errorCode?: string | null;
	errorMessage?: string | null;
}

/** Everything one run produced, folded from its events; also the copy-as-JSON payload. */
export interface DirectInferenceResult {
	text: string;
	reasoning: string;
	usage?: DirectInferenceUsage;
	timings?: DirectInferenceTimings;
	logprobs?: DirectInferenceLogprobEntry[];
	/** Reasoning entries past `directInferenceMaxKeptLogprobEntries`. */
	reasoningLogprobsLeftOut?: number;
	/** Answer entries past `directInferenceMaxKeptLogprobEntries`. */
	answerLogprobsLeftOut?: number;
	elapsedMs?: number;
	firstTokenMs?: number;
	finishReason?: string;
	errorCode?: string;
	errorMessage?: string;
}

export const emptyDirectInferenceResult: DirectInferenceResult = { text: "", reasoning: "" };

// Once a kind is full its entries only count, and with nothing kept the array stays the same one, so a memoized view skips it.
function appendLogprobEntries(result: DirectInferenceResult, entries: DirectInferenceLogprobEntry[]): DirectInferenceResult {
	const current = result.logprobs ?? [];
	let reasoningRoom = directInferenceMaxKeptLogprobEntries - current.filter((entry) => entry.isReasoning).length;
	let answerRoom = directInferenceMaxKeptLogprobEntries - current.filter((entry) => !entry.isReasoning).length;
	const accepted = entries.filter((entry) => (entry.isReasoning ? reasoningRoom-- > 0 : answerRoom-- > 0));
	const reasoningLeftOut = (result.reasoningLogprobsLeftOut ?? 0) + Math.max(-reasoningRoom, 0);
	const answerLeftOut = (result.answerLogprobsLeftOut ?? 0) + Math.max(-answerRoom, 0);
	return {
		...result,
		logprobs: accepted.length > 0 ? [...current, ...accepted] : current,
		reasoningLogprobsLeftOut: reasoningLeftOut > 0 ? reasoningLeftOut : undefined,
		answerLogprobsLeftOut: answerLeftOut > 0 ? answerLeftOut : undefined,
	};
}

export function applyDirectInferenceEvent(result: DirectInferenceResult, event: DirectInferenceEvent): DirectInferenceResult {
	switch (event.type) {
		case "delta":
			return {
				...result,
				text: result.text + (event.textDelta ?? ""),
				reasoning: result.reasoning + (event.reasoningDelta ?? ""),
			};
		case "usage":
			return event.usage ? { ...result, usage: event.usage } : result;
		case "timings":
			return event.timings ? { ...result, timings: event.timings } : result;
		case "logprobs":
			return appendLogprobEntries(result, event.logprobs ?? []);
		case "done":
			return {
				...result,
				elapsedMs: event.elapsedMs ?? undefined,
				firstTokenMs: event.firstTokenMs ?? undefined,
				finishReason: event.finishReason ?? undefined,
			};
		case "error":
			return { ...result, errorCode: event.errorCode ?? undefined, errorMessage: event.errorMessage ?? undefined };
		default:
			return result;
	}
}

/** Tokens per second, or undefined when either side is missing or zero. */
export function tokensPerSecond(tokens: number | null | undefined, ms: number | null | undefined): number | undefined {
	return tokens != null && ms != null && tokens > 0 && ms > 0 ? (tokens * 1000) / ms : undefined;
}

/** A logprob as a percentage; a value that rounds to nothing, like llama-server's stand-in for zero, reads as a bound rather than 0. */
export function logprobPercentLabel(logprob: number): string {
	const percent = Math.exp(logprob) * 100;
	return percent < 0.01 ? "<0.01%" : `${percent.toFixed(2)}%`;
}

/** Entry text with its whitespace visible and quoted, because " the" and "the" are different alternatives. */
export function logprobTextLabel(text: string | null | undefined): string {
	return JSON.stringify(text ?? "");
}
