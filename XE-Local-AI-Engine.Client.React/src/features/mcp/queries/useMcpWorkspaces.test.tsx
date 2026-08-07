// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { mutationFns } = vi.hoisted(() => ({
	mutationFns: {
		create: vi.fn(),
		remove: vi.fn(),
	},
}));

function workspaceListKey(): unknown {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: "listWorkspaces" }];
}

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	listWorkspacesOptions: () => ({
		queryKey: workspaceListKey(),
		queryFn: async () => ({ items: [{ workspaceId: "ws_opaque", alias: "Repository", mode: "read-only" }] }),
	}),
	listWorkspacesQueryKey: () => workspaceListKey(),
	createWorkspaceMutation: () => ({ mutationFn: mutationFns.create }),
	deleteWorkspaceMutation: () => ({ mutationFn: mutationFns.remove }),
}));

import { useCreateMcpWorkspace, useDeleteMcpWorkspace, useMcpWorkspaces } from "@/features/mcp/queries/useMcpWorkspaces";

function makeWrapper() {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();
	function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	}
	return { Wrapper, invalidate };
}

describe("MCP workspace queries", () => {
	beforeEach(() => {
		mutationFns.create.mockResolvedValue({ workspaceId: "ws_opaque", alias: "Repository", mode: "read-only" });
		mutationFns.remove.mockResolvedValue(undefined);
	});

	afterEach(() => vi.clearAllMocks());

	it("maps the generated response to an opaque read-only workspace model", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useMcpWorkspaces(), { wrapper: Wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(result.current.data).toEqual([{ id: "ws_opaque", alias: "Repository", mode: "read-only" }]);
	});

	it("create forwards the exact alias and host-path body and invalidates the workspace list", async () => {
		const { Wrapper, invalidate } = makeWrapper();
		const { result } = renderHook(() => useCreateMcpWorkspace(), { wrapper: Wrapper });
		const variables = { body: { alias: "Repository", hostPath: "/trusted/repository" } };

		result.current.mutate(variables);

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(mutationFns.create.mock.calls[0]?.[0]).toEqual(variables);
		expect(invalidate).toHaveBeenCalledWith({ queryKey: workspaceListKey() });
	});

	it("delete forwards only the opaque workspace ID and invalidates the workspace list", async () => {
		const { Wrapper, invalidate } = makeWrapper();
		const { result } = renderHook(() => useDeleteMcpWorkspace(), { wrapper: Wrapper });
		const variables = { path: { workspaceId: "ws_opaque" } };

		result.current.mutate(variables);

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(mutationFns.remove.mock.calls[0]?.[0]).toEqual(variables);
		expect(invalidate).toHaveBeenCalledWith({ queryKey: workspaceListKey() });
	});
});
