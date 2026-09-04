// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Mock generated query/mutation factories to isolate the hook while retaining validation and mapping.
const { listMock, toolCapableMock, createMutationFn, updateMutationFn, deleteMutationFn } = vi.hoisted(() => ({
	listMock: vi.fn(),
	toolCapableMock: vi.fn(),
	createMutationFn: vi.fn(),
	updateMutationFn: vi.fn(),
	deleteMutationFn: vi.fn(),
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	listAgentDefinitionsOptions: listMock,
	getToolCapableModelsOptions: toolCapableMock,
	createAgentDefinitionMutation: vi.fn(() => ({ mutationFn: createMutationFn })),
	updateAgentDefinitionMutation: vi.fn(() => ({ mutationFn: updateMutationFn })),
	deleteAgentDefinitionMutation: vi.fn(() => ({ mutationFn: deleteMutationFn })),
}));

import {
	agentDefinitionsInvalidationKey,
	agentDefinitionsQueryIds,
	useAgentDefinitions,
	useCreateAgentDefinition,
	useDeleteAgentDefinition,
	useToolCapableModels,
	useUpdateAgentDefinition,
} from "@/features/agents/queries/useAgentDefinitions";

// The generated query key the mutations invalidate (partial `_id` match), built via the production helper.
const LIST_KEY = agentDefinitionsInvalidationKey(agentDefinitionsQueryIds.list);

// A generated-shaped agent-definition response (every field optional on the wire).
const generatedDefinition = {
	id: "agent-1",
	name: "Research assistant",
	description: "Helps with research",
	instructions: "You are a research assistant.",
	modelProfile: "qwen3:8b",
	reasoningEffort: "medium",
	kind: "Single",
	allowedToolNames: ["GetCurrentTime"],
	toolApprovals: { GetCurrentTime: true },
	orchestrationTopologyJson: null,
	playbookEnabled: false,
	defaultTemporaryChat: false,
	version: 1,
	createdAtUtc: 1000,
	updatedAtUtc: 2000,
};

// The stricter domain shape the mappers produce from generatedDefinition.
const domainDefinition = {
	id: "agent-1",
	name: "Research assistant",
	description: "Helps with research",
	instructions: "You are a research assistant.",
	modelProfile: "qwen3:8b",
	reasoningEffort: "medium",
	kind: "Single",
	allowedToolNames: ["GetCurrentTime"],
	toolApprovals: { GetCurrentTime: true },
	allowedSkillIds: [],
	orchestrationTopologyJson: null,
	playbookEnabled: false,
	defaultTemporaryChat: false,
	// The wire fixture omits memoryExtractionEnabled; the mapper degrades an absent value to true (backend default).
	memoryExtractionEnabled: true,
	// The wire fixture omits disableBaseScaffold and disableToolRelevanceFilter; the mapper degrades an absent value to
	// false for both (the backend default).
	disableBaseScaffold: false,
	disableToolRelevanceFilter: false,
	version: 1,
	createdAtUtc: 1000,
	updatedAtUtc: 2000,
};

// A save-request body as toSaveAgentDefinitionRequest would build it (already-mapped domain → wire). The hooks keep
// the page-built request opaque, so the tests dispatch this shape directly.
const saveRequest = {
	name: "Research assistant",
	description: "Helps with research",
	instructions: "You are a research assistant.",
	modelProfile: "qwen3:8b",
	reasoningEffort: "medium",
	kind: "Single" as const,
	allowedToolNames: ["GetCurrentTime"],
	toolApprovals: { GetCurrentTime: true },
	orchestrationTopologyJson: null,
	playbookEnabled: false,
	defaultTemporaryChat: false,
	memoryExtractionEnabled: true,
};

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

describe("agent definition reads", () => {
	beforeEach(() => {
		listMock.mockImplementation(() => ({
			// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
			queryKey: [{ _id: agentDefinitionsQueryIds.list }],
			queryFn: async () => ({ items: [generatedDefinition] }),
		}));
		toolCapableMock.mockImplementation(() => ({
			// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
			queryKey: [{ _id: "getToolCapableModels" }],
			queryFn: async () => ({ models: ["qwen3:8b", "llama3.1:8b"] }),
		}));
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("maps the generated list response into domain definitions", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useAgentDefinitions(), { wrapper: Wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(listMock).toHaveBeenCalledWith();
		expect(result.current.data).toEqual([domainDefinition]);
	});

	it("maps the tool-capable-models response onto a bare string[]", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useToolCapableModels(), { wrapper: Wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(result.current.data).toEqual(["qwen3:8b", "llama3.1:8b"]);
	});
});

describe("agent definition mutations", () => {
	beforeEach(() => {
		createMutationFn.mockResolvedValue(generatedDefinition);
		updateMutationFn.mockResolvedValue(generatedDefinition);
		deleteMutationFn.mockResolvedValue(undefined);
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("create dispatches the save request into the generated { body } envelope and invalidates the list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useCreateAgentDefinition(), { wrapper: Wrapper });

		result.current.mutate(saveRequest);

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		// TanStack v5 calls mutationFn(variables, context) — assert variables via the first call arg only.
		expect(createMutationFn.mock.calls[0]?.[0]).toEqual({ body: saveRequest });
		expect(result.current.data).toEqual(domainDefinition);
		expect(invalidatedKeys).toContainEqual(LIST_KEY);
	});

	it("update dispatches the { path, body } envelope and invalidates the list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useUpdateAgentDefinition(), { wrapper: Wrapper });

		result.current.mutate({ id: "agent-1", request: saveRequest });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(updateMutationFn.mock.calls[0]?.[0]).toEqual({
			path: { agentDefinitionId: "agent-1" },
			body: saveRequest,
		});
		expect(result.current.data).toEqual(domainDefinition);
		expect(invalidatedKeys).toContainEqual(LIST_KEY);
	});

	it("delete dispatches the agent id into the generated { path } envelope and invalidates the list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useDeleteAgentDefinition(), { wrapper: Wrapper });

		result.current.mutate("agent-7");

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(deleteMutationFn.mock.calls[0]?.[0]).toEqual({ path: { agentDefinitionId: "agent-7" } });
		expect(invalidatedKeys).toContainEqual(LIST_KEY);
	});

	it("surfaces a mutation error and does not invalidate", async () => {
		createMutationFn.mockRejectedValue(new Error("Request failed with status code 400"));
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useCreateAgentDefinition(), { wrapper: Wrapper });

		result.current.mutate(saveRequest);

		await waitFor(() => expect(result.current.isError).toBe(true));

		expect(invalidatedKeys).not.toContainEqual(LIST_KEY);
	});
});
