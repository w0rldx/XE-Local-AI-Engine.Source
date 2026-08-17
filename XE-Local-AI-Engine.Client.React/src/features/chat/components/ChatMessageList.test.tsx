// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ChatMessageList } from "@/features/chat/components/ChatMessageList";
import type { ChatConversationModel, ChatMessageModel, ChatStreamingState } from "@/features/chat/models/ChatModels";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

function userMessage(overrides: Partial<ChatMessageModel> = {}): ChatMessageModel {
	return {
		id: "user-1",
		conversationId: "conversation-1",
		role: "user",
		content: "Hello?",
		status: "completed",
		createdAt: "2026-06-04T00:00:00.000Z",
		sortOrder: 1,
		...overrides,
	};
}

function failedAssistantMessage(overrides: Partial<ChatMessageModel> = {}): ChatMessageModel {
	return {
		id: "assistant-1",
		conversationId: "conversation-1",
		role: "assistant",
		content: "",
		status: "failed",
		error: "Stream timed out at hf.co/unsloth/model.",
		createdAt: "2026-06-04T00:00:01.000Z",
		sortOrder: 2,
		...overrides,
	};
}

function conversation(messages: ChatMessageModel[]): ChatConversationModel {
	return {
		id: "conversation-1",
		title: "Test",
		createdAt: "2026-06-04T00:00:00.000Z",
		updatedAt: "2026-06-04T00:00:01.000Z",
		messages,
	};
}

describe("ChatMessageList failed-turn rendering", () => {
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
		Element.prototype.scrollIntoView = vi.fn();
	});

	afterEach(() => {
		cleanup();
	});

	it("keeps a persisted failed turn (no content/reasoning) and renders its error block with regenerate", () => {
		const onRegenerate = vi.fn();
		const convo = conversation([userMessage(), failedAssistantMessage()]);

		renderWithProviders(<ChatMessageList conversation={convo} onRegenerate={onRegenerate} />);

		// Survives the normalizedMessages filter even though it has no content/reasoning.
		const errorBlocks = screen.getAllByTestId("chat-message-error-assistant-1");
		expect(errorBlocks).toHaveLength(1);
		expect(errorBlocks[0]?.textContent).toContain("Stream timed out at hf.co/unsloth/model.");
		// The regenerate affordance is present on the persisted failed turn.
		expect(screen.getByLabelText("Regenerate response")).toBeTruthy();
	});

	it("renders a live-stream error exactly once (inside the bubble, never in the footer)", () => {
		// Optimistic assistant row stamped at send time: empty content, not yet failed/persisted.
		const optimisticAssistant = failedAssistantMessage({ status: "streaming", error: undefined });
		const convo = conversation([userMessage(), optimisticAssistant]);
		const streamingMessage: ChatStreamingState = {
			conversationId: "conversation-1",
			messageId: "assistant-1",
			content: "",
			isActive: false,
			error: "Stream timed out at hf.co/unsloth/model.",
			failureCategory: "inter-chunk-stall",
		};

		renderWithProviders(<ChatMessageList conversation={convo} streamingMessage={streamingMessage} />);

		// Exactly one error render — the bubble's Alert. The footer (StreamingIndicator) no longer prints errors.
		const errorBlocks = screen.getAllByTestId("chat-message-error-assistant-1");
		expect(errorBlocks).toHaveLength(1);
		expect(screen.queryByTestId("chat-stream-error")).toBeNull();
		// The live failure category is folded into the same block.
		expect(screen.getByTestId("chat-message-error-category-assistant-1").textContent).toContain("inter-chunk-stall");
	});
});

describe("ChatMessageList conversation-load failure", () => {
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
		Element.prototype.scrollIntoView = vi.fn();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders an inline error with a Retry action (not a spinner) when the load failed", () => {
		const onRetry = vi.fn();
		renderWithProviders(
			<ChatMessageList
				conversation={conversation([])}
				isLoadingMessages={true}
				messagesLoadFailed={true}
				messagesLoadErrorText="Request failed with status code 500"
				onRetryLoadMessages={onRetry}
			/>,
		);

		// The error + retry surface, taking precedence over the loading spinner even though isLoadingMessages is true.
		const errorAlert = screen.getByTestId("chat-messages-load-error");
		expect(errorAlert.getAttribute("role")).toBe("alert");
		expect(errorAlert.textContent).toContain("Request failed with status code 500");
		expect(screen.queryByText("Loading messages…")).toBeNull();

		fireEvent.click(screen.getByTestId("chat-messages-load-retry"));
		expect(onRetry).toHaveBeenCalledTimes(1);
	});

	it("renders the accessible loading spinner while loading and not failed", () => {
		const { container } = renderWithProviders(
			<ChatMessageList conversation={conversation([])} isLoadingMessages={true} messagesLoadFailed={false} />,
		);

		expect(screen.getByText("Loading messages…")).toBeTruthy();
		expect(screen.queryByTestId("chat-messages-load-error")).toBeNull();
		// The busy region is announced to assistive tech.
		expect(container.querySelector('[role="status"][aria-busy="true"]')).toBeTruthy();
	});

	it("renders neither the error nor the loader once the conversation has messages", () => {
		renderWithProviders(
			<ChatMessageList
				conversation={conversation([userMessage()])}
				messagesLoadFailed={true}
				onRetryLoadMessages={vi.fn()}
			/>,
		);

		// A failed background refetch over an already-populated thread must not blow it away with an error.
		expect(screen.queryByTestId("chat-messages-load-error")).toBeNull();
		expect(screen.getByTestId("chat-message-user-1")).toBeTruthy();
	});
});

describe("ChatMessageList virtualization threshold", () => {
	function thread(count: number): ChatMessageModel[] {
		return Array.from({ length: count }, (_, index) =>
			userMessage({
				id: `user-${index + 1}`,
				content: `Message number ${index + 1}`,
				sortOrder: index + 1,
			}),
		);
	}

	it("renders every row plainly at or below the threshold (no virtual container)", () => {
		const { container } = renderWithProviders(<ChatMessageList conversation={conversation(thread(10))} />);

		expect(screen.queryByTestId("chat-message-list-virtual")).toBeNull();
		expect(container.querySelectorAll('[data-testid^="chat-message-user-"]')).toHaveLength(10);
	});

	it("switches to the windowed container above the threshold and does not mount every row", () => {
		const { container } = renderWithProviders(<ChatMessageList conversation={conversation(thread(60))} />);

		expect(screen.getByTestId("chat-message-list-virtual")).toBeTruthy();
		// jsdom reports no real viewport geometry, so the exact mounted count is estimate-driven — the invariant
		// under test is windowing itself: strictly fewer mounted rows than the 60 in the thread.
		const mounted = container.querySelectorAll("[data-testid=chat-message-list-virtual] > [data-index]").length;
		expect(mounted).toBeLessThan(60);
	});
});
