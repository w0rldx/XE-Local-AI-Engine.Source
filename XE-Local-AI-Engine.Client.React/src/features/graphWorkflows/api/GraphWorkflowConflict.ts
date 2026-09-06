import { ApiError } from "@/core/api/errors/ApiError";
import type { ConflictProblemDetails } from "@/core/api/models/ProblemDetails";

/**
 * The `conflictType` discriminators the node's global `ConflictExceptionHandler` writes for this module. They mirror
 * `NodeConflictProblemType` members (XE-Local-AI-Engine.Client/Common/ProblemDetailModels/Enums/), which serialize as
 * the enum NAME — rename one there and these strings must follow.
 *
 * Copied rather than imported from `devWorkflows/api/DevWorkflowConflict.ts`: features never import each other
 * (`no-cross-feature` + `config/dependency-baseline.json`), and the member set is this module's own.
 */
export const graphWorkflowConflictTypes = {
	/** A definition write lost the optimistic-concurrency race: someone saved over the row between the read and the write. */
	definitionConflict: "GraphWorkflowDefinitionConflict",
	/** The run is in a state that takes no such command — a cancel on a finished run, a decision on a node that moved on. */
	runConflict: "GraphWorkflowRunConflict",
	/**
	 * A DIFFERENT operation id arrived at an already-answered gate. Not a replay — a second human act — so the refusal
	 * carries the decision that stands, and the UI can say "someone already approved this" instead of "conflict".
	 */
	gateAlreadyDecided: "GraphWorkflowGateAlreadyDecided",
} as const;

export interface GraphWorkflowConflict {
	readonly conflictType: string;
	/** Set only for `GraphWorkflowGateAlreadyDecided`: the `Decision` name already on the node run. */
	readonly standingDecision?: string;
}

/**
 * Reads the 409 envelope off a thrown error, or `undefined` for anything that is not one.
 *
 * Matches the thrown `ApiError`, not an axios error: the response interceptor rethrows every non-2xx as `ApiError`, so
 * `isAxiosError` is already false by the time a component sees it.
 */
export function readGraphWorkflowConflict(error: unknown): GraphWorkflowConflict | undefined {
	if (!(error instanceof ApiError) || error.statusCode !== 409) {
		return undefined;
	}
	const problemDetails = error.apiProblemDetails as Partial<ConflictProblemDetails & { standingDecision: string }> | undefined;
	if (!problemDetails?.conflictType) {
		return undefined;
	}
	return { conflictType: problemDetails.conflictType, standingDecision: problemDetails.standingDecision };
}
