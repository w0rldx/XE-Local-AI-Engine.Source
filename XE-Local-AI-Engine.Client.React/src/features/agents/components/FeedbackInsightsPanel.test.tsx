// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string, options?: Record<string, unknown>) => {
			let text = defaultValue ?? _key;
			if (options) {
				for (const [name, value] of Object.entries(options)) {
					text = text.replace(`{{${name}}}`, String(value));
				}
			}
			return text;
		},
	}),
}));

const { hooksMock } = vi.hoisted(() => ({
	hooksMock: {
		useFeedbackInsights: vi.fn(),
	},
}));

vi.mock("@/features/agents/queries/useFeedbackInsights", () => hooksMock);

import { FeedbackInsightsPanel } from "@/features/agents/components/FeedbackInsightsPanel";
import type { FeedbackInsights } from "@/features/agents/models/FeedbackInsightsModels";

function makeInsights(overrides: Partial<FeedbackInsights> = {}): FeedbackInsights {
	return {
		agentDefinitionId: "agent-1",
		agentName: "Researcher",
		generatedAtUtc: 1700,
		minOccurrenceThreshold: 3,
		overall: { total: 5, up: 3, down: 2, downRate: 0.4, meetsThreshold: true },
		byTool: [{ toolName: "search", total: 4, up: 3, down: 1, downRate: 0.25, meetsThreshold: true }],
		exemplars: [
			{
				rating: "down",
				comment: "Too slow",
				messageId: "msg-1",
				conversationId: "conv-1",
				createdAtUtc: 1500,
				truncated: false,
			},
		],
		...overrides,
	};
}

function installJsdomEnvironmentMocks(): void {
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
	Object.defineProperty(document, "fonts", {
		writable: true,
		value: { ready: Promise.resolve(), addEventListener: vi.fn(), removeEventListener: vi.fn() },
	});
}

function renderPanel(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("FeedbackInsightsPanel", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		hooksMock.useFeedbackInsights.mockReturnValue({ data: makeInsights(), isLoading: false, error: null });
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders nothing when the capability gate is off", () => {
		renderPanel(<FeedbackInsightsPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={false} />);

		expect(screen.queryByTestId("feedback-insights-panel-agent-1")).toBeNull();
		// The query is disabled (null agent id) when the panel is gated off.
		expect(hooksMock.useFeedbackInsights).toHaveBeenCalledWith(null);
	});

	it("renders the overall counts, down rate, per-tool table, and exemplars", () => {
		renderPanel(<FeedbackInsightsPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("feedback-insights-up").textContent).toBe("Up 3");
		expect(screen.getByTestId("feedback-insights-down").textContent).toBe("Down 2");
		expect(screen.getByTestId("feedback-insights-down-rate").textContent).toBe("Down rate 40%");
		expect(screen.getByTestId("feedback-insights-tool-search")).toBeTruthy();
		expect(screen.getByTestId("feedback-insights-exemplar-msg-1")).toBeTruthy();
		expect(screen.getByTestId("feedback-insights-exemplar-rating-msg-1").textContent).toBe("down");
		expect(screen.getByText("Too slow")).toBeTruthy();
		expect(screen.getByTestId("feedback-insights-attribution")).toBeTruthy();
	});

	it("shows the empty state when there is no feedback", () => {
		hooksMock.useFeedbackInsights.mockReturnValue({
			data: makeInsights({
				overall: { total: 0, up: 0, down: 0, downRate: 0, meetsThreshold: false },
				byTool: [],
				exemplars: [],
			}),
			isLoading: false,
			error: null,
		});

		renderPanel(<FeedbackInsightsPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("feedback-insights-empty")).toBeTruthy();
		expect(screen.queryByTestId("feedback-insights-overall")).toBeNull();
	});

	it("flags a sub-threshold overall row with a not-enough-signal label", () => {
		hooksMock.useFeedbackInsights.mockReturnValue({
			data: makeInsights({
				overall: { total: 2, up: 1, down: 1, downRate: 0.5, meetsThreshold: false },
				byTool: [],
				exemplars: [],
			}),
			isLoading: false,
			error: null,
		});

		renderPanel(<FeedbackInsightsPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("feedback-insights-overall-threshold").textContent).toBe("not enough signal (n < 3)");
	});

	it("flags a sub-threshold per-tool row with a not-enough-signal label", () => {
		hooksMock.useFeedbackInsights.mockReturnValue({
			data: makeInsights({
				byTool: [{ toolName: "search", total: 2, up: 1, down: 1, downRate: 0.5, meetsThreshold: false }],
			}),
			isLoading: false,
			error: null,
		});

		renderPanel(<FeedbackInsightsPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("feedback-insights-tool-threshold-search").textContent).toBe("not enough signal (n < 3)");
	});

	it("renders a truncated exemplar comment verbatim (backend already appends the ellipsis)", () => {
		hooksMock.useFeedbackInsights.mockReturnValue({
			data: makeInsights({
				exemplars: [
					{
						rating: "up",
						// The backend truncates and appends the ellipsis itself, so a truncated comment already ends with "…".
						comment: "Great answer but…",
						messageId: "msg-2",
						conversationId: "conv-2",
						createdAtUtc: 1600,
						truncated: true,
					},
				],
			}),
			isLoading: false,
			error: null,
		});

		renderPanel(<FeedbackInsightsPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		// A single ellipsis — the panel must NOT append a second one.
		expect(screen.getByText("Great answer but…")).toBeTruthy();
		expect(screen.queryByText("Great answer but……")).toBeNull();
		expect(screen.getByTestId("feedback-insights-exemplar-rating-msg-2").textContent).toBe("up");
	});

	it("surfaces a load error", () => {
		hooksMock.useFeedbackInsights.mockReturnValue({ data: undefined, isLoading: false, error: new Error("boom") });

		renderPanel(<FeedbackInsightsPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("feedback-insights-error")).toBeTruthy();
	});
});
