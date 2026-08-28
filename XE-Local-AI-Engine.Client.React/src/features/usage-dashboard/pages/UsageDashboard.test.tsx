// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import "@/i18n";

import type { GetAgentUsageSummaryResponse } from "@/core/api/generated";

const { generatedMock } = vi.hoisted(() => ({
	generatedMock: {
		getAgentUsageSummaryOptions: vi.fn(),
		summaryFn: vi.fn(),
		// The page resolves `external:{connectionId}` usage rows to connection NAMES from the external-provider
		// configuration, so the dashboard reads that list too.
		listExternalProviderConnectionsOptions: vi.fn(),
		connectionsFn: vi.fn(),
	},
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	getAgentUsageSummaryOptions: generatedMock.getAgentUsageSummaryOptions,
	listExternalProviderConnectionsOptions: generatedMock.listExternalProviderConnectionsOptions,
}));

import { UsageDashboard } from "@/features/usage-dashboard/pages/UsageDashboard";

function renderWithProviders(ui: ReactElement) {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	return render(
		<MantineProvider>
			<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>
		</MantineProvider>,
	);
}

describe("UsageDashboard (generated hey-api data layer)", () => {
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
		generatedMock.summaryFn.mockResolvedValue(createSummary());
		generatedMock.getAgentUsageSummaryOptions.mockImplementation(() => ({
			// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
			queryKey: [{ _id: "getAgentUsageSummary" }],
			queryFn: generatedMock.summaryFn,
		}));
		generatedMock.connectionsFn.mockResolvedValue({ revision: "rev-1", connections: [] });
		generatedMock.listExternalProviderConnectionsOptions.mockImplementation(() => ({
			// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
			queryKey: [{ _id: "listExternalProviderConnections" }],
			queryFn: generatedMock.connectionsFn,
		}));
	});

	it("renders totals, per-provider breakdown and per-model rows from the summary", async () => {
		renderWithProviders(<UsageDashboard />);

		expect(generatedMock.getAgentUsageSummaryOptions).toHaveBeenCalled();
		expect(await screen.findByRole("heading", { name: "Usage dashboard", level: 2 })).toBeTruthy();

		// Grand totals: compact total-tokens headline + exact run count.
		expect((await screen.findByTestId("usage-total-tokens-value")).textContent).toMatch(/1\.2M/);
		expect(screen.getByTestId("usage-run-count-value").textContent).toBe("42");

		// Per-provider breakdown: labeled providers incl. the graceful "Unknown" label.
		expect(screen.getByTestId("usage-provider-row-local")).toBeTruthy();
		expect(screen.getAllByText("Local (llama.cpp)").length).toBeGreaterThan(0);
		expect(screen.getByText("Unknown")).toBeTruthy();

		// Per-model table rows.
		expect(screen.getByTestId("usage-model-row-qwen3:8b")).toBeTruthy();
		expect(screen.getByTestId("usage-model-row-gpt-5")).toBeTruthy();

		// Estimated cost surfaces the server value: the paid codex model shows a currency figure; the free/local model
		// and the local provider row render the graceful em-dash. Totals show the grand cost.
		expect(screen.getByTestId("usage-estimated-cost-value").textContent).toMatch(/3[.,]50/);
		expect(screen.getByTestId("usage-model-cost-gpt-5").textContent).toMatch(/3[.,]50/);
		expect(screen.getByTestId("usage-model-cost-qwen3:8b").textContent).toBe("—");
		expect(screen.getByTestId("usage-provider-cost-codex").textContent).toMatch(/3[.,]50/);
		expect(screen.getByTestId("usage-provider-cost-local").textContent).toBe("—");
	});

	it("renders the empty-state guidance when no usage was recorded", async () => {
		generatedMock.summaryFn.mockResolvedValue({
			items: [],
			totals: { runCount: 0, promptTokens: 0, completionTokens: 0, reasoningTokens: 0, totalTokens: 0, estimatedCostUsd: 0, currency: "USD" },
			byProvider: [],
			retentionDays: 30,
		} satisfies GetAgentUsageSummaryResponse);

		renderWithProviders(<UsageDashboard />);

		expect(await screen.findByTestId("usage-empty")).toBeTruthy();
		expect(screen.getByText("No usage recorded yet")).toBeTruthy();
		expect(screen.queryByTestId("usage-totals")).toBeNull();
	});
});

function createSummary(): GetAgentUsageSummaryResponse {
	const dayOne = Date.UTC(2026, 4, 24);
	const dayTwo = Date.UTC(2026, 4, 25);
	return {
		items: [
			{
				modelName: "qwen3:8b",
				provider: "local",
				dayStartUtcMs: dayOne,
				runCount: 20,
				promptTokens: 300_000,
				completionTokens: 500_000,
				reasoningTokens: 20_000,
				totalTokens: 820_000,
				estimatedCostUsd: 0,
				currency: "USD",
			},
			{
				modelName: "gpt-5",
				provider: "codex",
				dayStartUtcMs: dayTwo,
				runCount: 22,
				promptTokens: 200_000,
				completionTokens: 200_000,
				reasoningTokens: 14_567,
				totalTokens: 414_567,
				estimatedCostUsd: 3.5,
				currency: "USD",
			},
		],
		totals: {
			runCount: 42,
			promptTokens: 500_000,
			completionTokens: 700_000,
			reasoningTokens: 34_567,
			totalTokens: 1_234_567,
			estimatedCostUsd: 3.5,
			currency: "USD",
		},
		byProvider: [
			{ provider: "local", runCount: 20, promptTokens: 300_000, completionTokens: 500_000, reasoningTokens: 20_000, totalTokens: 820_000, estimatedCostUsd: 0, currency: "USD" },
			{ provider: "codex", runCount: 22, promptTokens: 200_000, completionTokens: 200_000, reasoningTokens: 14_567, totalTokens: 414_567, estimatedCostUsd: 3.5, currency: "USD" },
			{ provider: "unknown", runCount: 0, promptTokens: 0, completionTokens: 0, reasoningTokens: 0, totalTokens: 0, estimatedCostUsd: 0, currency: "USD" },
		],
		retentionDays: 30,
	};
}
