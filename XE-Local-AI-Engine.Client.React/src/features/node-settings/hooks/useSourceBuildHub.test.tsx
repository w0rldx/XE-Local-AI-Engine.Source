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

function descriptor(repository: string) {
	return {
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
				currentBuild: descriptor("https://github.com/ggml-org/llama.cpp"),
			}),
		);
		expect(result.current.logLines).toEqual(["old"]);

		act(() =>
			hubMock.handler?.({
				phase: "Completed",
				appendedLogLines: ["new"],
				terminal: true,
				sanitizedError: null,
				currentBuild: descriptor("https://github.com/example/fork"),
			}),
		);

		expect(result.current.logLines).toEqual(["new"]);
		await waitFor(() => expect(invalidate).toHaveBeenCalledTimes(2));
	});
});
