import { runDirectInference } from "@/core/directInference/DirectInferenceApi";
import type { DirectInferenceRequest, DirectInferenceResult } from "@/core/directInference/DirectInferenceModels";

/** Runs the requests one after the other. A failed run does not stop the next; an aborted signal does. */
export async function runInOrder(
	requests: DirectInferenceRequest[],
	onResult: (index: number, result: DirectInferenceResult) => void,
	signal: AbortSignal,
): Promise<void> {
	for (const [index, request] of requests.entries()) {
		if (signal.aborted) {
			return;
		}
		// biome-ignore lint/performance/noAwaitInLoops: one after the other is the point; the node serves one invocation at a time.
		await runDirectInference(request, (result) => onResult(index, result), signal);
	}
}

// One seed for both runs, drawn when none was typed: an empty field or the -1 "random" seed would give each run its own.
export function comparisonSeed(typed: number | undefined): number {
	return typed !== undefined && typed >= 0 ? typed : Math.floor(Math.random() * 2_147_483_647);
}
