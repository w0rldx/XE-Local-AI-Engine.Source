// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { toolCatalogQueryKeys } from "@/features/tools/queries/ToolCatalogQueryKeys";

// Mock the generated hey-api TanStack mutation factories. Each returns an object carrying a `mutationFn` the hook
// spreads (after withResponseValidation) into useMutation; the hooks layer their own onSuccess invalidation on top.
// The factory mocks let a test assert the variable shape the hook forwarded to the wire.
const { mutationFns } = vi.hoisted(() => ({
	mutationFns: {
		createMcpServer: vi.fn(),
		updateMcpServer: vi.fn(),
		deleteMcpServer: vi.fn(),
		setMcpServerEnabled: vi.fn(),
	},
}));

// Builds the single-element generated query key shape `listMcpServersQueryKey()` returns. Centralizes the `_id`
// discriminator literal (which trips biome's naming-convention rule) in one suppressed spot.
function fakeListKey(): unknown {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: "listMcpServers" }];
}

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	createMcpServerMutation: () => ({ mutationFn: mutationFns.createMcpServer }),
	updateMcpServerMutation: () => ({ mutationFn: mutationFns.updateMcpServer }),
	deleteMcpServerMutation: () => ({ mutationFn: mutationFns.deleteMcpServer }),
	setMcpServerEnabledMutation: () => ({ mutationFn: mutationFns.setMcpServerEnabled }),
	listMcpServersQueryKey: () => fakeListKey(),
	// Read-side factories are imported by the module under test but unused in these mutation tests.
	listMcpServersOptions: vi.fn(() => ({ queryKey: fakeListKey(), queryFn: vi.fn() })),
	getMcpServerToolsOptions: vi.fn(() => ({
		// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
		queryKey: [{ _id: "getMcpServerTools" }],
		queryFn: vi.fn(),
	})),
}));

import {
	useCreateMcpServer,
	useDeleteMcpServer,
	useSetMcpServerEnabled,
	useUpdateMcpServer,
} from "@/features/mcp/queries/useMcpServers";

const sampleBody = {
	name: "Server",
	description: null,
	transportKind: "Stdio" as const,
	command: "/usr/bin/srv",
	arguments: [],
	workingDirectory: null,
	env: {},
	url: null,
};

// The list invalidation key the hooks build via the generated `listMcpServersQueryKey()` (the `_id` partial object).
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

describe("useMcpServers mutations", () => {
	beforeEach(() => {
		mutationFns.createMcpServer.mockResolvedValue({ id: "mcp-1" });
		mutationFns.updateMcpServer.mockResolvedValue({ id: "mcp-1" });
		mutationFns.deleteMcpServer.mockResolvedValue(undefined);
		mutationFns.setMcpServerEnabled.mockResolvedValue({ id: "mcp-1", enabled: true });
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("create forwards the body and invalidates ONLY the servers list, never the tool catalog (server persists disabled)", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useCreateMcpServer(), { wrapper: Wrapper });

		result.current.mutate({ body: sampleBody });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		// TanStack v5 passes (variables, context) — assert the first arg.
		expect(mutationFns.createMcpServer.mock.calls[0]?.[0]).toEqual({ body: sampleBody });
		expect(invalidatedKeys).toContainEqual(listKey);
		expect(invalidatedKeys).not.toContainEqual(toolCatalogQueryKeys.all());
	});

	it("update forwards path + body and invalidates both the servers list and the tool catalog", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useUpdateMcpServer(), { wrapper: Wrapper });

		result.current.mutate({ path: { mcpServerId: "mcp-1" }, body: sampleBody });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.updateMcpServer.mock.calls[0]?.[0]).toEqual({
			path: { mcpServerId: "mcp-1" },
			body: sampleBody,
		});
		expect(invalidatedKeys).toContainEqual(listKey);
		expect(invalidatedKeys).toContainEqual(toolCatalogQueryKeys.all());
	});

	it("delete forwards the path and invalidates both the servers list and the tool catalog", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useDeleteMcpServer(), { wrapper: Wrapper });

		result.current.mutate({ path: { mcpServerId: "mcp-1" } });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.deleteMcpServer.mock.calls[0]?.[0]).toEqual({ path: { mcpServerId: "mcp-1" } });
		expect(invalidatedKeys).toContainEqual(listKey);
		expect(invalidatedKeys).toContainEqual(toolCatalogQueryKeys.all());
	});

	it("enable toggle forwards path + body and invalidates both the servers list and the tool catalog", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useSetMcpServerEnabled(), { wrapper: Wrapper });

		result.current.mutate({ id: "mcp-1", enabled: true });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.setMcpServerEnabled.mock.calls[0]?.[0]).toEqual({
			path: { mcpServerId: "mcp-1" },
			body: { enabled: true },
		});
		expect(invalidatedKeys).toContainEqual(listKey);
		expect(invalidatedKeys).toContainEqual(toolCatalogQueryKeys.all());
	});

	it("disable toggle forwards enabled=false through the same PATCH", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useSetMcpServerEnabled(), { wrapper: Wrapper });

		result.current.mutate({ id: "mcp-1", enabled: false });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.setMcpServerEnabled.mock.calls[0]?.[0]).toEqual({
			path: { mcpServerId: "mcp-1" },
			body: { enabled: false },
		});
	});
});
