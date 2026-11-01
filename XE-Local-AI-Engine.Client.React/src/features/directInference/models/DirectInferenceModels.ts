// Hand-written mirror of the chat hub's `StreamDirectInference` contract (DirectInferenceContracts.cs). Hub payloads are not
// in the OpenAPI document, so nothing generates these; keep the member names in step with the C# records.

import type { WireSamplingOptions } from "@/core/runtime/ChatSamplingOptions";

export interface DirectInferenceRequest {
	modelId: string;
	systemPrompt?: string;
	prompt: string;
	samplingOptions?: WireSamplingOptions;
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

export type DirectInferenceEventType = "delta" | "usage" | "timings" | "done" | "error";

export interface DirectInferenceEvent {
	type: DirectInferenceEventType;
	textDelta?: string | null;
	reasoningDelta?: string | null;
	usage?: DirectInferenceUsage | null;
	timings?: DirectInferenceTimings | null;
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
	elapsedMs?: number;
	firstTokenMs?: number;
	finishReason?: string;
	errorCode?: string;
	errorMessage?: string;
}

export const emptyDirectInferenceResult: DirectInferenceResult = { text: "", reasoning: "" };

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
