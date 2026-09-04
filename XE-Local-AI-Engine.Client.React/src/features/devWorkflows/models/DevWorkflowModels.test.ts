// The WIRE-VOCABULARY guard. Every union here is a ruling (X4 statuses, Y6's seven node types, X3's six decision
// kinds, C24's ten artifact kinds, Y4's five work-item statuses), and every one of them is looked up as an i18n key
// after narrowing — so a member added on the server and not here renders the FALLBACK, silently. The literal lists
// are asserted rather than inferred for exactly that reason.

import type { TFunction } from "i18next";
import { describe, expect, it } from "vitest";

import {
	asDevWorkflowArtifactKind,
	asDevWorkflowDecisionKind,
	decodeDevWorkflowArtifactContent,
	devWorkflowArtifactKinds,
	devWorkflowArtifactLanguage,
	devWorkflowArtifactLineages,
	devWorkflowAttemptCounts,
	devWorkflowAttemptLabel,
	devWorkflowDecisionKinds,
	devWorkflowNodeAwaitsHuman,
	devWorkflowNodeStatuses,
	devWorkflowNodeTypes,
	devWorkflowRunStatuses,
	devWorkflowWorkItemStatuses,
	isActiveDevWorkflowRunStatus,
	isDevWorkflowNodeInProgress,
	isTerminalDevWorkflowRunStatus,
	toDevWorkflowDecisionKinds,
	toDevWorkflowNodeStatus,
	toDevWorkflowNodeType,
	toDevWorkflowRunStatus,
	toDevWorkflowWorkItemStatus,
} from "@/features/devWorkflows/models/DevWorkflowModels";
import { devWorkflowArtifact } from "@/features/devWorkflows/test/DevWorkflowFixtures";

describe("dev-workflow wire unions", () => {
	it("carries X4's nine node-run statuses verbatim", () => {
		expect([...devWorkflowNodeStatuses]).toEqual([
			"Pending",
			"Queued",
			"Running",
			"WaitingForApproval",
			"Blocked",
			"Succeeded",
			"Failed",
			"Skipped",
			"Cancelled",
		]);
	});

	it("carries X4's nine run statuses and deliberately no Interrupted", () => {
		expect([...devWorkflowRunStatuses]).toEqual([
			"Pending",
			"Running",
			"Pausing",
			"Paused",
			"WaitingForApproval",
			"Cancelling",
			"Completed",
			"Failed",
			"Cancelled",
		]);
		expect(devWorkflowRunStatuses).not.toContain("Interrupted");
	});

	it("carries Y6's SEVEN node types with Start and End implicit", () => {
		expect([...devWorkflowNodeTypes]).toEqual(["Agent", "Tool", "DevTask", "HumanGate", "Gate", "Parallel", "Join"]);
	});

	it("carries X3's six decision kinds, gates and interventions on one enum", () => {
		expect([...devWorkflowDecisionKinds]).toEqual(["Approve", "Reject", "RequestChanges", "Retry", "Skip", "Abandon"]);
	});

	it("carries P1's ten artifact kinds, not the work-session set", () => {
		expect([...devWorkflowArtifactKinds]).toEqual([
			"Research",
			"Decision",
			"Specification",
			"Plan",
			"TaskPackage",
			"Patch",
			"Report",
			"Finding",
			"ValidationReport",
			"ReviewReport",
		]);
	});

	it("carries Y4's five work-item statuses", () => {
		expect([...devWorkflowWorkItemStatuses]).toEqual(["Draft", "Active", "Blocked", "Completed", "Cancelled"]);
	});
});

describe("dev-workflow narrowing", () => {
	it("passes every known member through untouched", () => {
		for (const status of devWorkflowNodeStatuses) {
			expect(toDevWorkflowNodeStatus(status)).toBe(status);
		}
		for (const status of devWorkflowRunStatuses) {
			expect(toDevWorkflowRunStatus(status)).toBe(status);
		}
		for (const type of devWorkflowNodeTypes) {
			expect(toDevWorkflowNodeType(type)).toBe(type);
		}
		for (const status of devWorkflowWorkItemStatuses) {
			expect(toDevWorkflowWorkItemStatus(status)).toBe(status);
		}
	});

	it("reads an unknown or absent node status as Pending — the state with no controls", () => {
		expect(toDevWorkflowNodeStatus("Exploded")).toBe("Pending");
		expect(toDevWorkflowNodeStatus(undefined)).toBe("Pending");
		expect(toDevWorkflowNodeStatus(null)).toBe("Pending");
		// The round-1 rename traps: neither old spelling may resolve to itself.
		expect(toDevWorkflowNodeStatus("WaitingForHuman")).toBe("Pending");
		expect(toDevWorkflowNodeStatus("Completed")).toBe("Pending");
	});

	it("reads an unknown run status as Pending, so no lifecycle command is offered for it", () => {
		expect(toDevWorkflowRunStatus("Interrupted")).toBe("Pending");
		expect(toDevWorkflowRunStatus(undefined)).toBe("Pending");
	});

	it("reads an unknown node type as Gate — the structural panel with no link-outs", () => {
		expect(toDevWorkflowNodeType("Subworkflow")).toBe("Gate");
		expect(toDevWorkflowNodeType("Start")).toBe("Gate");
		expect(toDevWorkflowNodeType(undefined)).toBe("Gate");
	});

	it("reads an unknown work-item status as Draft", () => {
		expect(toDevWorkflowWorkItemStatus("Archived")).toBe("Draft");
		expect(toDevWorkflowWorkItemStatus(undefined)).toBe("Draft");
	});

	it("drops an unrecognised decision kind rather than substituting one", () => {
		expect(asDevWorkflowDecisionKind("Approve")).toBe("Approve");
		expect(asDevWorkflowDecisionKind("Escalate")).toBeUndefined();
		expect(asDevWorkflowDecisionKind(undefined)).toBeUndefined();
	});

	it("filters allowedDecisions to renderable members, preserving the server's order", () => {
		expect(toDevWorkflowDecisionKinds(["Reject", "Escalate", "Approve"])).toEqual(["Reject", "Approve"]);
		expect(toDevWorkflowDecisionKinds(undefined)).toEqual([]);
	});

	it("drops an unrecognised artifact kind rather than mislabelling it", () => {
		expect(asDevWorkflowArtifactKind("ValidationReport")).toBe("ValidationReport");
		expect(asDevWorkflowArtifactKind("Note")).toBeUndefined();
	});
});

describe("dev-workflow status predicates", () => {
	it("counts Pausing and Cancelling as active — work is still winding down behind a fire-and-forget command", () => {
		expect(isActiveDevWorkflowRunStatus("Pausing")).toBe(true);
		expect(isActiveDevWorkflowRunStatus("Cancelling")).toBe(true);
		expect(isActiveDevWorkflowRunStatus("Paused")).toBe(true);
		expect(isTerminalDevWorkflowRunStatus("Cancelled")).toBe(true);
		expect(isActiveDevWorkflowRunStatus("Completed")).toBe(false);
	});

	it("treats Blocked as needs-intervention alongside a gate (Y20), not as a passive wait", () => {
		expect(devWorkflowNodeAwaitsHuman("Blocked")).toBe(true);
		expect(devWorkflowNodeAwaitsHuman("WaitingForApproval")).toBe(true);
		expect(devWorkflowNodeAwaitsHuman("Pending")).toBe(false);
	});

	it("grants motion to Running only — a Queued node is waiting for a slot, not working", () => {
		expect(isDevWorkflowNodeInProgress("Running")).toBe(true);
		expect(isDevWorkflowNodeInProgress("Queued")).toBe(false);
		expect(isDevWorkflowNodeInProgress("Pending")).toBe(false);
	});
});

describe("dev-workflow artifact rendering", () => {
	it("maps kinds to editor languages, defaulting to markdown", () => {
		expect(devWorkflowArtifactLanguage("Patch", "text/plain")).toBe("diff");
		expect(devWorkflowArtifactLanguage("TaskPackage", "application/json")).toBe("json");
		expect(devWorkflowArtifactLanguage("Plan", "text/markdown")).toBe("markdown");
		expect(devWorkflowArtifactLanguage(undefined, "application/json")).toBe("json");
		expect(devWorkflowArtifactLanguage(undefined, undefined)).toBe("markdown");
	});

	it("reports undecodable base64 as binary instead of rendering replacement characters", () => {
		expect(decodeDevWorkflowArtifactContent("plain text", false)).toEqual({ text: "plain text", isBinary: false });
		expect(decodeDevWorkflowArtifactContent(btoa("hello"), true)).toEqual({ text: "hello", isBinary: false });
		// 0xFF is not valid UTF-8; the fatal TextDecoder is what turns it into an honest "binary" answer.
		expect(decodeDevWorkflowArtifactContent(btoa("ÿþ"), true).isBinary).toBe(true);
	});
});

describe("devWorkflowArtifactLineages", () => {
	const artifact = (id: string, lineageId: string, version: number, isLatest: boolean) =>
		devWorkflowArtifact({ id, lineageId, version, isLatest });

	it("collapses every version of one lineage into a single group, newest version first", () => {
		const lineages = devWorkflowArtifactLineages([
			artifact("a1", "lineage-a", 1, false),
			artifact("a3", "lineage-a", 3, true),
			artifact("a2", "lineage-a", 2, false),
		]);

		expect(lineages).toHaveLength(1);
		expect(lineages[0]?.versions.map((row) => row.id)).toEqual(["a3", "a2", "a1"]);
	});

	it("reads `isLatest` off the wire rather than deriving it from the highest version (Y17/C39)", () => {
		// A deliberately inconsistent feed: the server marked v2 latest. Deriving would have answered v3.
		const lineages = devWorkflowArtifactLineages([
			artifact("a3", "lineage-a", 3, false),
			artifact("a2", "lineage-a", 2, true),
		]);

		expect(lineages[0]?.latest.id).toBe("a2");
	});

	it("falls back to the highest version when no row claims to be the latest", () => {
		const lineages = devWorkflowArtifactLineages([
			artifact("a1", "lineage-a", 1, false),
			artifact("a2", "lineage-a", 2, false),
		]);

		expect(lineages[0]?.latest.id).toBe("a2");
	});

	it("keeps lineages in first-appearance order and never merges two of them", () => {
		const lineages = devWorkflowArtifactLineages([
			artifact("b1", "lineage-b", 1, true),
			artifact("a1", "lineage-a", 1, true),
		]);

		expect(lineages.map((lineage) => lineage.lineageId)).toEqual(["lineage-b", "lineage-a"]);
	});

	it("gives a row with no lineage id a lineage of its own rather than merging unrelated documents", () => {
		const lineages = devWorkflowArtifactLineages([
			devWorkflowArtifact({ id: "x", lineageId: undefined, name: "one.md" }),
			devWorkflowArtifact({ id: "y", lineageId: undefined, name: "two.md" }),
		]);

		expect(lineages).toHaveLength(2);
		expect(lineages.map((lineage) => lineage.latest.name)).toEqual(["one.md", "two.md"]);
	});
});

describe("devWorkflowAttemptCounts", () => {
	it("reads back the declared cap under an operator retry, which widened the budget the runtime works to", () => {
		expect(devWorkflowAttemptCounts(4, 4, 1)).toEqual({ attempt: 4, maxAttempts: 4, cap: 3, operatorRetries: 1 });
		expect(devWorkflowAttemptCounts(5, 5, 2)).toEqual({ attempt: 5, maxAttempts: 5, cap: 3, operatorRetries: 2 });
	});

	it("leaves an ordinary attempt alone and defaults every half to 1", () => {
		expect(devWorkflowAttemptCounts(2, 3, 0)).toEqual({ attempt: 2, maxAttempts: 3, cap: 3, operatorRetries: 0 });
		expect(devWorkflowAttemptCounts(undefined, undefined, undefined)).toEqual({
			attempt: 1,
			maxAttempts: 1,
			cap: 1,
			operatorRetries: 0,
		});
		expect(devWorkflowAttemptCounts(null, null, null)).toEqual({ attempt: 1, maxAttempts: 1, cap: 1, operatorRetries: 0 });
	});

	it("treats a server that has no operatorRetries field as having granted none", () => {
		expect(devWorkflowAttemptCounts(2, 3)).toEqual({ attempt: 2, maxAttempts: 3, cap: 3, operatorRetries: 0 });
	});

	it("still clamps the maximum up to the attempt, the compatibility guard for a server from before the widening", () => {
		expect(devWorkflowAttemptCounts(4, 3)).toEqual({ attempt: 4, maxAttempts: 4, cap: 4, operatorRetries: 0 });
	});

	it("never reports a declared cap below 1, however many retries the row claims", () => {
		expect(devWorkflowAttemptCounts(2, 2, 9)).toEqual({ attempt: 2, maxAttempts: 2, cap: 1, operatorRetries: 9 });
	});
});

describe("devWorkflowAttemptLabel", () => {
	// The real bundle answers this in the component tests; here the interpolation is what is under test, so a stub `t`
	// that echoes its key and values proves which key was picked and what was handed to it.
	const t = ((key: string, _fallback: string, values: Record<string, unknown>) =>
		`${key} ${JSON.stringify(values)}`) as unknown as TFunction;

	it("says 'of M' when no human granted the attempt", () => {
		expect(devWorkflowAttemptLabel(t, devWorkflowAttemptCounts(2, 3, 0))).toContain("pages.devWorkflows.nodes.attempt ");
	});

	it("hands the retry key both maxima and the retry count, so the copy can say what capacity was added", () => {
		const label = devWorkflowAttemptLabel(t, devWorkflowAttemptCounts(5, 5, 2));

		expect(label).toContain("pages.devWorkflows.nodes.attemptOperatorRetry");
		expect(label).toContain('"maxAttempts":5');
		expect(label).toContain('"cap":3');
		expect(label).toContain('"count":2');
	});
});
