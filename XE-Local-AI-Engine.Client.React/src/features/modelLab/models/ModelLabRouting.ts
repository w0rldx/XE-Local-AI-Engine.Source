import type {
	XeLocalAiEngineClientEndpointsModelLabV1ModelLabRoutingBlockResponse as RoutingBlock,
	XeLocalAiEngineClientEndpointsModelLabV1ModelLabRoutingStepResponse as RoutingStep,
} from "@/core/api/generated/types.gen";

export interface ExpertChoice {
	expert: number;
	weight: number;
}

export interface ExpertUsage {
	expert: number;
	// Steps in which the block chose this expert.
	picks: number;
}

// One block's chosen experts, heaviest first; equal weights keep the lower expert id first.
export function expertChoices(block: RoutingBlock): ExpertChoice[] {
	return block.experts
		.map((expert, at) => ({ expert, weight: block.weights[at] ?? 0 }))
		.sort((a, b) => b.weight - a.weight || a.expert - b.expert);
}

// How often each expert of one block was chosen over the recorded steps, most picked first, ties by expert id. A step
// counts each expert at most once: the server refuses a row that repeats an id.
export function expertUsage(steps: readonly RoutingStep[], layer: number): ExpertUsage[] {
	const picks = new Map<number, number>();
	for (const step of steps) {
		for (const expert of step.blocks.find((block) => block.layer === layer)?.experts ?? []) {
			picks.set(expert, (picks.get(expert) ?? 0) + 1);
		}
	}
	return [...picks].map(([expert, count]) => ({ expert, picks: count })).sort((a, b) => b.picks - a.picks || a.expert - b.expert);
}
