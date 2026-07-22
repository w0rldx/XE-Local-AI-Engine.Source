// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { generated } = vi.hoisted(() => ({
	generated: {
		startMutation: vi.fn(),
		startFn: vi.fn(),
	},
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	startLlamaCppSourceBuildMutation: generated.startMutation,
}));

import { useStartSourceBuild } from "@/features/node-settings/queries/useLocalRuntime";

describe("source build queries", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		generated.startFn.mockResolvedValue({ started: true });
		generated.startMutation.mockReturnValue({ mutationFn: generated.startFn });
	});

	it("sends the exact normalized custom build body", async () => {
		const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
		const wrapper = ({ children }: { children: ReactNode }) => (
			<QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
		);
		const { result } = renderHook(() => useStartSourceBuild(), { wrapper });

		await act(async () => {
			await result.current.mutateAsync({
				backend: "cuda",
				source: "custom",
				repository: " https://github.com/example/fork ",
				commit: "ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD",
				acknowledgeCustomSourceRisk: true,
			});
		});

		expect(generated.startFn).toHaveBeenCalledWith(
			{
				body: {
					backend: "cuda",
					source: "custom",
					repository: "https://github.com/example/fork",
					commit: "abcdefabcdefabcdefabcdefabcdefabcdefabcd",
					acknowledgeCustomSourceRisk: true,
				},
			},
			undefined,
		);
	});
});
