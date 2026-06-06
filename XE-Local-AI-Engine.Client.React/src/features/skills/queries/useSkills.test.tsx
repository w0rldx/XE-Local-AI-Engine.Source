// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Mock the generated hey-api TanStack factories. Each mutation factory returns an object carrying a `mutationFn` the
// hook spreads (after withResponseValidation) into useMutation; the hooks layer their own onSuccess invalidation on
// top. The read factories return a queryFn the list/get hooks spread. The factory mocks let a test assert the
// variable shape the hook forwarded to the wire and which caches it invalidated.
const { mutationFns, listFn, getFn } = vi.hoisted(() => ({
	mutationFns: {
		createSkill: vi.fn(),
		updateSkill: vi.fn(),
		deleteSkill: vi.fn(),
	},
	listFn: vi.fn(),
	getFn: vi.fn(),
}));

// Builds the single-element generated list query key shape `listSkillsQueryKey()` returns.
function fakeListKey(): unknown {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: "listSkills" }];
}

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	createSkillMutation: () => ({ mutationFn: mutationFns.createSkill }),
	updateSkillMutation: () => ({ mutationFn: mutationFns.updateSkill }),
	deleteSkillMutation: () => ({ mutationFn: mutationFns.deleteSkill }),
	listSkillsQueryKey: () => fakeListKey(),
	listSkillsOptions: () => ({ queryKey: fakeListKey(), queryFn: listFn }),
	getSkillOptions: (options: { path: { skillId: string } }) => ({
		// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
		queryKey: [{ _id: "getSkill", path: options.path }],
		queryFn: getFn,
	}),
}));

import { useCreateSkill, useDeleteSkill, useSkill, useSkills, useUpdateSkill } from "@/features/skills/queries/useSkills";

const listKey = fakeListKey();

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

const createBody = { name: "invoice-review", description: "How to review", body: "# Body" };
const updateBody = { name: "invoice-review", description: "How to review", body: "# Body", enabled: false };

describe("useSkills reads", () => {
	beforeEach(() => {
		// The mocked queryFn stands in for the generated `*Options().queryFn`, which already UNWRAPS the axios
		// envelope (`const { data } = await listSkills(...); return data`). So it resolves the bare wire payload —
		// the list/get hooks' `select` then maps it into the domain shape.
		listFn.mockResolvedValue({
			items: [{ id: "skill-1", name: "invoice-review", description: "d", enabled: true, version: 1 }],
		});
		getFn.mockResolvedValue({
			id: "skill-1",
			name: "invoice-review",
			description: "d",
			body: "# Body",
			enabled: true,
			version: 1,
		});
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("maps the list response into domain skill summaries (body omitted)", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useSkills(), { wrapper: Wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(result.current.data).toEqual([
			{
				id: "skill-1",
				name: "invoice-review",
				description: "d",
				enabled: true,
				version: 1,
				createdAtUtc: 0,
				updatedAtUtc: 0,
			},
		]);
	});

	it("does not fetch the single skill when no id is supplied (create path)", () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useSkill(null), { wrapper: Wrapper });

		expect(result.current.fetchStatus).toBe("idle");
		expect(getFn).not.toHaveBeenCalled();
	});

	it("fetches and maps the full single skill (body included) when an id is supplied", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useSkill("skill-1"), { wrapper: Wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(result.current.data).toEqual({
			id: "skill-1",
			name: "invoice-review",
			description: "d",
			body: "# Body",
			enabled: true,
			version: 1,
			createdAtUtc: 0,
			updatedAtUtc: 0,
		});
	});
});

describe("useSkills mutations", () => {
	beforeEach(() => {
		mutationFns.createSkill.mockResolvedValue({ id: "skill-1" });
		mutationFns.updateSkill.mockResolvedValue({ id: "skill-1" });
		mutationFns.deleteSkill.mockResolvedValue(undefined);
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("create forwards the body and invalidates the skills list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useCreateSkill(), { wrapper: Wrapper });

		result.current.mutate({ body: createBody });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.createSkill.mock.calls[0]?.[0]).toEqual({ body: createBody });
		expect(invalidatedKeys).toContainEqual(listKey);
	});

	it("update forwards path + body and invalidates both the list and the single-skill cache", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useUpdateSkill(), { wrapper: Wrapper });

		result.current.mutate({ path: { skillId: "skill-1" }, body: updateBody });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.updateSkill.mock.calls[0]?.[0]).toEqual({ path: { skillId: "skill-1" }, body: updateBody });
		expect(invalidatedKeys).toContainEqual(listKey);
		// The single-skill cache is invalidated by the `_id: "getSkill"` partial key so a re-open shows the fresh body.
		// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
		expect(invalidatedKeys).toContainEqual([{ _id: "getSkill" }]);
	});

	it("delete forwards the path and invalidates the skills list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useDeleteSkill(), { wrapper: Wrapper });

		result.current.mutate({ path: { skillId: "skill-1" } });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.deleteSkill.mock.calls[0]?.[0]).toEqual({ path: { skillId: "skill-1" } });
		expect(invalidatedKeys).toContainEqual(listKey);
	});
});
