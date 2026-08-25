// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import i18next from "i18next";
import { afterEach, describe, expect, it, vi } from "vitest";

import de from "@/locales/de.json";

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
		attachedBudgets: 1,
		...overrides,
	});
}

function renderTab(events: readonly WorkSessionEventResponse[]) {
	renderWithProviders(<WorkSessionEventsTab events={events} hasMore={false} canLoadMore={false} onLoadMore={vi.fn()} />);
}

describe("WorkSessionEventsTab", () => {
	afterEach(async () => {
		cleanup();
		// The i18next instance is the app's real one and is shared across this file's tests, so a language switch
		// has to be undone or every later assertion reads German.
		await i18next.changeLanguage("en");
	});

	it("summarises what a step spent beside the outcome", () => {
		renderTab([event("e1", { detailJson: consumption() })]);

		expect(screen.getByTestId("work-session-event-consumption-e1").textContent).toBe("7/10 provider calls · 3 tool calls · ~18.2k est. input tokens");
		expect(screen.getByTestId("work-session-event-outcome-e1").textContent).toBe("Completed");
	});

	it("ignores a provider usage figure rather than reporting it beside the step totals", () => {
		// UsageSnapshot is assigned per provider round, not accumulated, so it is the LAST round's count. Printing it
		// next to a step-total call count and a step-total estimate would read as a contradiction.
		renderTab([event("e2", { detailJson: consumption({ usageInputTokens: 21_400, usageOutputTokens: 900 }) })]);

		expect(screen.getByTestId("work-session-event-consumption-e2").textContent).toBe("7/10 provider calls · 3 tool calls · ~18.2k est. input tokens");
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

	it("says how many budgets a step ran rather than showing a ratio the cap never bounded", () => {
		// The cap bounds each invocation, not their sum. "18/10" here would read as a breached cap and argue for
		// raising one that nothing actually hit.
		renderTab([event("e8", { detailJson: consumption({ providerCalls: 18, toolCallsCompleted: 9, attachedBudgets: 2 }) })]);

		const line = screen.getByTestId("work-session-event-consumption-e8").textContent;
		expect(line).toBe("18 provider calls across 2 budgets (cap 10 each) · 9 tool calls · ~18.2k est. input tokens");
		expect(line).not.toContain("18/10");
	});

	it("reads a row written before attachedBudgets existed as a single budget", () => {
		const { attachedBudgets: _omitted, ...legacy } = JSON.parse(consumption()) as Record<string, number>;
		renderTab([event("e9", { detailJson: JSON.stringify(legacy) })]);

		expect(screen.getByTestId("work-session-event-consumption-e9").textContent).toBe(
			"7/10 provider calls · 3 tool calls · ~18.2k est. input tokens",
		);
	});

	it("formats the token magnitude in the active locale", async () => {
		// Regression for a hardcoded toFixed(1): a German reader writes 18,2k, not 18.2k. The separator comes from
		// Intl on the active language and the "k" from the catalogue, so neither is baked into the component.
		i18next.addResourceBundle("de", "translation", de, true, true);
		await i18next.changeLanguage("de");
		renderTab([event("e10", { detailJson: consumption() })]);

		expect(screen.getByTestId("work-session-event-consumption-e10").textContent).toBe(
			"7/10 Provider-Aufrufe · 3 Tool-Aufrufe · ~18,2k geschätzte Eingabe-Tokens",
		);
	});

	it("says so when the session has recorded nothing", () => {
		renderTab([]);

		expect(screen.getByTestId("work-session-events-empty")).toBeDefined();
	});
});
