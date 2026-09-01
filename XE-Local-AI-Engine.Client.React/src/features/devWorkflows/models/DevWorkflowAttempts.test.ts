// The attempt walk, which is the only reconstruction of history the single-row node-run schema allows (X2).

import { describe, expect, it } from "vitest";

import { devWorkflowNodeAttempts, devWorkflowNodeEvents } from "@/features/devWorkflows/models/DevWorkflowAttempts";
import { devWorkflowRunEvent } from "@/features/devWorkflows/test/DevWorkflowFixtures";

const nodeRunId = "11111111-1111-4111-8111-111111111111";

function event(sequence: number, eventType: string, overrides: Parameters<typeof devWorkflowRunEvent>[0] = {}) {
	return devWorkflowRunEvent({ id: `e${sequence}`, sequence, eventType, nodeRunId, ...overrides });
}

describe("devWorkflowNodeAttempts", () => {
	it("closes an attempt on each retry and opens the next, carrying the outcome the event reported", () => {
		const attempts = devWorkflowNodeAttempts(
			[
				event(1, "node.retry.scheduled", { outcome: "provider-error" }),
				event(2, "node.retry.scheduled", { outcome: "timeout" }),
				event(3, "node.completed", { outcome: "succeeded" }),
			],
			3,
		);

		expect(attempts.map((attempt) => [attempt.attempt, attempt.outcome])).toEqual([
			[1, "provider-error"],
			[2, "timeout"],
			[3, "succeeded"],
		]);
	});

	it("believes the event's own attempt number over the counter, because the log is the authority", () => {
		// A feed whose earlier pages are not loaded starts mid-history: the counter would say "attempt 1", and the
		// attach event says 3. Placing the session on attempt 1 would attribute a transcript to the wrong attempt.
		const attempts = devWorkflowNodeAttempts(
			[event(9, "worksession.attached", { detailJson: JSON.stringify({ workSessionId: "s-3", attempt: 3 }) })],
			3,
		);

		expect(attempts.find((attempt) => attempt.attempt === 3)?.workSessionId).toBe("s-3");
		expect(attempts.find((attempt) => attempt.attempt === 1)?.workSessionId).toBeUndefined();
	});

	it("carries the counter forward from a stated attempt, so what follows an attachment is not filed under attempt 1", () => {
		// The tail-anchored page set: the `node.retry.scheduled` events that grew the counter are not loaded, and the
		// oldest row that IS loaded is attempt 2's attachment. Recording the session on attempt 2 while leaving the
		// counter at 1 files the interruption and the close that follow under attempt 1 — evidence of the second
		// attempt shown as the first's.
		const attempts = devWorkflowNodeAttempts(
			[
				event(40, "worksession.attached", { detailJson: JSON.stringify({ workSessionId: "s-2", attempt: 2 }) }),
				event(41, "node.interrupted"),
				event(42, "node.completed", { outcome: "succeeded" }),
			],
			2,
		);

		expect(attempts.map((attempt) => [attempt.attempt, attempt.interruptedCount, attempt.outcome])).toEqual([
			[1, 0, undefined],
			[2, 1, "succeeded"],
		]);
	});

	it("takes the attempt a retry states as the one it closes", () => {
		// `node.retry.scheduled` carries the attempt that FAILED, so a page set opening on one states where history is
		// just as an attachment does — and the attempt it opens is the one after that, not the one after 1.
		const attempts = devWorkflowNodeAttempts(
			[
				event(50, "node.retry.scheduled", { outcome: "timeout", detailJson: JSON.stringify({ attempt: 2 }) }),
				event(51, "node.completed", { outcome: "succeeded" }),
			],
			3,
		);

		expect(attempts.map((attempt) => [attempt.attempt, attempt.outcome])).toEqual([
			[1, undefined],
			[2, "timeout"],
			[3, "succeeded"],
		]);
	});

	it("never moves the counter backwards", () => {
		// A stated number older than where the walk already is cannot rewind it: the events after it belong to the
		// attempt the walk had reached, not to the one an out-of-order payload names.
		const attempts = devWorkflowNodeAttempts(
			[
				event(1, "node.retry.scheduled", { outcome: "timeout" }),
				event(2, "worksession.attached", { detailJson: JSON.stringify({ workSessionId: "s-old", attempt: 1 }) }),
				event(3, "node.completed", { outcome: "succeeded" }),
			],
			2,
		);

		expect(attempts.map((attempt) => [attempt.attempt, attempt.outcome])).toEqual([
			[1, "timeout"],
			[2, "succeeded"],
		]);
		expect(attempts.find((attempt) => attempt.attempt === 1)?.workSessionId).toBe("s-old");
	});

	it("lists every attempt the node-run row claims, even the ones no loaded event accounts for", () => {
		// The row is the authority on HOW MANY. A list that stopped at the loaded evidence would read as "never retried".
		const attempts = devWorkflowNodeAttempts([], 3);

		expect(attempts.map((attempt) => attempt.attempt)).toEqual([1, 2, 3]);
		expect(attempts.every((attempt) => attempt.outcome === undefined)).toBe(true);
	});

	it("counts interruptions against the attempt they happened in", () => {
		const attempts = devWorkflowNodeAttempts(
			[event(1, "node.interrupted"), event(2, "node.retry.scheduled"), event(3, "node.interrupted")],
			2,
		);

		expect(attempts.map((attempt) => attempt.interruptedCount)).toEqual([1, 1]);
	});

	it("keeps another node's rows out, and sorts what is left by sequence", () => {
		const filtered = devWorkflowNodeEvents(
			[
				event(5, "node.completed"),
				devWorkflowRunEvent({ id: "other", sequence: 2, eventType: "node.completed", nodeRunId: "other-node" }),
				event(1, "node.started"),
			],
			nodeRunId,
		);

		expect(filtered.map((entry) => entry.sequence)).toEqual([1, 5]);
	});
});
