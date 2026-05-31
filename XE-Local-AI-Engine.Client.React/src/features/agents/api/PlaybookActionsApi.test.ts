import { describe, expect, it, vi } from "vitest";

const { axiosInstanceMock, buildLocalApiUrlMock } = vi.hoisted(() => ({
	axiosInstanceMock: {
		delete: vi.fn(),
		get: vi.fn(),
		post: vi.fn(),
		put: vi.fn(),
	},
	buildLocalApiUrlMock: vi.fn((path: string) => `/local/${path}`),
}));

vi.mock("@/core/api/axios/AxiosInstance", () => ({
	axiosInstance: axiosInstanceMock,
}));

vi.mock("@/core/api/utils/LocalApiUrl", () => ({
	buildLocalApiUrl: buildLocalApiUrlMock,
}));

import { ApiError } from "@/core/api/errors/ApiError";
import {
	analyzePlaybook,
	createPlaybookAction,
	deletePlaybookAction,
	listPlaybookActions,
	promoteSuggested,
	PromoteConflictError,
	rejectSuggested,
	runEval,
	updatePlaybookAction,
	updateSuggested,
} from "@/features/agents/api/PlaybookActionsApi";
import type {
	PlaybookActionDto,
	SavePlaybookActionRequestDto,
	SaveSuggestedActionRequestDto,
} from "@/features/agents/models/PlaybookActionModels";

function makeDto(overrides: Partial<PlaybookActionDto> = {}): PlaybookActionDto {
	return {
		id: "action-1",
		agentDefinitionId: "agent-1",
		state: "Enabled",
		source: "Manual",
		triggerCondition: "When asked for code",
		behavior: "Always include tests",
		scope: "coding",
		priority: 0,
		version: 1,
		createdAtUtc: 1000,
		updatedAtUtc: 2000,
		sourceFeedbackIds: null,
		confidence: null,
		evalResult: null,
		...overrides,
	};
}

const sampleRequest: SavePlaybookActionRequestDto = {
	state: "Enabled",
	triggerCondition: "When asked for code",
	behavior: "Always include tests",
	scope: "coding",
	priority: 0,
};

describe("playbook actions API", () => {
	it("lists playbook actions for an agent, mapping DTOs and forwarding the abort signal", async () => {
		const abortController = new AbortController();
		axiosInstanceMock.get.mockResolvedValue({ data: { items: [makeDto()] } });

		const result = await listPlaybookActions("agent-1", { signal: abortController.signal });

		expect(axiosInstanceMock.get).toHaveBeenCalledWith("/local/agents/agent-1/playbook", {
			signal: abortController.signal,
		});
		expect(result).toHaveLength(1);
		expect(result[0]?.behavior).toBe("Always include tests");
		expect(result[0]?.state).toBe("Enabled");
		expect(result[0]?.source).toBe("Manual");
	});

	it("maps a null trigger/scope and an unknown state/source defensively", async () => {
		axiosInstanceMock.get.mockResolvedValue({
			data: { items: [makeDto({ triggerCondition: null, scope: null, state: "weird", source: "weird" })] },
		});

		const result = await listPlaybookActions("agent-1");

		expect(result[0]?.triggerCondition).toBeNull();
		expect(result[0]?.scope).toBeNull();
		// Unknown state falls back to Disabled (the non-injecting state); unknown source falls back to Manual.
		expect(result[0]?.state).toBe("Disabled");
		expect(result[0]?.source).toBe("Manual");
	});

	it("creates a playbook action through POST under the agent route", async () => {
		axiosInstanceMock.post.mockResolvedValue({ data: makeDto() });

		const result = await createPlaybookAction("agent-1", sampleRequest);

		expect(axiosInstanceMock.post).toHaveBeenCalledWith("/local/agents/agent-1/playbook", sampleRequest, undefined);
		expect(result.id).toBe("action-1");
	});

	it("updates a playbook action through PUT with encoded ids", async () => {
		axiosInstanceMock.put.mockResolvedValue({ data: makeDto({ id: "a/b" }) });

		await updatePlaybookAction("ag/1", "a/b", sampleRequest);

		expect(axiosInstanceMock.put).toHaveBeenCalledWith("/local/agents/ag%2F1/playbook/a%2Fb", sampleRequest, undefined);
	});

	it("edits a Suggested action through PUT .../suggested with a state-less body and encoded ids", async () => {
		const suggestedRequest: SaveSuggestedActionRequestDto = {
			behavior: "Summarize before answering",
			triggerCondition: null,
			scope: null,
			priority: 2,
		};
		axiosInstanceMock.put.mockResolvedValue({
			data: makeDto({ id: "s/1", state: "Suggested", source: "Analysis" }),
		});

		const result = await updateSuggested("ag/1", "s/1", suggestedRequest);

		// Dedicated `/suggested` route; the body carries no `state` field (the action stays Suggested).
		expect(axiosInstanceMock.put).toHaveBeenCalledWith(
			"/local/agents/ag%2F1/playbook/s%2F1/suggested",
			suggestedRequest,
			undefined,
		);
		expect(suggestedRequest).not.toHaveProperty("state");
		expect(result.state).toBe("Suggested");
	});

	it("deletes a playbook action through DELETE under the agent route", async () => {
		axiosInstanceMock.delete.mockResolvedValue({ data: undefined });

		await deletePlaybookAction("agent-1", "action-1");

		expect(axiosInstanceMock.delete).toHaveBeenCalledWith("/local/agents/agent-1/playbook/action-1", undefined);
	});

	it("parses the P3 analysis fields for an Analysis action and leaves them null for a Manual action", async () => {
		axiosInstanceMock.get.mockResolvedValue({
			data: {
				items: [
					makeDto(),
					makeDto({
						id: "suggested-1",
						state: "Suggested",
						source: "Analysis",
						sourceFeedbackIds: ["fb-1", "fb-2"],
						confidence: 0.75,
					}),
				],
			},
		});

		const result = await listPlaybookActions("agent-1");

		// Manual action: both analysis fields stay null.
		expect(result[0]?.source).toBe("Manual");
		expect(result[0]?.sourceFeedbackIds).toBeNull();
		expect(result[0]?.confidence).toBeNull();
		// Analysis action: ids + confidence carried through.
		expect(result[1]?.source).toBe("Analysis");
		expect(result[1]?.state).toBe("Suggested");
		expect(result[1]?.sourceFeedbackIds).toEqual(["fb-1", "fb-2"]);
		expect(result[1]?.confidence).toBe(0.75);
	});

	it("degrades unknown/missing/out-of-range analysis fields to null without throwing", async () => {
		axiosInstanceMock.get.mockResolvedValue({
			data: {
				items: [
					// Field omitted entirely (older backend) and a garbage confidence + non-array ids.
					makeDto({ sourceFeedbackIds: undefined, confidence: undefined }),
					makeDto({
						id: "bad-1",
						// Out-of-range confidence and a non-string-array ids value (cast through unknown for the boundary test).
						confidence: 5 as unknown as number,
						sourceFeedbackIds: "not-an-array" as unknown as string[],
					}),
				],
			},
		});

		const result = await listPlaybookActions("agent-1");

		expect(result[0]?.sourceFeedbackIds).toBeNull();
		expect(result[0]?.confidence).toBeNull();
		// Out-of-range / wrong-typed values degrade to null rather than blanking the whole parse.
		expect(result[1]?.confidence).toBeNull();
		expect(result[1]?.sourceFeedbackIds).toBeNull();
		expect(result[1]?.behavior).toBe("Always include tests");
	});

	it("runs analysis through POST .../playbook/analyze and maps the returned Suggested actions", async () => {
		axiosInstanceMock.post.mockResolvedValue({
			data: { items: [makeDto({ id: "suggested-1", state: "Suggested", source: "Analysis", confidence: 0.9 })] },
		});

		const result = await analyzePlaybook("agent-1");

		// An empty JSON object body (not undefined) is sent so axios sets Content-Type — FastEndpoints 415s a bodyless POST.
		expect(axiosInstanceMock.post).toHaveBeenCalledWith("/local/agents/agent-1/playbook/analyze", {}, undefined);
		const analyzeBody = axiosInstanceMock.post.mock.calls.at(-1)?.at(1);
		expect(analyzeBody).toEqual({});
		expect(analyzeBody).not.toBeUndefined();
		expect(result).toHaveLength(1);
		expect(result[0]?.state).toBe("Suggested");
		expect(result[0]?.confidence).toBe(0.9);
	});

	it("returns an empty array when analysis produces no suggestions", async () => {
		axiosInstanceMock.post.mockResolvedValue({ data: { items: [] } });

		const result = await analyzePlaybook("agent-1");

		expect(result).toEqual([]);
	});

	it("promotes a Suggested action through POST .../promote with encoded ids", async () => {
		axiosInstanceMock.post.mockResolvedValue({ data: makeDto({ id: "s/1", state: "Enabled", source: "Analysis" }) });

		const result = await promoteSuggested("ag/1", "s/1");

		// Empty JSON object body (not undefined) — FastEndpoints 415s a bodyless POST.
		expect(axiosInstanceMock.post).toHaveBeenCalledWith("/local/agents/ag%2F1/playbook/s%2F1/promote", {}, undefined);
		expect(axiosInstanceMock.post.mock.calls.at(-1)?.at(1)).toEqual({});
		expect(result.state).toBe("Enabled");
	});

	it("rejects a Suggested action through POST .../reject with encoded ids", async () => {
		axiosInstanceMock.post.mockResolvedValue({ data: makeDto({ id: "s/1", state: "Archived", source: "Analysis" }) });

		const result = await rejectSuggested("ag/1", "s/1");

		// Empty JSON object body (not undefined) — FastEndpoints 415s a bodyless POST.
		expect(axiosInstanceMock.post).toHaveBeenCalledWith("/local/agents/ag%2F1/playbook/s%2F1/reject", {}, undefined);
		expect(axiosInstanceMock.post.mock.calls.at(-1)?.at(1)).toEqual({});
		expect(result.state).toBe("Archived");
	});

	it("runs the eval gate through POST .../eval with an empty body and maps the returned evalResult", async () => {
		const evalResult = {
			passed: true,
			evaluatedAtUtc: 1748600000000,
			actionVersionAtEval: 1,
			modelName: "qwen3.5:9b",
			goldenCaseCount: 3,
			baselinePassCount: 3,
			candidatePassCount: 3,
			regressedCaseCount: 0,
			improvedCaseCount: 0,
			cases: [{ goldenCaseId: "g-1", scoredBy: "assertion", baselinePass: true, candidatePass: true, regressed: false }],
		};
		axiosInstanceMock.post.mockResolvedValue({
			data: makeDto({ id: "s/1", state: "Suggested", source: "Analysis", evalResult }),
		});

		const result = await runEval("ag/1", "s/1");

		// Empty JSON object body (not undefined) — FastEndpoints 415s a bodyless POST; ids ride the route.
		expect(axiosInstanceMock.post).toHaveBeenCalledWith("/local/agents/ag%2F1/playbook/s%2F1/eval", {}, undefined);
		// The shared post mock accumulates calls across tests — assert the LAST call's body.
		expect(axiosInstanceMock.post.mock.calls.at(-1)?.at(1)).toEqual({});
		expect(result.evalResult?.passed).toBe(true);
		expect(result.evalResult?.regressedCaseCount).toBe(0);
		expect(result.evalResult?.cases[0]?.goldenCaseId).toBe("g-1");
		expect(result.evalResult?.cases[0]?.scoredBy).toBe("assertion");
	});

	it("degrades an unknown/missing evalResult to null without throwing", async () => {
		axiosInstanceMock.get.mockResolvedValue({
			data: {
				items: [
					// Field omitted entirely (older backend) and a garbage non-object evalResult.
					makeDto({ evalResult: undefined }),
					makeDto({ id: "bad-1", evalResult: "not-an-object" as unknown }),
				],
			},
		});

		const result = await listPlaybookActions("agent-1");

		expect(result[0]?.evalResult).toBeNull();
		expect(result[1]?.evalResult).toBeNull();
		expect(result[1]?.behavior).toBe("Always include tests");
	});

	it("surfaces a 409 promote as a typed PromoteConflictError carrying { status, reason }", async () => {
		// The route base's interceptor wraps a 409 into an ApiError; the pinned P4 conflict body { status, reason }
		// rides apiProblemDetails (cast through unknown for the boundary test — the raw body is an opaque object).
		const conflictBody = { status: "EvalRegressed", reason: "Candidate regressed golden case g-1." } as unknown;
		axiosInstanceMock.post.mockRejectedValue(new ApiError(409, conflictBody as never));

		await expect(promoteSuggested("ag/1", "s/1")).rejects.toBeInstanceOf(PromoteConflictError);
		await expect(promoteSuggested("ag/1", "s/1")).rejects.toMatchObject({
			status: "EvalRegressed",
			message: "Candidate regressed golden case g-1.",
		});
	});

	it("parses the REAL host CapReached 409 body (PascalCase) through the boundary into a typed PromoteConflictError", async () => {
		// Regression guard for the host/FE casing seam: the host emits the cap conflict as PascalCase `CapReached`
		// (matching every other promotion status + the Zod enum). Feed the EXACT host body shape and exercise the real
		// parse path (promoteSuggested → toPromoteError → parsePromoteConflictBody), NOT a direct PromoteConflictError
		// construction, so a regression to camelCase `capReached` (which fails safeParse → null → generic error) is caught.
		const capBody = {
			status: "CapReached",
			reason: "Archive an enabled action before promoting (cap reached).",
		} as unknown;
		axiosInstanceMock.post.mockRejectedValue(new ApiError(409, capBody as never));

		await expect(promoteSuggested("ag/1", "s/1")).rejects.toBeInstanceOf(PromoteConflictError);
		await expect(promoteSuggested("ag/1", "s/1")).rejects.toMatchObject({
			status: "CapReached",
			message: "Archive an enabled action before promoting (cap reached).",
		});
	});

	it("passes a non-409 promote error through unchanged (not a PromoteConflictError)", async () => {
		const original = new ApiError(404, { type: "about:blank", title: "Not Found", status: 404, detail: "gone" });
		axiosInstanceMock.post.mockRejectedValue(original);

		await expect(promoteSuggested("ag/1", "s/1")).rejects.toBe(original);
	});
});
