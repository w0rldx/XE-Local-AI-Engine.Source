// @vitest-environment jsdom

import { act, cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ChatInputArea } from "@/features/chat/components/ChatInputArea";
import { usePendingComposerTextStore } from "@/core/ui/stores/PendingComposerTextStore";
import { renderWithProviders } from "@/test/RenderWithProviders";

function baseProps() {
	return {
		availableReasoningEfforts: ["none" as const],
		isSending: false,
		modelOptions: [{ value: "model-a", label: "Model A", isAvailable: true }],
		selectedModel: "model-a",
		reasoningEffort: "none" as const,
		onCancel: vi.fn(),
		onModelChange: vi.fn(),
		onReasoningEffortChange: vi.fn(),
		onSend: vi.fn(),
	};
}

function composer(): HTMLTextAreaElement {
	return screen.getByRole("textbox") as HTMLTextAreaElement;
}

describe("ChatInputArea pending composer text", () => {
	beforeEach(() => {
		usePendingComposerTextStore.setState({ pendingText: "" });
	});

	afterEach(() => {
		cleanup();
	});

	it("inserts text another page staged for the composer", () => {
		usePendingComposerTextStore.getState().actions.setPendingText("a transcript of the standup");
		renderWithProviders(<ChatInputArea {...baseProps()} />);

		expect(composer().value).toBe("a transcript of the standup");
	});

	it("appends to an existing draft with exactly one space", () => {
		renderWithProviders(<ChatInputArea {...baseProps()} />);
		fireEvent.change(composer(), { target: { value: "Summarise this: " } });

		// The store is written from outside React (the transcription page navigates away), so the subscribing render
		// has to be flushed before the composer is read.
		act(() => usePendingComposerTextStore.getState().actions.setPendingText("a transcript"));

		expect(composer().value).toBe("Summarise this: a transcript");
	});

	// The store is drained on consume, so a component that remounts (a route change and back) must not paste the same
	// transcript into the composer a second time.
	it("empties the store so a remount does not re-insert the text", () => {
		usePendingComposerTextStore.getState().actions.setPendingText("a transcript");
		const first = renderWithProviders(<ChatInputArea {...baseProps()} />);

		expect(composer().value).toBe("a transcript");
		expect(usePendingComposerTextStore.getState().pendingText).toBe("");

		first.unmount();
		renderWithProviders(<ChatInputArea {...baseProps()} />);

		expect(composer().value).toBe("");
	});

	it("leaves the composer alone when nothing is staged", () => {
		renderWithProviders(<ChatInputArea {...baseProps()} />);

		expect(composer().value).toBe("");
	});
});
