// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";

// Mock the generated TanStack options/mutation factories so the hooks run against owned queryFn/mutationFn (no
// network). The hooks still compose the real withResponseValidation bridge + the real toPlaybookAction mapper +
// the real toPromoteError 409 translation, so this exercises the full read/mutation paths end-to-end.
const { listOptionsMock, mutationFns } = vi.hoisted(() => ({
	listOptionsMock: vi.fn(),
	mutationFns: {
		create: vi.fn(),
		update: vi.fn(),
		updateSuggested: vi.fn(),
		delete: vi.fn(),
		analyze: vi.fn(),
		promote: vi.fn(),
		reject: vi.fn(),
		runEval: vi.fn(),
	},
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	listAgentPlaybookActionsOptions: listOptionsMock,
	createPlaybookActionMutation: vi.fn(() => ({ mutationFn: mutationFns.create })),
	updatePlaybookActionMutation: vi.fn(() => ({ mutationFn: mutationFns.update })),
	updateSuggestedPlaybookActionMutation: vi.fn(() => ({ mutationFn: mutationFns.updateSuggested })),
	deletePlaybookActionMutation: vi.fn(() => ({ mutationFn: mutationFns.delete })),
	analyzePlaybookMutation: vi.fn(() => ({ mutationFn: mutationFns.analyze })),
	promoteSuggestedPlaybookActionMutation: vi.fn(() => ({ mutationFn: mutationFns.promote })),
	rejectSuggestedPlaybookActionMutation: vi.fn(() => ({ mutationFn: mutationFns.reject })),
	runPlaybookActionEvalMutation: vi.fn(() => ({ mutationFn: mutationFns.runEval })),
}));

import { PromoteConflictError } from "@/features/agents/models/PlaybookActionMappers";
import {
	playbookInvalidationKey,
	playbookQueryIds,
	useAnalyzePlaybook,
	useCreatePlaybookAction,
	useDeletePlaybookAction,
	usePlaybookActions,
	usePromoteSuggestedAction,
	useRejectSuggestedAction,
	useRunEval,
	useUpdatePlaybookAction,
	useUpdateSuggestedAction,
} from "@/features/agents/queries/usePlaybookActions";

// The generated query key the mutations invalidate (partial `_id` match), built via the production helper.
const ACTIONS_KEY = playbookInvalidationKey(playbookQueryIds.listActions);

// A generated-shaped playbook-action response (every field optional on the wire).
const generatedAction = {
	id: "action-1",
	agentDefinitionId: "agent-1",
	state: "Enabled",
	source: "Manual",
	triggerCondition: null,
	behavior: "Always cite your sources",
	scope: null,
	priority: 0,
	version: 1,
	createdAtUtc: 1000,
	updatedAtUtc: 2000,
	sourceFeedbackIds: null,
	confidence: null,
	evalResult: null,
};

// Captures the queryKey of every invalidateQueries call so a test can assert which caches a mutation touched.
const invalidatedKeys: unknown[] = [];

function makeWrapper() {
	invalidatedKeys.length = 0;
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	vi.spyOn(queryClient, "invalidateQueries").mockImplementation((filters) => {
		invalidatedKeys.push((filters as { queryKey?: unknown } | undefined)?.queryKey);
		return Promise.resolve();
	});
	function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	}
	return { Wrapper };
}

describe("usePlaybookActions read hook", () => {
	beforeEach(() => {
		listOptionsMock.mockImplementation(() => ({
			// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
			queryKey: [{ _id: playbookQueryIds.listActions }],
			queryFn: async () => ({ items: [generatedAction] }),
		}));
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("passes the agent id as a path param and maps the generated list into domain actions", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => usePlaybookActions("agent-1"), { wrapper: Wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(listOptionsMock).toHaveBeenCalledWith({ path: { agentDefinitionId: "agent-1" } });
		expect(result.current.data).toEqual([
			{
				id: "action-1",
				agentDefinitionId: "agent-1",
				state: "Enabled",
				source: "Manual",
				triggerCondition: null,
				behavior: "Always cite your sources",
				scope: null,
				priority: 0,
				version: 1,
				createdAtUtc: 1000,
				updatedAtUtc: 2000,
				sourceFeedbackIds: null,
				confidence: null,
				evalResult: null,
			},
		]);
	});

	it("is disabled (does not fetch) when no agent is selected", () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => usePlaybookActions(null), { wrapper: Wrapper });

		expect(result.current.fetchStatus).toBe("idle");
		expect(result.current.isPending).toBe(true);
	});
});

describe("usePlaybookActions mutations", () => {
	beforeEach(() => {
		mutationFns.create.mockResolvedValue(generatedAction);
		mutationFns.update.mockResolvedValue(generatedAction);
		mutationFns.updateSuggested.mockResolvedValue(generatedAction);
		mutationFns.delete.mockResolvedValue(undefined);
		mutationFns.analyze.mockResolvedValue({ items: [generatedAction] });
		mutationFns.promote.mockResolvedValue(generatedAction);
		mutationFns.reject.mockResolvedValue(generatedAction);
		mutationFns.runEval.mockResolvedValue(generatedAction);
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("create dispatches the agent path + body envelope and invalidates the action list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useCreatePlaybookAction("agent-1"), { wrapper: Wrapper });

		const request = { state: "Enabled", triggerCondition: null, behavior: "Be concise", scope: null, priority: 0 };
		result.current.mutate(request);

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		// TanStack v5 calls mutationFn(variables, context) — assert the variables via the first call arg only.
		expect(mutationFns.create.mock.calls[0]?.[0]).toEqual({ path: { agentDefinitionId: "agent-1" }, body: request });
		expect(invalidatedKeys).toContainEqual(ACTIONS_KEY);
	});

	it("update dispatches the agent + action path + body and invalidates the action list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useUpdatePlaybookAction("agent-1"), { wrapper: Wrapper });

		const request = { state: "Disabled", triggerCondition: null, behavior: "Be concise", scope: null, priority: 1 };
		result.current.mutate({ actionId: "action-1", request });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.update.mock.calls[0]?.[0]).toEqual({
			path: { agentDefinitionId: "agent-1", actionId: "action-1" },
			body: request,
		});
		expect(invalidatedKeys).toContainEqual(ACTIONS_KEY);
	});

	it("updateSuggested dispatches the dedicated /suggested envelope (state-less body) and invalidates", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useUpdateSuggestedAction("agent-1"), { wrapper: Wrapper });

		const request = { behavior: "Summarize first", triggerCondition: null, scope: null, priority: 0 };
		result.current.mutate({ actionId: "suggested-1", request });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		const envelope = mutationFns.updateSuggested.mock.calls[0]?.[0] as { body: Record<string, unknown> };
		expect(envelope).toEqual({ path: { agentDefinitionId: "agent-1", actionId: "suggested-1" }, body: request });
		// The body carries no `state` field (the action stays Suggested).
		expect(envelope.body).not.toHaveProperty("state");
		expect(invalidatedKeys).toContainEqual(ACTIONS_KEY);
	});

	it("delete dispatches the agent + action path and invalidates the action list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useDeletePlaybookAction("agent-1"), { wrapper: Wrapper });

		result.current.mutate("action-1");

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.delete.mock.calls[0]?.[0]).toEqual({
			path: { agentDefinitionId: "agent-1", actionId: "action-1" },
		});
		expect(invalidatedKeys).toContainEqual(ACTIONS_KEY);
	});

	it("analyze dispatches the agent path, maps the items envelope to domain actions, and invalidates", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useAnalyzePlaybook("agent-1"), { wrapper: Wrapper });

		result.current.mutate();

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.analyze.mock.calls[0]?.[0]).toEqual({ path: { agentDefinitionId: "agent-1" } });
		// The analyze result is the proposed Suggested actions (mapped), so the panel can react to an empty result.
		expect(result.current.data).toHaveLength(1);
		expect(result.current.data?.[0]?.id).toBe("action-1");
		expect(invalidatedKeys).toContainEqual(ACTIONS_KEY);
	});

	it("runEval dispatches the agent + action path and invalidates the action list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useRunEval("agent-1"), { wrapper: Wrapper });

		result.current.mutate("suggested-1");

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.runEval.mock.calls[0]?.[0]).toEqual({
			path: { agentDefinitionId: "agent-1", actionId: "suggested-1" },
		});
		expect(invalidatedKeys).toContainEqual(ACTIONS_KEY);
	});

	it("reject dispatches the agent + action path and invalidates the action list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useRejectSuggestedAction("agent-1"), { wrapper: Wrapper });

		result.current.mutate("suggested-1");

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.reject.mock.calls[0]?.[0]).toEqual({
			path: { agentDefinitionId: "agent-1", actionId: "suggested-1" },
		});
		expect(invalidatedKeys).toContainEqual(ACTIONS_KEY);
	});

	it("promote dispatches the agent + action path and invalidates on success", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => usePromoteSuggestedAction("agent-1"), { wrapper: Wrapper });

		result.current.mutate("suggested-1");

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.promote.mock.calls[0]?.[0]).toEqual({
			path: { agentDefinitionId: "agent-1", actionId: "suggested-1" },
		});
		expect(invalidatedKeys).toContainEqual(ACTIONS_KEY);
	});

	// Eval-gate 409: the promote mutation uses onSettled (not onSuccess), so the list still refreshes when the
	// promote rejects. It also translates the 409 ApiError into a typed PromoteConflictError so the panel can show
	// the precise reason (needs eval / regressed / stale / cap reached).
	it("promote translates a 409 into a PromoteConflictError AND still invalidates (onSettled) on rejection", async () => {
		const conflictBody = { status: "EvalRegressed", reason: "Candidate regressed golden case g-1." } as unknown;
		mutationFns.promote.mockRejectedValue(new ApiError(409, conflictBody as never));

		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => usePromoteSuggestedAction("agent-1"), { wrapper: Wrapper });

		result.current.mutate("suggested-1");

		await waitFor(() => expect(result.current.isError).toBe(true));

		expect(result.current.error).toBeInstanceOf(PromoteConflictError);
		expect((result.current.error as PromoteConflictError).status).toBe("EvalRegressed");
		// onSettled fires the invalidation even though the mutation rejected with a 409.
		expect(invalidatedKeys).toContainEqual(ACTIONS_KEY);
	});

	it("promote passes a non-409 error through unchanged (not a PromoteConflictError)", async () => {
		const original = new ApiError(404, { type: "about:blank", title: "Not Found", status: 404, detail: "gone" });
		mutationFns.promote.mockRejectedValue(original);

		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => usePromoteSuggestedAction("agent-1"), { wrapper: Wrapper });

		result.current.mutate("suggested-1");

		await waitFor(() => expect(result.current.isError).toBe(true));

		expect(result.current.error).toBe(original);
	});
});
