// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Mock generated query/mutation factories to isolate the hook while retaining validation and mapping.
const { listMock, importMutationFn, templatesKeyMock } = vi.hoisted(() => ({
	listMock: vi.fn(),
	importMutationFn: vi.fn(),
	templatesKeyMock: vi.fn(),
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	listAgentTemplatesOptions: listMock,
	importAgentTemplatesMutation: vi.fn(() => ({ mutationFn: importMutationFn })),
	listAgentTemplatesQueryKey: templatesKeyMock,
}));

import { agentDefinitionsInvalidationKey, agentDefinitionsQueryIds } from "@/features/agents/queries/useAgentDefinitions";
import { useAgentTemplates, useImportAgentTemplates } from "@/features/agents/queries/useAgentTemplates";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

const DEFINITIONS_KEY = agentDefinitionsInvalidationKey(agentDefinitionsQueryIds.list);
// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
const TEMPLATES_KEY = [{ _id: "listAgentTemplates" }];

const generatedSummary = {
	slug: "engineering-backend-architect",
	name: "Backend architect",
	description: "Designs services",
	division: "engineering",
	estimatedPromptTokens: 1200,
	hasOriginalTools: false,
	alreadyImported: false,
};

const invalidatedKeys: unknown[] = [];

function makeWrapper() {
	invalidatedKeys.length = 0;
	const { wrapper, queryClient } = createProvidersWrapper();
	vi.spyOn(queryClient, "invalidateQueries").mockImplementation((filters) => {
		invalidatedKeys.push((filters as { queryKey?: unknown } | undefined)?.queryKey);
		return Promise.resolve();
	});
	return { wrapper };
}

describe("agent template reads", () => {
	beforeEach(() => {
		listMock.mockImplementation(() => ({
			// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
			queryKey: [{ _id: "listAgentTemplates" }],
			queryFn: async () => ({ items: [generatedSummary] }),
		}));
		templatesKeyMock.mockImplementation(() => TEMPLATES_KEY);
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("returns the template summaries from the generated list response", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useAgentTemplates(), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(result.current.data).toEqual([generatedSummary]);
	});

	it("returns an empty array when the response omits items", async () => {
		listMock.mockImplementation(() => ({
			// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
			queryKey: [{ _id: "listAgentTemplates" }],
			queryFn: async () => ({}),
		}));
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useAgentTemplates(), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(result.current.data).toEqual([]);
	});
});

describe("agent template import", () => {
	beforeEach(() => {
		templatesKeyMock.mockImplementation(() => TEMPLATES_KEY);
		importMutationFn.mockResolvedValue({ imported: ["engineering-backend-architect"], skippedExisting: [], unknown: [] });
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("dispatches the { body: { slugs } } envelope and invalidates both lists", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useImportAgentTemplates(), { wrapper });

		result.current.mutate({ body: { slugs: ["engineering-backend-architect"] } });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(importMutationFn.mock.calls[0]?.[0]).toEqual({ body: { slugs: ["engineering-backend-architect"] } });
		expect(invalidatedKeys).toContainEqual(DEFINITIONS_KEY);
		expect(invalidatedKeys).toContainEqual(TEMPLATES_KEY);
	});

	it("surfaces a mutation error and does not invalidate", async () => {
		importMutationFn.mockRejectedValue(new Error("Request failed with status code 400"));
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useImportAgentTemplates(), { wrapper });

		result.current.mutate({ body: { slugs: ["engineering-backend-architect"] } });

		await waitFor(() => expect(result.current.isError).toBe(true));

		expect(invalidatedKeys).not.toContainEqual(DEFINITIONS_KEY);
	});
});
