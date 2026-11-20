// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { hubMock } = vi.hoisted(() => ({
	hubMock: {
		path: undefined as string | undefined,
		handler: undefined as ((payload: unknown) => void) | undefined,
		reconnected: undefined as (() => void) | undefined,
		unregisterReconnected: vi.fn(),
		release: vi.fn(),
		on: vi.fn((_name: string, handler: (payload: unknown) => void) => {
			hubMock.handler = handler;
		}),
		off: vi.fn(),
		onReconnected: vi.fn((handler: () => void) => {
			hubMock.reconnected = handler;
			return hubMock.unregisterReconnected;
		}),
	},
}));

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: (path: string) => {
		hubMock.path = path;
		return {
			connection: { on: hubMock.on, off: hubMock.off },
			onReconnected: hubMock.onReconnected,
			release: hubMock.release,
		};
	},
}));

import {
	imageRuntimeSourceBuildStream,
	llamaCppSourceBuildStream,
	useSourceBuildHub,
} from "@/features/node-settings/hooks/useSourceBuildHub";

function descriptor(repository: string, buildId: string) {
	return {
		buildId,
		backend: "cpu",
		source: repository.includes("ggml-org") ? "official" : "custom",
		repository,
		revisionMode: repository.includes("ggml-org") ? "enginePinned" : "defaultBranch",
		requestedCommit: null,
		resolvedCommit: "a".repeat(40),
	};
}

describe("useSourceBuildHub", () => {
	beforeEach(() => {
		hubMock.path = undefined;
		hubMock.handler = undefined;
		hubMock.reconnected = undefined;
		vi.clearAllMocks();
	});

	it("resets live logs for a new build identity and invalidates status/runtime on terminal", async () => {
		const queryClient = new QueryClient();
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue(undefined);
		const wrapper = ({ children }: { children: ReactNode }) => (
			<QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
		);
		const { result } = renderHook(() => useSourceBuildHub(llamaCppSourceBuildStream, true), { wrapper });

		act(() =>
			hubMock.handler?.({
				phase: "Building",
				appendedLogStartSequence: 0,
				appendedLogLines: ["old"],
				terminal: false,
				sanitizedError: null,
				currentBuild: descriptor("https://github.com/ggml-org/llama.cpp", "11111111-1111-4111-8111-111111111111"),
			}),
		);
		expect(result.current.logEntries).toEqual([{ sequence: 0, message: "old" }]);

		act(() =>
			hubMock.handler?.({
				phase: "Completed",
				appendedLogStartSequence: 0,
				appendedLogLines: ["new"],
				terminal: true,
				sanitizedError: null,
				currentBuild: descriptor("https://github.com/ggml-org/llama.cpp", "22222222-2222-4222-8222-222222222222"),
			}),
		);

		expect(result.current.logEntries).toEqual([{ sequence: 0, message: "new" }]);
		await waitFor(() => expect(invalidate).toHaveBeenCalledTimes(2));
	});

	it("reconciles replayed and out-of-order events by sequence", () => {
		const queryClient = new QueryClient();
		const wrapper = ({ children }: { children: ReactNode }) => (
			<QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
		);
		const { result } = renderHook(() => useSourceBuildHub(llamaCppSourceBuildStream, true), { wrapper });
		const currentBuild = descriptor("https://github.com/ggml-org/llama.cpp", "11111111-1111-4111-8111-111111111111");

		act(() =>
			hubMock.handler?.({
				phase: "Building",
				appendedLogStartSequence: 2,
				appendedLogLines: ["same", "same"],
				terminal: false,
				sanitizedError: null,
				currentBuild,
			}),
		);
		act(() =>
			hubMock.handler?.({
				phase: "Building",
				appendedLogStartSequence: 0,
				appendedLogLines: ["zero", "one", "same"],
				terminal: false,
				sanitizedError: null,
				currentBuild,
			}),
		);

		expect(result.current.logEntries).toEqual([
			{ sequence: 0, message: "zero" },
			{ sequence: 1, message: "one" },
			{ sequence: 2, message: "same" },
			{ sequence: 3, message: "same" },
		]);
	});

	it("clears stale live state and invalidates status/runtime on reconnect, then unregisters", async () => {
		const queryClient = new QueryClient();
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue(undefined);
		const wrapper = ({ children }: { children: ReactNode }) => (
			<QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
		);
		const { result, unmount } = renderHook(() => useSourceBuildHub(llamaCppSourceBuildStream, true), { wrapper });

		act(() =>
			hubMock.handler?.({
				phase: "Building",
				appendedLogStartSequence: 7,
				appendedLogLines: ["stale"],
				terminal: false,
				sanitizedError: null,
				currentBuild: descriptor("https://github.com/ggml-org/llama.cpp", "11111111-1111-4111-8111-111111111111"),
			}),
		);
		act(() => hubMock.reconnected?.());

		expect(result.current.logEntries).toEqual([]);
		expect(result.current.buildIdentity).toBeNull();
		await waitFor(() => expect(invalidate).toHaveBeenCalledTimes(2));

		unmount();
		expect(hubMock.unregisterReconnected).toHaveBeenCalledOnce();
		expect(hubMock.off).toHaveBeenCalledOnce();
		expect(hubMock.release).toHaveBeenCalledOnce();
	});

	it("rejects provider enum casing instead of accepting a mismatched wire payload", () => {
		const queryClient = new QueryClient();
		const wrapper = ({ children }: { children: ReactNode }) => (
			<QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
		);
		const { result } = renderHook(() => useSourceBuildHub(llamaCppSourceBuildStream, true), { wrapper });

		act(() =>
			hubMock.handler?.({
				phase: "Building",
				appendedLogStartSequence: 0,
				appendedLogLines: ["ignored"],
				terminal: false,
				sanitizedError: null,
				currentBuild: {
					...descriptor("https://github.com/ggml-org/llama.cpp", "11111111-1111-4111-8111-111111111111"),
					backend: "Cpu",
				},
			}),
		);

		expect(result.current.logEntries).toEqual([]);
	});

	it("subscribes the image runtime stream to its own hub and invalidates its own queries on terminal", async () => {
		const queryClient = new QueryClient();
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue(undefined);
		const wrapper = ({ children }: { children: ReactNode }) => (
			<QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
		);
		const { result } = renderHook(() => useSourceBuildHub(imageRuntimeSourceBuildStream, true), { wrapper });

		expect(hubMock.path).toBe("images/runtime/source-build/hub");
		expect(hubMock.on).toHaveBeenCalledWith("stableDiffusionCppSourceBuild.statusChanged", expect.any(Function));

		act(() =>
			hubMock.handler?.({
				phase: "Completed",
				appendedLogStartSequence: 0,
				appendedLogLines: ["done"],
				terminal: true,
				sanitizedError: null,
				currentBuild: descriptor("https://github.com/leejet/stable-diffusion.cpp", "33333333-3333-4333-8333-333333333333"),
			}),
		);

		expect(result.current.logEntries).toEqual([{ sequence: 0, message: "done" }]);
		await waitFor(() => expect(invalidate).toHaveBeenCalledTimes(2));
		expect(invalidate).toHaveBeenCalledWith({ queryKey: [{ _id: "getStableDiffusionCppSourceBuildStatus" }] });
		expect(invalidate).toHaveBeenCalledWith({ queryKey: [{ _id: "getImageRuntimeStatus" }] });
	});
});
