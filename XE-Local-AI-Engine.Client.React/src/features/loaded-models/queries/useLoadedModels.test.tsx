// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The hooks call the generated SDK fns directly through callWithResponseValidation. Mock the generated module so the
// test owns the fns and can assert the request shape + the mapped result without hitting the network.
const { sdkMock } = vi.hoisted(() => ({
	sdkMock: {
		getRunningLocalModels: vi.fn(),
		unloadLocalModel: vi.fn(),
	},
}));

vi.mock("@/core/api/generated", () => sdkMock);

// useRunningModels (the llama.cpp twin of the Ollama hooks above) wraps the generated TanStack `*Options()` instead of
// calling the SDK fn directly, so its generated module is mocked separately with a test-owned options object.
const { runningModelsGenMock } = vi.hoisted(() => ({
	runningModelsGenMock: {
		listRunningModelsOptions: vi.fn(),
		ejectRunningModelMutation: vi.fn(),
	},
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => runningModelsGenMock);

import type { LoadedModelsSnapshot } from "@/features/loaded-models/models/LoadedModelsModels";
import {
	loadedModelsQueryKey,
	resolveLoadedModelsPollIntervalMs,
	useEjectModel,
	useLoadedModels,
} from "@/features/loaded-models/queries/useLoadedModels";
import { runningModelsPollIntervalMs, useRunningModels } from "@/features/loaded-models/queries/useRunningModels";

function makeClient() {
	return new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
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
		expect(result.current.data?.models).toHaveLength(2);
		expect(result.current.data?.models[1]?.sizeVramBytes).toBeNull();
	});

	it("resolves the unavailable snapshot (200 + isAvailable:false) without erroring", async () => {
		sdkMock.getRunningLocalModels.mockResolvedValue({
			data: { isAvailable: false, error: "Provider unreachable", items: [] },
		});
		const queryClient = makeClient();

		const { result } = renderHook(() => useLoadedModels(), { wrapper: makeWrapper(queryClient) });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(result.current.data?.isAvailable).toBe(false);
		expect(result.current.data?.error).toBe("Provider unreachable");
		expect(result.current.data?.models).toEqual([]);
	});
});

describe("resolveLoadedModelsPollIntervalMs back-off", () => {
	const fast = resolveLoadedModelsPollIntervalMs({ isAvailable: true, ollamaConfigured: true, error: null, models: [] });

	it("polls at the fast cadence while the provider is available", () => {
		expect(fast).toBe(4000);
	});

	it("polls at the fast cadence before the first response (no snapshot yet)", () => {
		expect(resolveLoadedModelsPollIntervalMs(undefined)).toBe(fast);
	});

	it("backs off to a slower cadence once a configured provider reports unreachable", () => {
		// A configured-but-down Ollama must not be polled every 4s: the interval grows so the connection-refused loop is
		// throttled while still recovering automatically if it later comes up.
		const slow = resolveLoadedModelsPollIntervalMs({ isAvailable: false, ollamaConfigured: true, error: "Provider unreachable", models: [] });
		expect(slow).toBe(30_000);
	});

	it("STOPS polling entirely once the node reports Ollama is not configured", () => {
		// A switched-off Ollama runtime will never answer, so the recurring poll is disabled outright rather than backing
		// off forever against an endpoint that is deliberately absent.
		const stopped = resolveLoadedModelsPollIntervalMs({ isAvailable: false, ollamaConfigured: false, error: null, models: [] });
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

	it("pins the cadence to the same 4s the adjacent Ollama loaded-models query polls at", () => {
		expect(runningModelsPollIntervalMs).toBe(4000);
	});
});

describe("useEjectModel", () => {
	beforeEach(() => {
		sdkMock.unloadLocalModel.mockResolvedValue({ data: { modelName: "llama3.1:8b", unloaded: true } });
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("dispatches the model name to the generated unload path and resolves the mapped result", async () => {
		const queryClient = makeClient();

		const { result } = renderHook(() => useEjectModel(), { wrapper: makeWrapper(queryClient) });

		result.current.mutate("llama3.1:8b");

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(sdkMock.unloadLocalModel).toHaveBeenCalledWith(
			expect.objectContaining({ path: { modelName: "llama3.1:8b" }, throwOnError: true }),
		);
		// The unload endpoint is route-only and the generated requestValidator types its body as `z.never().optional()`,
		// so ANY body (even `{}`) fails zod parsing before the request is built and the eject never reaches the wire.
		// Assert no body is passed — the shipped defect this replaced was exactly that, and it is invisible to the
		// resolution assertions above.
		expect(sdkMock.unloadLocalModel.mock.calls[0]?.[0]).not.toHaveProperty("body");
		expect(result.current.data).toEqual({ modelName: "llama3.1:8b", unloaded: true });
	});

	it("optimistically removes the ejected row from the cached snapshot before the request resolves", async () => {
		const queryClient = makeClient();
		const seeded: LoadedModelsSnapshot = {
			isAvailable: true,
			ollamaConfigured: true,
			error: null,
			models: [
				{ modelName: "llama3.1:8b", sizeBytes: 1, sizeVramBytes: null, expiresAtUtc: null },
				{ modelName: "qwen2.5:3b", sizeBytes: 2, sizeVramBytes: null, expiresAtUtc: null },
			],
		};
		queryClient.setQueryData(loadedModelsQueryKey, seeded);
		// Hold the request open so the assertion observes the optimistic state, not the post-settle invalidation.
		let resolveUnload: (value: { data: { modelName: string; unloaded: boolean } }) => void = () => undefined;
		sdkMock.unloadLocalModel.mockReturnValue(
			new Promise((resolve) => {
				resolveUnload = resolve;
			}),
		);

		const { result } = renderHook(() => useEjectModel(), { wrapper: makeWrapper(queryClient) });

		result.current.mutate("llama3.1:8b");

		await waitFor(() => {
			const optimistic = queryClient.getQueryData<LoadedModelsSnapshot>(loadedModelsQueryKey);
			expect(optimistic?.models.map((model) => model.modelName)).toEqual(["qwen2.5:3b"]);
		});

		resolveUnload({ data: { modelName: "llama3.1:8b", unloaded: true } });
		await waitFor(() => expect(result.current.isSuccess).toBe(true));
	});

	it("restores the previous snapshot when the eject fails", async () => {
		const queryClient = makeClient();
		const seeded: LoadedModelsSnapshot = {
			isAvailable: true,
			ollamaConfigured: true,
			error: null,
			models: [{ modelName: "llama3.1:8b", sizeBytes: 1, sizeVramBytes: null, expiresAtUtc: null }],
		};
		queryClient.setQueryData(loadedModelsQueryKey, seeded);
		sdkMock.unloadLocalModel.mockRejectedValue(new Error("Request failed with status code 400"));

		const { result } = renderHook(() => useEjectModel(), { wrapper: makeWrapper(queryClient) });

		result.current.mutate("llama3.1:8b");

		await waitFor(() => expect(result.current.isError).toBe(true));

		const restored = queryClient.getQueryData<LoadedModelsSnapshot>(loadedModelsQueryKey);
		expect(restored?.models.map((model) => model.modelName)).toEqual(["llama3.1:8b"]);
	});
});
