// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { UsageModelTable } from "@/features/usage-dashboard/components/UsageModelTable";
import type { UsageModelRow } from "@/features/usage-dashboard/models/UsageDashboardModel";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

function row(overrides: Partial<UsageModelRow>): UsageModelRow {
	return {
		modelName: "qwen3:8b",
		providers: ["local"],
		runCount: 3,
		promptTokens: 100,
		completionTokens: 200,
		reasoningTokens: null,
		totalTokens: 300,
		tokensReported: 3,
		estimatedCostUsd: 0,
		...overrides,
	};
}

describe("UsageModelTable", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders a model whose runs reported no tokens as not reported instead of zero", () => {
		renderWithMantine(
			<UsageModelTable
				rows={[row({ modelName: "stopped", promptTokens: 0, completionTokens: 0, totalTokens: 0, tokensReported: 0 })]}
			/>,
		);

		expect(screen.getByTestId("usage-model-not-reported-stopped").textContent).toBe("Not reported");
	});

	it("shows the sum with a hint naming how many runs did not report", () => {
		renderWithMantine(<UsageModelTable rows={[row({ runCount: 3, tokensReported: 2 })]} />);

		expect(screen.getByTestId("usage-model-partly-reported-qwen3:8b").textContent).toBe("1 run not reported");
		expect(screen.queryByTestId("usage-model-not-reported-qwen3:8b")).toBeNull();
	});

	it("shows no hint when every run reported", () => {
		renderWithMantine(<UsageModelTable rows={[row({})]} />);

		expect(screen.queryByTestId("usage-model-partly-reported-qwen3:8b")).toBeNull();
	});
});
