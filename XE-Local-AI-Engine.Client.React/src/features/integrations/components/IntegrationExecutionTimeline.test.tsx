// @vitest-environment jsdom

import { cleanup, fireEvent, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import de from "@/locales/de.json";
import type { IntegrationExecutionEvent } from "@/features/integrations/models/IntegrationModels";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

// The stub interpolates like i18next does, and is a spy so a test can pin that a phrase went through a KEY rather
// than being hard-coded English the German UI would print untranslated.
const { translate } = vi.hoisted(() => ({
	translate: vi.fn((key: string, defaultValue?: string | Record<string, unknown>, options?: Record<string, unknown>) => {
		const config = typeof defaultValue === "object" ? defaultValue : options;
		let text = typeof defaultValue === "string" ? defaultValue : ((config?.["defaultValue"] as string) ?? key);
		if (config) {
			for (const [name, value] of Object.entries(config)) {
				text = text.replace(`{{${name}}}`, String(value));
			}
		}
		return text;
	}),
}));

vi.mock("react-i18next", () => ({ useTranslation: () => ({ t: translate }) }));

// Monaco is ~3 MB behind a lazy import and never reaches the DOM in a test, so the shared code editor stands in as a
// textarea carrying the same testid, value and language.
vi.mock("@/core/ui/components/CodeEditor/CodeEditor", () => ({
	CodeEditor: ({ value, language, "data-testid": testId }: { value: string; language?: string; "data-testid"?: string }) => (
		<textarea data-testid={testId} data-language={language} readOnly={true} value={value} />
	),
}));

import { IntegrationExecutionTimeline } from "@/features/integrations/components/IntegrationExecutionTimeline";

function event(overrides: Partial<IntegrationExecutionEvent> & { sequence: number; eventType: string }): IntegrationExecutionEvent {
	return { detailJson: null, occurredAtUtc: 1_700_000_000_000, ...overrides };
}

/** An output payload past the 4096-character threshold, which is the only size that gets the editor. */
function largePayload(): string {
	return "x".repeat(5000);
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

	it("renders a small external.output payload as plain text, with no editor and no disclosure", () => {
		renderWithMantine(<IntegrationExecutionTimeline events={events} isLoading={false} />);

		const output = screen.getByTestId("integration-execution-output");
		expect(output.tagName).not.toBe("TEXTAREA");
		expect(JSON.parse(output.textContent ?? "")).toEqual({ reading: 42 });
		expect(screen.queryByTestId("integration-execution-output-toggle")).toBeNull();
	});

	it("keeps a large payload behind a disclosure so its editor mounts only once opened", () => {
		renderWithMantine(
			<IntegrationExecutionTimeline
				events={[
					event({
						sequence: 2,
						eventType: "external.output",
						detailJson: JSON.stringify({ contentType: "application/json", payload: { blob: largePayload() } }),
					}),
				]}
				isLoading={false}
			/>,
		);

		expect(screen.queryByTestId("integration-execution-output")).toBeNull();
		fireEvent.click(screen.getByTestId("integration-execution-output-toggle"));

		const editor = screen.getByTestId("integration-execution-output") as HTMLTextAreaElement;
		expect(editor.tagName).toBe("TEXTAREA");
		expect(editor.getAttribute("data-language")).toBe("json");
	});

	it("takes the viewer language and the raw text from a non-JSON content type", () => {
		renderWithMantine(
			<IntegrationExecutionTimeline
				events={[
					event({
						sequence: 2,
						eventType: "external.output",
						detailJson: JSON.stringify({ contentType: "text/plain; charset=utf-8", payload: largePayload() }),
					}),
					event({
						sequence: 3,
						eventType: "external.output",
						detailJson: JSON.stringify({ contentType: "text/plain", payload: "plain body" }),
					}),
				]}
				isLoading={false}
			/>,
		);

		// The small one renders verbatim rather than as the quoted, escaped string JSON.stringify would produce.
		expect(screen.getByTestId("integration-execution-event-3").textContent).toContain("plain body");
		expect(screen.getByTestId("integration-execution-event-3").textContent).not.toContain('"plain body"');

		const large = within(screen.getByTestId("integration-execution-event-2"));
		fireEvent.click(large.getByTestId("integration-execution-output-toggle"));
		expect(large.getByTestId("integration-execution-output").getAttribute("data-language")).toBe("plaintext");
	});

	it("renders a payload of null as null rather than falling back to the whole envelope", () => {
		renderWithMantine(
			<IntegrationExecutionTimeline
				events={[
					event({
						sequence: 2,
						eventType: "external.output",
						detailJson: '{"contentType":"application/json","payload":null}',
					}),
				]}
				isLoading={false}
			/>,
		);

		expect(screen.getByTestId("integration-execution-output").textContent).toBe("null");
	});

	it("falls back to the raw text of an external.output it cannot parse", () => {
		renderWithMantine(
			<IntegrationExecutionTimeline
				events={[event({ sequence: 2, eventType: "external.output", detailJson: "not json at all" })]}
				isLoading={false}
			/>,
		);

		expect(screen.getByTestId("integration-execution-output").textContent).toBe("not json at all");
	});

	it("renders the detail line of each event type that carries a payload", () => {
		renderWithMantine(<IntegrationExecutionTimeline events={events} isLoading={false} />);

		expect(screen.getByTestId("integration-execution-event-3").textContent).toContain("read_file");
		expect(screen.getByTestId("integration-execution-event-5").textContent).toContain("read_file — error");
		const completed = screen.getByTestId("integration-execution-event-7").textContent ?? "";
		expect(completed).toContain("2.5s");
		expect(completed).toContain("128 tokens");
	});

	it("takes its own vocabulary from the locales, so the German UI does not print English", () => {
		renderWithMantine(<IntegrationExecutionTimeline events={events} isLoading={false} />);

		expect(translate).toHaveBeenCalledWith("pages.integrations.executions.detail.toolError", "error");
		expect(translate).toHaveBeenCalledWith(
			"pages.integrations.executions.detail.tokens",
			expect.objectContaining({ count: 128 }),
		);
		expect(de.pages.integrations.executions.detail.toolError).toBe("Fehler");
		expect(de.pages.integrations.executions.detail.toolOk).toBe("ok");
		expect(de.pages.integrations.executions.detail.tokens).toBe("{{count}} Tokens");
	});

	it("says it is loading the timeline rather than the execution list", () => {
		renderWithMantine(<IntegrationExecutionTimeline events={[]} isLoading={true} />);

		expect(translate).toHaveBeenCalledWith("pages.integrations.executions.detail.loading", "Loading timeline…");
		expect(de.pages.integrations.executions.detail.loading).toBe("Verlauf wird geladen…");
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
