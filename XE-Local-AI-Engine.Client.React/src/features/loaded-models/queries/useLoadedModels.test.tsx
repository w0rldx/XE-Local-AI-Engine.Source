// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

// The hooks call the generated SDK fns directly through callWithResponseValidation. Mock the generated module so the
// test owns the fns and can assert the request shape + the mapped result without hitting the network.
const { sdkMock } = vi.hoisted(() => ({
	sdkMock: {
		getRunningLocalModels: vi.fn(),
	},
}));

vi.mock("@/core/api/generated", () => sdkMock);

// useRunningModels (the llama.cpp runtime the page lists) wraps the generated TanStack `*Options()` instead of
// calling the SDK fn directly, so its generated module is mocked separately with a test-owned options object.
const { runningModelsGenMock } = vi.hoisted(() => ({
	runningModelsGenMock: {
		listRunningModelsOptions: vi.fn(),
		ejectRunningModelMutation: vi.fn(),
	},
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => runningModelsGenMock);

import { resolveLoadedModelsPollIntervalMs, useLoadedModels } from "@/features/loaded-models/queries/useLoadedModels";
import { runningModelsPollIntervalMs, useRunningModels } from "@/features/loaded-models/queries/useRunningModels";

function makeClient() {
	return new QueryClient({
		defaultOptions: { queries: { retry: false } },
	});
}

function makeWrapper(queryClient: QueryClient) {
	return function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	};
}

const availableSnapshot = {
	isAvailable: true,
	error: null,
	items: [
		{ modelName: "llama3.1:8b", sizeBytes: 8_589_934_592, sizeVramBytes: 4_294_967_296, expiresAtUtc: 1_700_000_000_000 },
		{ modelName: "qwen2.5:3b", sizeBytes: 3_221_225_472, sizeVramBytes: null, expiresAtUtc: null },
	],
};

describe("useLoadedModels", () => {
	afterEach(() => {
		vi.clearAllMocks();
	});

	it("fetches the running models and maps the snapshot to the domain view-model", async () => {
		sdkMock.getRunningLocalModels.mockResolvedValue({ data: availableSnapshot });
		const queryClient = makeClient();

		const { result } = renderHook(() => useLoadedModels(), { wrapper: makeWrapper(queryClient) });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(sdkMock.getRunningLocalModels).toHaveBeenCalledWith(expect.objectContaining({ throwOnError: true }));
		expect(result.current.data?.isAvailable).toBe(true);
		expect(result.current.data?.models.map((model) => model.modelName)).toEqual(["llama3.1:8b", "qwen2.5:3b"]);
	});

	it("resolves the unavailable snapshot (200 + isAvailable:false) without erroring", async () => {
		sdkMock.getRunningLocalModels.mockResolvedValue({
			data: { isAvailable: false, error: "Provider unreachable", items: [] },
		});
		const queryClient = makeClient();

		const { result } = renderHook(() => useLoadedModels(), { wrapper: makeWrapper(queryClient) });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(result.current.data?.isAvailable).toBe(false);
		expect(result.current.data?.models).toEqual([]);
	});
});

describe("resolveLoadedModelsPollIntervalMs back-off", () => {
	const fast = resolveLoadedModelsPollIntervalMs({ isAvailable: true, ollamaConfigured: true, models: [] });

	it("polls at the fast cadence while the provider is available", () => {
		expect(fast).toBe(4000);
	});

	it("polls at the fast cadence before the first response (no snapshot yet)", () => {
		expect(resolveLoadedModelsPollIntervalMs(undefined)).toBe(fast);
	});

	it("backs off to a slower cadence once a configured provider reports unreachable", () => {
		// A configured-but-down Ollama must not be polled every 4s: the interval grows so the connection-refused loop is
		// throttled while still recovering automatically if it later comes up.
		const slow = resolveLoadedModelsPollIntervalMs({
			isAvailable: false,
			ollamaConfigured: true,
			models: [],
		});
		expect(slow).toBe(30_000);
	});

	it("STOPS polling entirely once the node reports Ollama is not configured", () => {
		// A switched-off Ollama runtime will never answer, so the recurring poll is disabled outright rather than backing
		// off forever against an endpoint that is deliberately absent.
		const stopped = resolveLoadedModelsPollIntervalMs({ isAvailable: false, ollamaConfigured: false, models: [] });
		expect(stopped).toBe(false);
	});
});

describe("useRunningModels (llama.cpp) polling", () => {
	afterEach(() => {
		vi.useRealTimers();
		vi.clearAllMocks();
	});

	it("re-fetches on its own poll interval, since loads/evictions happen without any client mutation to invalidate on", async () => {
		vi.useFakeTimers({ shouldAdvanceTime: true });
		const queryFn = vi.fn().mockResolvedValue({ items: [] });
		runningModelsGenMock.listRunningModelsOptions.mockReturnValue({
			// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
			queryKey: [{ _id: "listRunningModels" }],
			queryFn,
		});
		const queryClient = makeClient();

		const { result } = renderHook(() => useRunningModels(), { wrapper: makeWrapper(queryClient) });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(queryFn).toHaveBeenCalledTimes(1);

		// One full poll interval later the list re-fetches on its own — models appear as chat sends warm them and
		// disappear on idle-TTL eviction, so without this the page only ever refreshed on manual reload.
		await vi.advanceTimersByTimeAsync(runningModelsPollIntervalMs + 100);
		await waitFor(() => expect(queryFn.mock.calls.length).toBeGreaterThanOrEqual(2));
	});

	it("pins the cadence to 4s", () => {
		expect(runningModelsPollIntervalMs).toBe(4000);
	});
});
