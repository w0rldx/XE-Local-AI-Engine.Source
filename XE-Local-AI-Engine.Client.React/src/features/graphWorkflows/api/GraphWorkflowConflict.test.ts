// The three 409 discriminators this module can receive, read off the thrown `ApiError` rather than off a message
// string. A conflictType that stops being recognised here does not throw — it degrades to a generic "conflict" toast
// and the panel keeps offering a decision the server has already refused, so the member names are asserted literally.

import { describe, expect, it } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";
import { graphWorkflowConflictTypes, readGraphWorkflowConflict } from "@/features/graphWorkflows/api/GraphWorkflowConflict";

function conflict(status: number, extras: Record<string, unknown>): ApiError {
	return new ApiError(status, {
		type: "about:blank",
		title: "Conflict",
		status,
		detail: "The request conflicts with the current state.",
		...extras,
	} as ProblemDetails);
}

describe("readGraphWorkflowConflict", () => {
	it("reads a definition write that lost the optimistic-concurrency race", () => {
		const error = conflict(409, { conflictType: "GraphWorkflowDefinitionConflict" });

		expect(readGraphWorkflowConflict(error)).toEqual({
			conflictType: graphWorkflowConflictTypes.definitionConflict,
			standingDecision: undefined,
		});
	});

	it("reads a run that is in no state to take the command", () => {
		const error = conflict(409, { conflictType: "GraphWorkflowRunConflict" });

		expect(readGraphWorkflowConflict(error)?.conflictType).toBe(graphWorkflowConflictTypes.runConflict);
	});

	it("carries the decision that stands when a gate was already answered", () => {
		// The one arm with a typed extra: `standingDecision` is what lets the panel say "someone already approved this"
		// instead of "conflict", so it must survive the read.
		const error = conflict(409, { conflictType: "GraphWorkflowGateAlreadyDecided", standingDecision: "Approve" });

		expect(readGraphWorkflowConflict(error)).toEqual({
			conflictType: graphWorkflowConflictTypes.gateAlreadyDecided,
			standingDecision: "Approve",
		});
	});

	it("reads nothing off anything that is not a 409 conflict envelope", () => {
		expect(readGraphWorkflowConflict(conflict(400, { conflictType: "GraphWorkflowRunConflict" }))).toBeUndefined();
		expect(readGraphWorkflowConflict(conflict(409, {}))).toBeUndefined();
		expect(readGraphWorkflowConflict(new Error("network down"))).toBeUndefined();
		expect(readGraphWorkflowConflict(undefined)).toBeUndefined();
	});
});
