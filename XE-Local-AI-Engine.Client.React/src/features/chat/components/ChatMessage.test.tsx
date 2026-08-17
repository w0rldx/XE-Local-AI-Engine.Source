// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement, ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The ModelNotInstalled error renders a TanStack-router Link to /models. Stub the router module so the component
// mounts without a RouterProvider in these unit tests (mirrors ModelManagement.test.tsx).
vi.mock("@tanstack/react-router", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@tanstack/react-router")>();
	return {
		...actual,
		Link: ({ children, to, ...props }: { children: ReactNode; to: string; [key: string]: unknown }) => (
			<a href={to} {...props}>
				{children}
			</a>
		),
	};
});

import { ChatMessage } from "@/features/chat/components/ChatMessage";
import type { ChatMessageModel } from "@/features/chat/models/ChatModels";
import { useNodeChatPreferencesStore } from "@/features/chat/stores/NodeChatPreferencesStore";

// A tool-call card in the ordered parts can now fire the resolve-approval TanStack mutation, so the render
// tree needs a QueryClientProvider even for turns that never surface an approval. Kept as an element helper (not a
// named component) so the test module stays fast-refresh-clean under biome's useComponentExportOnlyModules rule.
function withProviders(ui: ReactNode): ReactElement {
	const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
	return (
		<QueryClientProvider client={queryClient}>
			<MantineProvider>{ui}</MantineProvider>
		</QueryClientProvider>
	);
}

function renderWithProviders(ui: ReactElement) {
	return render(withProviders(ui));
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
		// The tokens/sec preference is a module-singleton store; reset it to the default (off) so a test that flips
		// it on does not leak into the others.
		useNodeChatPreferencesStore.getState().actions.setShowTokensPerSecond(false);
	});

	afterEach(() => {
		cleanup();
		useNodeChatPreferencesStore.getState().actions.setShowTokensPerSecond(false);
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
		renderWithProviders(
			<ChatMessage message={assistantMessage({ status: "streaming" })} isStreaming={true} onRegenerate={vi.fn()} />,
		);

		expect(screen.queryByLabelText("Copy message")).toBeNull();
		expect(screen.queryByLabelText("Regenerate response")).toBeNull();
	});

	it("does not offer regenerate on user messages", () => {
		const onRegenerate = vi.fn();
		renderWithProviders(
			<ChatMessage
				message={assistantMessage({ id: "user-1", role: "user", content: "Question?" })}
				onRegenerate={onRegenerate}
			/>,
		);

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
		renderWithProviders(
			<ChatMessage message={assistantMessage()} revisionNav={{ activeIndex: 1, total: 3, onPrevious, onNext: vi.fn() }} />,
		);

		expect(screen.getByTestId("message-revision-count-assistant-1").textContent).toBe("2/3");
		fireEvent.click(screen.getByLabelText("Previous revision"));
		expect(onPrevious).toHaveBeenCalled();
	});

	it("hides feedback controls unless enabled", () => {
		const { rerender } = renderWithProviders(
			<ChatMessage message={assistantMessage()} onSubmitFeedback={vi.fn()} showFeedbackControls={false} />,
		);
		expect(screen.queryByLabelText("Good response")).toBeNull();

		rerender(withProviders(<ChatMessage message={assistantMessage()} onSubmitFeedback={vi.fn()} showFeedbackControls={true} />));
		expect(screen.getByLabelText("Good response")).toBeTruthy();
		expect(screen.getByLabelText("Bad response")).toBeTruthy();
	});

	it("submits feedback with the chosen rating and comment", async () => {
		const onSubmitFeedback = vi.fn();
		renderWithProviders(
			<ChatMessage message={assistantMessage()} onSubmitFeedback={onSubmitFeedback} showFeedbackControls={true} />,
		);

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

	it("lets the attribution row wrap on narrow widths instead of overlaying the action icons", () => {
		renderWithProviders(
			<ChatMessage message={assistantMessage({ agentName: "My Custom Agent", model: "a-very-long-model-identifier-Q4_K_M" })} />,
		);

		const attribution = screen.getByTestId("chat-message-agent-assistant-1");
		const row = attribution.parentElement as HTMLElement;
		// Regression guard: nowrap + flexShrink:0 previously forced the metadata string across the icon row.
		expect(row.style.getPropertyValue("--group-wrap")).not.toBe("nowrap");
		expect(attribution.style.flexShrink).not.toBe("0");
		expect(attribution.style.marginLeft).toBe("auto");
	});

	it("falls back to Default Assistant label when agentName is absent on an assistant turn", () => {
		renderWithProviders(<ChatMessage message={assistantMessage({ agentName: undefined })} />);

		const attribution = screen.getByTestId("chat-message-agent-assistant-1");
		// i18n fallback key value in test environment
		expect(attribution.textContent).toBeTruthy();
	});

	it("does not render the attribution testid on user messages", () => {
		renderWithProviders(<ChatMessage message={assistantMessage({ id: "user-1", role: "user", content: "Question?" })} />);

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

	it("includes the model name segment in the attribution row when message.model is present", () => {
		// Audit cue: multi-provider threads must show which model produced each assistant turn. The outer
		// fallback "Model: {{model}}" is not interpolated in the test env, but the model id is passed as a var
		// and the "Model:" prefix proves the segment rendered.
		renderWithProviders(<ChatMessage message={assistantMessage({ model: "gpt-5.5", createdAt: "2026-06-08T10:00:00.000Z" })} />);

		const attribution = screen.getByTestId("chat-message-agent-assistant-1").textContent ?? "";
		expect(attribution).toContain("Model:");
		expect(attribution).toContain("·");
	});

	it("omits the model segment when message.model is absent", () => {
		renderWithProviders(<ChatMessage message={assistantMessage({ model: undefined })} />);

		const attribution = screen.getByTestId("chat-message-agent-assistant-1").textContent ?? "";
		expect(attribution).not.toContain("Model:");
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

	it("renders the friendly ModelNotInstalled message and a Models link for the no-chat-model failure", () => {
		// A "Local runtime default" send with no installed GGUF chat model surfaces FailureCategory ModelNotInstalled.
		// The alert must render the friendly i18n message + a "Go to Models" CTA, not just the raw backend string.
		renderWithProviders(
			<ChatMessage
				message={assistantMessage({ content: "", status: "failed", error: "No chat model installed. Pull a GGUF model to start chatting." })}
				failureCategory="ModelNotInstalled"
			/>,
		);

		const errorBlock = screen.getByTestId("chat-message-error-assistant-1");
		expect(errorBlock.textContent).toContain("No chat model installed. Pull a GGUF model to start chatting.");

		const modelsLink = screen.getByTestId("chat-message-error-models-link-assistant-1");
		expect(modelsLink.getAttribute("href")).toBe("/models");
	});

	it("renders the error block alongside partial content when a turn streamed text before failing", () => {
		// Regression for the MEDIUM bug: the original guard `!hasContentStarted` hid the error whenever
		// the turn had any content, so a partial-stream failure showed truncated text with no error indicator.
		// The error Alert must render AFTER the content Paper regardless of whether content is present.
		renderWithProviders(
			<ChatMessage
				message={assistantMessage({ content: "Partial answer so far…", status: "failed", error: "Stream failed mid-response." })}
			/>,
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

	it("renders the ⋮ options menu on a completed assistant turn", () => {
		renderWithProviders(<ChatMessage message={assistantMessage()} />);

		expect(screen.getByTestId("chat-message-menu-assistant-1")).toBeTruthy();
	});

	it("does not render the ⋮ options menu while streaming or on user turns", () => {
		const { rerender } = renderWithProviders(
			<ChatMessage message={assistantMessage({ status: "streaming", content: "" })} isStreaming={true} />,
		);
		expect(screen.queryByTestId("chat-message-menu-assistant-1")).toBeNull();

		rerender(withProviders(<ChatMessage message={assistantMessage({ id: "user-1", role: "user", content: "Question?" })} />));
		expect(screen.queryByTestId("chat-message-menu-user-1")).toBeNull();
	});

	it("does not show tokens/sec by default even when duration and tokens are present", () => {
		renderWithProviders(
			<ChatMessage
				message={assistantMessage({ generationDurationMs: 2000, outputTokens: 84, createdAt: "2026-06-05T10:00:00.000Z" })}
			/>,
		);

		const attribution = screen.getByTestId("chat-message-agent-assistant-1").textContent ?? "";
		expect(attribution).not.toContain("tok/s");
	});

	it("shows tokens/sec on the attribution row when the toggle is on and duration + output tokens are present", () => {
		// 84 tokens over 2.0s → 42 tok/s. The test env runs without full i18n init: t() returns the fallback string
		// and does NOT interpolate {{value}}, so the rendered segment is the literal "{{value}} tok/s" — its presence
		// proves the tps was computed (> 0) and the segment was added to the attribution row (same pattern the
		// reasoning-label tests use for "Reasoning:").
		useNodeChatPreferencesStore.getState().actions.setShowTokensPerSecond(true);
		renderWithProviders(
			<ChatMessage
				message={assistantMessage({ generationDurationMs: 2000, outputTokens: 84, createdAt: "2026-06-05T10:00:00.000Z" })}
			/>,
		);

		const attribution = screen.getByTestId("chat-message-agent-assistant-1").textContent ?? "";
		expect(attribution).toContain("tok/s");
	});

	it("hides tokens/sec when the toggle is on but the turn has no recorded duration (legacy turn)", () => {
		useNodeChatPreferencesStore.getState().actions.setShowTokensPerSecond(true);
		renderWithProviders(<ChatMessage message={assistantMessage({ outputTokens: 84, createdAt: "2026-06-05T10:00:00.000Z" })} />);

		const attribution = screen.getByTestId("chat-message-agent-assistant-1").textContent ?? "";
		expect(attribution).not.toContain("tok/s");
	});

	it("hides tokens/sec when the toggle is on but the duration is zero (no div-by-zero)", () => {
		useNodeChatPreferencesStore.getState().actions.setShowTokensPerSecond(true);
		renderWithProviders(
			<ChatMessage
				message={assistantMessage({ generationDurationMs: 0, outputTokens: 84, createdAt: "2026-06-05T10:00:00.000Z" })}
			/>,
		);

		const attribution = screen.getByTestId("chat-message-agent-assistant-1").textContent ?? "";
		expect(attribution).not.toContain("tok/s");
	});

	it("toggles the tokens/sec preference from the menu item", async () => {
		renderWithProviders(<ChatMessage message={assistantMessage()} />);

		expect(useNodeChatPreferencesStore.getState().showTokensPerSecond).toBe(false);

		// Open the menu (the dropdown mounts asynchronously via the portal), then click the checkable item; the menu
		// stays open (closeMenuOnClick=false) and the pref flips.
		fireEvent.click(screen.getByTestId("chat-message-menu-assistant-1"));
		const item = await screen.findByTestId("chat-message-menu-tps-assistant-1");
		fireEvent.click(item);

		expect(useNodeChatPreferencesStore.getState().showTokensPerSecond).toBe(true);
	});

	// A user cancellation is a neutral, expected outcome — never the red "Response failed" alert.
	it("renders a cancelled turn as a neutral 'Generation stopped' line, not the red error alert", () => {
		renderWithProviders(
			<ChatMessage message={assistantMessage({ content: "Partial answer…", status: "cancelled" })} />,
		);

		expect(screen.getByTestId("chat-message-stopped-assistant-1")).toBeTruthy();
		expect(screen.getByTestId("chat-message-stopped-assistant-1").textContent).toContain("Generation stopped");
		// The neutral line replaces — never accompanies — the error alert.
		expect(screen.queryByTestId("chat-message-error-assistant-1")).toBeNull();
		// Any partial content the model produced before the stop is still shown.
		expect(screen.getByText("Partial answer…")).toBeTruthy();
	});

	it("keeps a cancelled turn neutral even when the backend persisted an error string alongside it", () => {
		// Classification is driven by the terminal `status`, never by the (localized) error text — so a cancelled
		// run that carries a backend error string still renders the neutral stop, not the red failure alert.
		renderWithProviders(
			<ChatMessage message={assistantMessage({ content: "", status: "cancelled", error: "The operation was canceled." })} />,
		);

		expect(screen.getByTestId("chat-message-stopped-assistant-1")).toBeTruthy();
		expect(screen.queryByTestId("chat-message-error-assistant-1")).toBeNull();
	});

	it("still renders the red error alert for a genuinely failed turn (cancelled classification does not leak)", () => {
		renderWithProviders(
			<ChatMessage message={assistantMessage({ content: "", status: "failed", error: "Stream failed." })} />,
		);

		expect(screen.getByTestId("chat-message-error-assistant-1")).toBeTruthy();
		expect(screen.queryByTestId("chat-message-stopped-assistant-1")).toBeNull();
	});
});
