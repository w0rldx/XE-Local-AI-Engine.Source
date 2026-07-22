// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { hubMock } = vi.hoisted(() => ({
	hubMock: {
		handler: undefined as ((payload: unknown) => void) | undefined,
		release: vi.fn(),
		on: vi.fn((_name: string, handler: (payload: unknown) => void) => {
			hubMock.handler = handler;
		}),
		off: vi.fn(),
	},
}));

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: () => ({
		connection: { on: hubMock.on, off: hubMock.off },
		release: hubMock.release,
	}),
}));

import { useSourceBuildHub } from "@/features/node-settings/hooks/useSourceBuildHub";

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
		hubMock.handler = undefined;
		vi.clearAllMocks();
	});

	it("resets live logs for a new build identity and invalidates status/runtime on terminal", async () => {
		const queryClient = new QueryClient();
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue(undefined);
		const wrapper = ({ children }: { children: ReactNode }) => (
			<QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
		);
		const { result } = renderHook(() => useSourceBuildHub(true), { wrapper });

		act(() =>
			hubMock.handler?.({
				phase: "Building",
				appendedLogLines: ["old"],
				terminal: false,
				sanitizedError: null,
				currentBuild: descriptor("https://github.com/ggml-org/llama.cpp", "11111111-1111-4111-8111-111111111111"),
			}),
		);
		expect(result.current.logLines).toEqual(["old"]);

		act(() =>
			hubMock.handler?.({
				phase: "Completed",
				appendedLogLines: ["new"],
				terminal: true,
				sanitizedError: null,
				currentBuild: descriptor("https://github.com/ggml-org/llama.cpp", "22222222-2222-4222-8222-222222222222"),
			}),
		);

		expect(result.current.logLines).toEqual(["new"]);
		await waitFor(() => expect(invalidate).toHaveBeenCalledTimes(2));
	});

	it("rejects provider enum casing instead of accepting a mismatched wire payload", () => {
		const queryClient = new QueryClient();
		const wrapper = ({ children }: { children: ReactNode }) => (
			<QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
		);
		const { result } = renderHook(() => useSourceBuildHub(true), { wrapper });

		act(() =>
			hubMock.handler?.({
				phase: "Building",
				appendedLogLines: ["ignored"],
				terminal: false,
				sanitizedError: null,
				currentBuild: {
					...descriptor("https://github.com/ggml-org/llama.cpp", "11111111-1111-4111-8111-111111111111"),
					backend: "Cpu",
				},
			}),
		);

		expect(result.current.logLines).toEqual([]);
	});
});
