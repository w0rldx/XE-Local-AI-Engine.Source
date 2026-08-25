// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { WorkSessionEventsTab } from "@/features/workSessions/components/WorkSessionEventsTab";
import type { WorkSessionEventResponse } from "@/features/workSessions/models/WorkSessionModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

function event(id: string, overrides: Partial<WorkSessionEventResponse> = {}): WorkSessionEventResponse {
	return {
		id,
		sequence: 1,
		step: 1,
		eventType: "StepEnded",
		detailJson: null,
		outcome: "Completed",
		occurredAtUtc: 0,
		operationId: null,
		...overrides,
	};
}

function consumption(overrides: Record<string, unknown> = {}): string {
	return JSON.stringify({
		providerCalls: 7,
		estimatedInputTokens: 18_247,
		toolCallsCompleted: 3,
		providerCallCap: 10,
		...overrides,
	});
}

function renderTab(events: readonly WorkSessionEventResponse[]) {
	renderWithProviders(<WorkSessionEventsTab events={events} hasMore={false} canLoadMore={false} onLoadMore={vi.fn()} />);
}

describe("WorkSessionEventsTab", () => {
	afterEach(() => {
		cleanup();
	});

	it("summarises what a step spent beside the outcome", () => {
		renderTab([event("e1", { detailJson: consumption() })]);

		expect(screen.getByTestId("work-session-event-consumption-e1").textContent).toBe("7/10 provider calls · 3 tool calls · ~18.2k est. input tokens");
		expect(screen.getByTestId("work-session-event-outcome-e1").textContent).toBe("Completed");
	});

	it("adds the provider's own count when the turn reported one", () => {
		renderTab([event("e2", { detailJson: consumption({ usageInputTokens: 21_400, usageOutputTokens: 900 }) })]);

		expect(screen.getByTestId("work-session-event-consumption-e2").textContent).toBe(
			"7/10 provider calls · 3 tool calls · ~18.2k est. input tokens · 21.4k reported",
		);
	});

	it("summarises a failed step the same way", () => {
		renderTab([event("e3", { eventType: "StepFailed", outcome: "1", detailJson: consumption({ providerCalls: 2, toolCallsCompleted: 0, estimatedInputTokens: 640 }) })]);

		expect(screen.getByTestId("work-session-event-consumption-e3").textContent).toBe("2/10 provider calls · 0 tool calls · ~640 est. input tokens");
	});

	it("renders nothing extra for a detail payload of some other shape", () => {
		// The column is opaque and other writers use it for their own shapes — CompletionRequested carries a summary.
		renderTab([
			event("e4", { eventType: "CompletionRequested", detailJson: JSON.stringify({ summary: "Everything is done." }) }),
			event("e5", { detailJson: JSON.stringify({ providerCalls: 7 }) }),
		]);

		expect(screen.queryByTestId("work-session-event-consumption-e4")).toBeNull();
		expect(screen.queryByTestId("work-session-event-consumption-e5")).toBeNull();
	});

	it("survives a detail payload that is not JSON at all", () => {
		renderTab([event("e6", { detailJson: "{not json" })]);

		expect(screen.queryByTestId("work-session-event-consumption-e6")).toBeNull();
		expect(screen.getByTestId("work-session-event-e6")).toBeDefined();
	});

	it("still offers the raw payload behind the detail toggle", () => {
		renderTab([event("e7", { detailJson: consumption() })]);

		expect(screen.getByTestId("work-session-event-toggle-e7")).toBeDefined();
	});

	it("says so when the session has recorded nothing", () => {
		renderTab([]);

		expect(screen.getByTestId("work-session-events-empty")).toBeDefined();
	});
});
