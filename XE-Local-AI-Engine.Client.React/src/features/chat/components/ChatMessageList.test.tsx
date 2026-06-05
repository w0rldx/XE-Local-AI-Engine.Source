// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
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
