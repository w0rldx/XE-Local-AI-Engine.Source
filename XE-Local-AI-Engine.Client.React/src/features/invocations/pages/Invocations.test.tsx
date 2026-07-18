// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import "@/i18n";

import type { GetInvocationMonitorResponse } from "@/core/api/generated";

const { generatedMock } = vi.hoisted(() => ({
	generatedMock: {
		getInvocationMonitorOptions: vi.fn(),
		monitorFn: vi.fn(),
	},
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	getInvocationMonitorOptions: generatedMock.getInvocationMonitorOptions,
}));

import { Invocations } from "@/features/invocations/pages/Invocations";

function renderWithProviders(ui: ReactElement) {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	return render(
		<MantineProvider>
			<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>
		</MantineProvider>,
	);
}

describe("Invocations (generated hey-api data layer)", () => {
	afterEach(() => {
		cleanup();
	});

	beforeEach(() => {
		vi.clearAllMocks();
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation((query: string) => ({
				matches: false,
				media: query,
				onchange: null,
				addEventListener: vi.fn(),
				removeEventListener: vi.fn(),
				dispatchEvent: vi.fn(),
			})),
		});
		Object.defineProperty(window, "ResizeObserver", {
			writable: true,
			value: class ResizeObserverMock {
				observe = vi.fn();

				unobserve = vi.fn();

				disconnect = vi.fn();
			},
		});
		generatedMock.monitorFn.mockResolvedValue(createMonitor());
		generatedMock.getInvocationMonitorOptions.mockImplementation(() => ({
			// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
			queryKey: [{ _id: "getInvocationMonitor" }],
			queryFn: generatedMock.monitorFn,
		}));
	});

	it("renders current invocation and history through the generated query options", async () => {
		renderWithProviders(<Invocations />);

		expect(generatedMock.getInvocationMonitorOptions).toHaveBeenCalled();
		expect(await screen.findByRole("heading", { name: "Invocation monitor", level: 2 })).toBeTruthy();
		expect(await screen.findByText(/Model: qwen3:8b/)).toBeTruthy();
		expect(screen.getByText("Active")).toBeTruthy();
		expect(screen.getByText("Pending approval: Yes")).toBeTruthy();
		expect(screen.getByText("Invocation ended with a failure. See local logs for details.")).toBeTruthy();
		expect(screen.getByText("1.0 min")).toBeTruthy();
	});

	it("renders idle and empty history states", async () => {
		generatedMock.monitorFn.mockResolvedValue({ current: null, history: [], historyCapacity: 50 });

		renderWithProviders(<Invocations />);

		expect(await screen.findByText("No invocation is currently assigned or running.")).toBeTruthy();
		expect(screen.getByText("No completed invocations recorded yet.")).toBeTruthy();
		expect(screen.getByText("Idle")).toBeTruthy();
	});

	it("refreshes monitor data through the generated query function", async () => {
		renderWithProviders(<Invocations />);
		await screen.findByRole("heading", { name: "Invocation monitor", level: 2 });

		const refreshButton = screen.getByRole("button", { name: "Refresh" }) as HTMLButtonElement;
		await waitFor(() => expect(refreshButton.disabled).toBe(false));
		fireEvent.click(refreshButton);

		await waitFor(() => expect(generatedMock.monitorFn).toHaveBeenCalledTimes(2));
	});
});

function createMonitor(): GetInvocationMonitorResponse {
	return {
		current: {
			invocationId: "11111111-1111-1111-1111-111111111111",
			conversationId: "33333333-3333-3333-3333-333333333333",
			status: "Running",
			modelUsed: "qwen3:8b",
			startedAt: "2026-05-25T09:59:50Z",
			lastUpdatedAt: "2026-05-25T10:00:00Z",
			completedAt: null,
			error: null,
			failureCategory: null,
			streamedChunkCount: 2,
			streamedThinkingChunkCount: 1,
			pendingToolCallCount: 1,
			hasPendingApproval: true,
		},
		history: [
			{
				invocationId: "22222222-2222-2222-2222-222222222222",
				conversationId: "44444444-4444-4444-4444-444444444444",
				status: "Failed",
				modelUsed: "qwen3:0.6b",
				startedAt: "2026-05-25T09:55:00Z",
				completedAt: "2026-05-25T09:56:00Z",
				durationMs: 60_000,
				error: "Invocation ended with a failure. See local logs for details.",
				failureCategory: "AgentRuntime",
				streamedChunkCount: 3,
				streamedThinkingChunkCount: 2,
			},
		],
		historyCapacity: 50,
	};
}
