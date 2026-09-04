// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { IntegrationExecutionEvent } from "@/features/integrations/models/IntegrationModels";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (key: string, defaultValue?: string) => defaultValue ?? key }),
}));

// Monaco is ~3 MB behind a lazy import and never reaches the DOM in a test, so the shared code editor stands in as a
// textarea carrying the same testid and value.
vi.mock("@/core/ui/components/CodeEditor/CodeEditor", () => ({
	CodeEditor: ({ value, "data-testid": testId }: { value: string; "data-testid"?: string }) => (
		<textarea data-testid={testId} readOnly={true} value={value} />
	),
}));

import { IntegrationExecutionTimeline } from "@/features/integrations/components/IntegrationExecutionTimeline";

function event(overrides: Partial<IntegrationExecutionEvent> & { sequence: number; eventType: string }): IntegrationExecutionEvent {
	return { detailJson: null, occurredAtUtc: 1_700_000_000_000, ...overrides };
}

// Sequence 4 is deliberately missing: a failed durable write leaves a permanent hole, and the timeline must render
// the two rows it has rather than treat the gap as a missing row.
const events: IntegrationExecutionEvent[] = [
	event({ sequence: 1, eventType: "execution.accepted" }),
	event({ sequence: 2, eventType: "execution.started" }),
	event({ sequence: 3, eventType: "tool.started", detailJson: '{"name":"read_file"}' }),
	event({ sequence: 5, eventType: "tool.completed", detailJson: '{"name":"read_file","ok":false}' }),
	event({
		sequence: 6,
		eventType: "external.output",
		detailJson: '{"contentType":"application/json","payload":{"reading":42}}',
	}),
	event({ sequence: 7, eventType: "execution.completed", detailJson: '{"tokens":128,"durationMs":2500}' }),
];

describe("IntegrationExecutionTimeline", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders every event in the order supplied, gaps included", () => {
		renderWithMantine(<IntegrationExecutionTimeline events={events} isLoading={false} />);

		const rendered = Array.from(
			screen.getByTestId("integration-execution-timeline").querySelectorAll("[data-testid^='integration-execution-event-']"),
		).map((row) => row.getAttribute("data-testid"));
		expect(rendered).toEqual([1, 2, 3, 5, 6, 7].map((sequence) => `integration-execution-event-${sequence}`));
	});

	it("renders an external.output payload into the read-only editor", () => {
		renderWithMantine(<IntegrationExecutionTimeline events={events} isLoading={false} />);

		const editor = screen.getByTestId("integration-execution-output") as HTMLTextAreaElement;
		expect(JSON.parse(editor.value)).toEqual({ reading: 42 });
	});

	it("renders the detail line of each event type that carries a payload", () => {
		renderWithMantine(<IntegrationExecutionTimeline events={events} isLoading={false} />);

		expect(screen.getByTestId("integration-execution-event-3").textContent).toContain("read_file");
		expect(screen.getByTestId("integration-execution-event-5").textContent).toContain("read_file — error");
		const completed = screen.getByTestId("integration-execution-event-7").textContent ?? "";
		expect(completed).toContain("2.5s");
		expect(completed).toContain("128 tokens");
	});

	it("renders a failure category and summary verbatim", () => {
		renderWithMantine(
			<IntegrationExecutionTimeline
				events={[
					event({
						sequence: 2,
						eventType: "execution.failed",
						detailJson: '{"category":"queue-timeout","summary":"Waited past MaxQueueAgeSeconds."}',
					}),
				]}
				isLoading={false}
			/>,
		);

		expect(screen.getByTestId("integration-execution-event-2").textContent).toContain(
			"queue-timeout — Waited past MaxQueueAgeSeconds.",
		);
	});

	it("omits the token count a provider never reported rather than printing a null", () => {
		renderWithMantine(
			<IntegrationExecutionTimeline
				events={[event({ sequence: 9, eventType: "execution.completed", detailJson: '{"durationMs":1000}' })]}
				isLoading={false}
			/>,
		);

		const line = screen.getByTestId("integration-execution-event-9").textContent ?? "";
		expect(line).toContain("1.0s");
		expect(line).not.toContain("null");
		expect(line).not.toContain("tokens");
	});

	it("survives a detail payload it cannot parse", () => {
		renderWithMantine(
			<IntegrationExecutionTimeline
				events={[event({ sequence: 3, eventType: "tool.started", detailJson: "not json at all" })]}
				isLoading={false}
			/>,
		);

		expect(screen.getByTestId("integration-execution-event-3")).toBeTruthy();
	});

	it("says so when an execution recorded no events", () => {
		renderWithMantine(<IntegrationExecutionTimeline events={[]} isLoading={false} />);

		expect(screen.getByTestId("integration-execution-timeline-empty")).toBeTruthy();
	});
});
