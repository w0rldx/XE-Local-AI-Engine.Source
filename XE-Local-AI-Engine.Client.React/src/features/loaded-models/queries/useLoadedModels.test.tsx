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

import type { LoadedModelsSnapshot } from "@/features/loaded-models/models/LoadedModelsModels";
import { loadedModelsQueryKey, useEjectModel, useLoadedModels } from "@/features/loaded-models/queries/useLoadedModels";

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
		// The unload endpoint is a body-bound POST — FastEndpoints 415s a route-only POST with no body — so the
		// mutation MUST send an empty JSON object. Assert the body explicitly: a missing/omitted body is the runtime
		// 415 this guards against, and the SDK-level mock would not otherwise catch it.
		expect(sdkMock.unloadLocalModel.mock.calls[0]?.[0]?.body).toEqual({});
		expect(result.current.data).toEqual({ modelName: "llama3.1:8b", unloaded: true });
	});

	it("optimistically removes the ejected row from the cached snapshot before the request resolves", async () => {
		const queryClient = makeClient();
		const seeded: LoadedModelsSnapshot = {
			isAvailable: true,
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
