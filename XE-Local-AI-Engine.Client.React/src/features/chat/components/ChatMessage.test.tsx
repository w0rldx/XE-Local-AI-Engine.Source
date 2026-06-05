// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ChatMessage } from "@/features/chat/components/ChatMessage";
import type { ChatMessageModel } from "@/features/chat/models/ChatModels";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

function assistantMessage(overrides: Partial<ChatMessageModel> = {}): ChatMessageModel {
	return {
		id: "assistant-1",
		conversationId: "conversation-1",
		role: "assistant",
		content: "Here is the answer.",
		status: "completed",
		createdAt: "2026-05-24T00:00:01.000Z",
		sortOrder: 2,
		...overrides,
	};
}

describe("ChatMessage actions", () => {
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
		// jsdom lacks the FontFaceSet API that Mantine's autosize Textarea subscribes to.
		Object.defineProperty(document, "fonts", {
			configurable: true,
			value: { addEventListener: vi.fn(), removeEventListener: vi.fn() },
		});
		Object.assign(navigator, {
			clipboard: { writeText: vi.fn().mockResolvedValue(undefined) },
		});
	});

	afterEach(() => {
		cleanup();
	});

	it("copies the message content to the clipboard", async () => {
		renderWithProviders(<ChatMessage message={assistantMessage()} />);

		fireEvent.click(screen.getByLabelText("Copy message"));

		await waitFor(() => expect(navigator.clipboard.writeText).toHaveBeenCalledWith("Here is the answer."));
	});

	it("invokes onRegenerate with the assistant message id", () => {
		const onRegenerate = vi.fn();
		renderWithProviders(<ChatMessage message={assistantMessage()} onRegenerate={onRegenerate} />);

		fireEvent.click(screen.getByLabelText("Regenerate response"));

		expect(onRegenerate).toHaveBeenCalledWith("assistant-1");
	});

	it("hides actions while the assistant message is streaming", () => {
		renderWithProviders(<ChatMessage message={assistantMessage({ status: "streaming" })} isStreaming={true} onRegenerate={vi.fn()} />);

		expect(screen.queryByLabelText("Copy message")).toBeNull();
		expect(screen.queryByLabelText("Regenerate response")).toBeNull();
	});

	it("does not offer regenerate on user messages", () => {
		const onRegenerate = vi.fn();
		renderWithProviders(<ChatMessage message={assistantMessage({ id: "user-1", role: "user", content: "Question?" })} onRegenerate={onRegenerate} />);

		expect(screen.queryByLabelText("Regenerate response")).toBeNull();
		expect(screen.getByLabelText("Copy message")).toBeTruthy();
	});

	it("invokes onBranch with the assistant message id", () => {
		const onBranch = vi.fn();
		renderWithProviders(<ChatMessage message={assistantMessage()} onBranch={onBranch} />);

		fireEvent.click(screen.getByLabelText("Branch from here"));

		expect(onBranch).toHaveBeenCalledWith("assistant-1");
	});

	it("renders revision navigation and pages between siblings", () => {
		const onPrevious = vi.fn();
		renderWithProviders(<ChatMessage message={assistantMessage()} revisionNav={{ activeIndex: 1, total: 3, onPrevious, onNext: vi.fn() }} />);

		expect(screen.getByTestId("message-revision-count-assistant-1").textContent).toBe("2/3");
		fireEvent.click(screen.getByLabelText("Previous revision"));
		expect(onPrevious).toHaveBeenCalled();
	});

	it("hides feedback controls unless enabled", () => {
		const { rerender } = renderWithProviders(<ChatMessage message={assistantMessage()} onSubmitFeedback={vi.fn()} showFeedbackControls={false} />);
		expect(screen.queryByLabelText("Good response")).toBeNull();

		rerender(
			<MantineProvider>
				<ChatMessage message={assistantMessage()} onSubmitFeedback={vi.fn()} showFeedbackControls={true} />
			</MantineProvider>,
		);
		expect(screen.getByLabelText("Good response")).toBeTruthy();
		expect(screen.getByLabelText("Bad response")).toBeTruthy();
	});

	it("submits feedback with the chosen rating and comment", async () => {
		const onSubmitFeedback = vi.fn();
		renderWithProviders(<ChatMessage message={assistantMessage()} onSubmitFeedback={onSubmitFeedback} showFeedbackControls={true} />);

		fireEvent.click(screen.getByLabelText("Good response"));
		const comment = (await screen.findByTestId("message-feedback-comment-assistant-1")) as HTMLTextAreaElement;
		fireEvent.change(comment, { target: { value: "Clear and concise" } });
		fireEvent.click(screen.getByTestId("message-feedback-submit-assistant-1"));

		expect(onSubmitFeedback).toHaveBeenCalledWith("assistant-1", "up", "Clear and concise");
	});

	it("flags reasoning emitted while the 'none' effort is selected", () => {
		renderWithProviders(
			<ChatMessage message={assistantMessage({ reasoning: "Considering the request." })} reasoningEffort="none" />,
		);

		expect(screen.getByTestId("chat-message-reasoning-bypass-assistant-1")).toBeTruthy();
	});

	it("does not flag a bypass when the effort is not 'none'", () => {
		renderWithProviders(
			<ChatMessage message={assistantMessage({ reasoning: "Considering the request." })} reasoningEffort="medium" />,
		);

		expect(screen.queryByTestId("chat-message-reasoning-bypass-assistant-1")).toBeNull();
	});

	it("does not flag a bypass when 'none' is selected but no reasoning was emitted", () => {
		renderWithProviders(<ChatMessage message={assistantMessage()} reasoningEffort="none" />);

		expect(screen.queryByTestId("chat-message-reasoning-bypass-assistant-1")).toBeNull();
	});

	it("renders the agent name on the attribution row for assistant turns", () => {
		renderWithProviders(
			<ChatMessage message={assistantMessage({ agentName: "My Custom Agent", createdAt: "2026-06-03T10:00:00.000Z" })} />,
		);

		const attribution = screen.getByTestId("chat-message-agent-assistant-1");
		expect(attribution.textContent).toContain("My Custom Agent");
		expect(attribution.textContent).toContain("·");
	});

	it("falls back to Default Assistant label when agentName is absent on an assistant turn", () => {
		renderWithProviders(<ChatMessage message={assistantMessage({ agentName: undefined })} />);

		const attribution = screen.getByTestId("chat-message-agent-assistant-1");
		// i18n fallback key value in test environment
		expect(attribution.textContent).toBeTruthy();
	});

	it("does not render the attribution testid on user messages", () => {
		renderWithProviders(
			<ChatMessage message={assistantMessage({ id: "user-1", role: "user", content: "Question?" })} />,
		);

		expect(screen.queryByTestId("chat-message-agent-user-1")).toBeNull();
	});

	it("includes the reasoning label segment in the attribution row when reasoningEffort is present", () => {
		// The test environment runs without full i18n initialisation: t() returns fallback strings, and
		// variable interpolation inside fallbacks is not performed. The outer fallback for the reasoning
		// label is "Reasoning: {{effort}}" — its presence proves the segment was rendered.
		renderWithProviders(
			<ChatMessage message={assistantMessage({ reasoningEffort: "medium", createdAt: "2026-06-04T10:00:00.000Z" })} />,
		);

		const attribution = screen.getByTestId("chat-message-agent-assistant-1").textContent ?? "";
		// "Reasoning:" prefix proves the label segment is present (catches the case where it is omitted).
		expect(attribution).toContain("Reasoning:");
		// Three-segment row: agentName · Reasoning: … · time.
		expect(attribution).toContain("·");
	});

	it("includes the reasoning label for effort 'none' — it is never silently omitted", () => {
		// "none" (reasoning off) is a valid persisted value and must appear in the attribution row just
		// like any other effort. The label key "pages.chat.reasoning.effort.none" maps to "Off" in the
		// real locale (kept in sync with the composer menu's reasoningEffortOptions.none = "Off"); in
		// test env the fallback string is returned, but the segment is still present.
		renderWithProviders(
			<ChatMessage message={assistantMessage({ reasoningEffort: "none", createdAt: "2026-06-04T10:00:00.000Z" })} />,
		);

		const attribution = screen.getByTestId("chat-message-agent-assistant-1").textContent ?? "";
		expect(attribution).toContain("Reasoning:");
		expect(attribution).toContain("·");
	});

	it("omits the reasoning label from the attribution row when reasoningEffort is absent (legacy turn)", () => {
		renderWithProviders(
			<ChatMessage message={assistantMessage({ reasoningEffort: undefined, createdAt: "2026-06-04T10:00:00.000Z" })} />,
		);

		const attribution = screen.getByTestId("chat-message-agent-assistant-1").textContent ?? "";
		// No reasoning segment rendered — "Reasoning:" fallback text must not appear.
		expect(attribution).not.toContain("Reasoning:");
	});

	it("renders the ordered parts interleave: reasoning → tool card → reasoning", () => {
		renderWithProviders(
			<ChatMessage
				message={assistantMessage({
					parts: [
						{ kind: "reasoning", id: "assistant-1:0", sequence: 0, text: "first thoughts" },
						{ kind: "tool", id: "call-1", sequence: 1, name: "get_time", state: "received", result: "12:00" },
						{ kind: "reasoning", id: "assistant-1:2", sequence: 2, text: "second thoughts" },
					],
				})}
			/>,
		);

		// Two distinct folded Thoughts blocks (Option A) plus one tool card, all from the ordered parts.
		expect(screen.getByTestId("chat-message-reasoning-assistant-1:0")).toBeTruthy();
		expect(screen.getByTestId("chat-message-reasoning-assistant-1:2")).toBeTruthy();
		expect(screen.getByTestId("chat-tool-call-card-get_time")).toBeTruthy();
		// The trailing answer still renders from message.content.
		expect(screen.getByText("Here is the answer.")).toBeTruthy();
	});

	it("renders a persisted failed turn's error block once and offers regenerate", () => {
		const onRegenerate = vi.fn();
		renderWithProviders(
			<ChatMessage
				message={assistantMessage({ content: "", status: "failed", error: "Stream timed out at hf.co/unsloth/model." })}
				onRegenerate={onRegenerate}
			/>,
		);

		// Rendered exactly once, inside the assistant bubble, with the literal slash (no HTML entity).
		const errorBlocks = screen.getAllByTestId("chat-message-error-assistant-1");
		expect(errorBlocks).toHaveLength(1);
		expect(errorBlocks[0]?.textContent).toContain("Stream timed out at hf.co/unsloth/model.");
		expect(errorBlocks[0]?.textContent).not.toContain("&#x2F;");

		// The regenerate affordance is present on the failed turn (it is not streaming).
		fireEvent.click(screen.getByLabelText("Regenerate response"));
		expect(onRegenerate).toHaveBeenCalledWith("assistant-1");
	});

	it("folds the live failure category into the error block", () => {
		renderWithProviders(
			<ChatMessage
				message={assistantMessage({ content: "", status: "failed", error: "Inter-chunk stall." })}
				failureCategory="inter-chunk-stall"
			/>,
		);

		expect(screen.getByTestId("chat-message-error-category-assistant-1").textContent).toContain("inter-chunk-stall");
	});

	it("renders the error block alongside partial content when a turn streamed text before failing", () => {
		// Regression for the MEDIUM bug: the original guard `!hasContentStarted` hid the error whenever
		// the turn had any content, so a partial-stream failure showed truncated text with no error indicator.
		// The error Alert must render AFTER the content Paper regardless of whether content is present.
		renderWithProviders(
			<ChatMessage message={assistantMessage({ content: "Partial answer so far…", status: "failed", error: "Stream failed mid-response." })} />,
		);

		// Both the content and the error block are present.
		expect(screen.getByText("Partial answer so far…")).toBeTruthy();
		expect(screen.getByTestId("chat-message-error-assistant-1")).toBeTruthy();
		expect(screen.getByTestId("chat-message-error-assistant-1").textContent).toContain("Stream failed mid-response.");
	});

	it("renders the live tool card and its result from the streaming parts while streaming", () => {
		renderWithProviders(
			<ChatMessage
				message={assistantMessage({ status: "streaming", content: "" })}
				isStreaming={true}
				streamingParts={[
					{ kind: "reasoning", id: "assistant-1:0", sequence: 0, text: "thinking" },
					{ kind: "tool", id: "call-1", sequence: 1, name: "get_time", state: "received", result: "12:00" },
				]}
			/>,
		);

		expect(screen.getByTestId("chat-tool-call-result-get_time").textContent).toContain("12:00");
	});
});
