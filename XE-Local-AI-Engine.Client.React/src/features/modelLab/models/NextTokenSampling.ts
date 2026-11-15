import type { DirectInferenceLogprobAlternative } from "@/core/directInference/DirectInferenceModels";

export interface SamplerSettings {
	temperature: number;
	/** 0 or less turns top-k off. */
	topK: number;
	/** 1 or more turns top-p off. */
	topP: number;
	/** 0 or less turns min-p off. */
	minP: number;
}

// Each step a no-op, so the simulation starts as the shown candidates renormalized.
export const neutralSamplerSettings: SamplerSettings = { temperature: 1, topK: 0, topP: 1, minP: 0 };

export interface SimulatedCandidate {
	text?: string | null;
	/** The server's value, untouched. */
	logprob: number;
	/** The simulated probability over the shown candidates; 0 once a step cut it. */
	probability: number;
	/** False once a step cut it; a kept candidate's probability can still round to 0 at a low temperature. */
	kept: boolean;
}

function softmax(logprobs: readonly number[], temperature = 1): number[] {
	if (logprobs.length === 0) {
		return [];
	}
	const scaled = logprobs.map((value) => value / temperature);
	const max = Math.max(...scaled);
	const weights = scaled.map((value) => Math.exp(value - max));
	const total = weights.reduce((sum, weight) => sum + weight, 0);
	return weights.map((weight) => weight / total);
}

// shortcut: hand-copied from llama.cpp b12014; a pin bump that changes the chain or these samplers needs this re-checked.
// Order: common_params_sampling::samplers (common/common.h) runs top_k, top_p, min_p, then temperature last in the
// default chain; a model's GGUF sampling metadata (common_init_sampler_from_model) or the operator's launch arguments can
// change the order and the defaults. So top-p and min-p cut on the untempered values; penalties and the other samplers
// are not simulated. In src/llama-sampler.cpp: llama_sampler_top_k_impl, k <= 0 is off. llama_sampler_top_p_apply,
// p >= 1 is off, else it keeps through the first entry whose running sum reaches p (>=), so at least one.
// llama_sampler_min_p_apply, p <= 0 is off, else it keeps logits >= the highest + ln p, so the highest always stays.
// llama_sampler_temp_impl, T <= 0 keeps the highest only. The logprobs are get_token_probabilities (tools/server/server-common.cpp): a softmax over the
// raw logits, before any sampler.
// A logprob is the logit minus one shared constant, so a softmax over the shown logprobs is the softmax over their logits.
/** Applies top-k, top-p, min-p and temperature to the shown candidates only, sorted by probability, highest first. */
export function simulateSampling(
	candidates: readonly DirectInferenceLogprobAlternative[],
	settings: SamplerSettings,
): SimulatedCandidate[] {
	// Ties keep the server's list order; the server's own tie order is unspecified (std::partial_sort), so an exact tie
	// may pick differently there.
	const sorted = [...candidates].sort((left, right) => right.logprob - left.logprob);
	let kept = settings.topK > 0 ? Math.min(settings.topK, sorted.length) : sorted.length;
	if (settings.topP < 1 && kept > 0) {
		const probabilities = softmax(sorted.slice(0, kept).map((candidate) => candidate.logprob));
		let cumulative = 0;
		// Keeps candidates up to and including the one that reaches top-p, and always the first.
		const reached = probabilities.findIndex((probability) => {
			cumulative += probability;
			return cumulative >= settings.topP;
		});
		kept = reached === -1 ? kept : reached + 1;
	}
	if (settings.minP > 0 && kept > 0) {
		const threshold = (sorted[0]?.logprob ?? 0) + Math.log(settings.minP);
		kept = sorted.slice(0, kept).filter((candidate) => candidate.logprob >= threshold).length;
	}
	if (settings.temperature <= 0) {
		kept = Math.min(kept, 1);
	}
	const probabilities =
		settings.temperature <= 0
			? [1]
			: softmax(
					sorted.slice(0, kept).map((candidate) => candidate.logprob),
					settings.temperature,
				);
	return sorted.map((candidate, index) => ({
		text: candidate.text,
		logprob: candidate.logprob,
		probability: index < kept ? (probabilities[index] ?? 0) : 0,
		kept: index < kept,
	}));
}
