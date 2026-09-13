// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { ChatInputArea } from "@/features/chat/components/ChatInputArea";
import type { ChatCommandOption } from "@/features/chat/models/SlashCommandModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

const commandOptions: ChatCommandOption[] = [
	{ id: null, name: "ping", description: "Test the current chat agent.", prompt: "Respond with exactly PONG" },
	{ id: "review-id", name: "review", description: "Review current work", prompt: "Review the current work" },
];

function renderComposer(overrides: Record<string, unknown> = {}) {
	const onSend = vi.fn();
	renderWithProviders(
		<ChatInputArea
			availableReasoningEfforts={["none", "medium"]}
			isSending={false}
			modelOptions={[]}
			selectedModel="local-default"
			reasoningEffort="medium"
			commandOptions={commandOptions}
			onCancel={vi.fn()}
			onModelChange={vi.fn()}
			onReasoningEffortChange={vi.fn()}
			onSend={onSend}
			{...overrides}
		/>,
	);
	return { input: screen.getByTestId<HTMLTextAreaElement>("chat-input"), onSend };
}

describe("ChatInputArea slash commands", () => {
	afterEach(cleanup);

	it("uses Enter first to select and a second Enter to expand and send exactly once", () => {
		const { input, onSend } = renderComposer();
		fireEvent.change(input, { target: { value: "/pi", selectionStart: 3, selectionEnd: 3 } });
		expect(input.getAttribute("aria-activedescendant")).toBe(screen.getByTestId("slash-command-option-ping").id);
		fireEvent.keyDown(input, { key: "Enter", code: "Enter" });
		expect(input.value).toBe("/ping");
		expect(onSend).not.toHaveBeenCalled();
		fireEvent.keyDown(input, { key: "Enter", code: "Enter" });
		expect(onSend).toHaveBeenCalledTimes(1);
		expect(onSend).toHaveBeenCalledWith("Respond with exactly PONG", "medium", "local-default");
	});

	it("supports mouse selection without sending", () => {
		const { input, onSend } = renderComposer();
		fireEvent.change(input, { target: { value: "/rev", selectionStart: 4, selectionEnd: 4 } });
		fireEvent.click(screen.getByTestId("slash-command-option-review"));
		expect(input.value).toBe("/review");
		expect(onSend).not.toHaveBeenCalled();
	});

	it("uses Mantine keyboard selection for ArrowDown and exposes the active option to assistive technology", () => {
		const { input, onSend } = renderComposer();
		fireEvent.change(input, { target: { value: "/", selectionStart: 1, selectionEnd: 1 } });
		fireEvent.keyDown(input, { key: "ArrowDown", code: "ArrowDown" });
		expect(input.getAttribute("aria-activedescendant")).toBe(screen.getByTestId("slash-command-option-review").id);
		fireEvent.keyDown(input, { key: "Enter", code: "Enter" });
		expect(input.value).toBe("/review");
		expect(onSend).not.toHaveBeenCalled();
	});

	it("wraps ArrowUp from the first option to the last command before selection", () => {
		const { input } = renderComposer();
		fireEvent.change(input, { target: { value: "/", selectionStart: 1, selectionEnd: 1 } });
		fireEvent.keyDown(input, { key: "ArrowUp", code: "ArrowUp" });
		expect(input.getAttribute("aria-activedescendant")).toBe(screen.getByTestId("slash-command-option-review").id);
		fireEvent.keyDown(input, { key: "Enter", code: "Enter" });
		expect(input.value).toBe("/review");
	});

	it("dismisses unchanged suggestions with Escape and preserves ordinary-chat fallback", () => {
		const { input, onSend } = renderComposer();
		fireEvent.change(input, { target: { value: "/pi", selectionStart: 3, selectionEnd: 3 } });
		fireEvent.keyDown(input, { key: "Escape" });
		expect(screen.getByTestId("slash-command-menu").style.display).toBe("none");
		fireEvent.keyDown(input, { key: "Enter", code: "Enter" });
		expect(onSend).toHaveBeenCalledWith("/pi", "medium", "local-default");
	});

	it("does not activate in mid-text and sends unknown slash text unchanged", () => {
		const { input, onSend } = renderComposer();
		fireEvent.change(input, { target: { value: "say /ping", selectionStart: 9, selectionEnd: 9 } });
		expect(screen.getByTestId("slash-command-menu").style.display).toBe("none");
		fireEvent.change(input, { target: { value: "/missing", selectionStart: 8, selectionEnd: 8 } });
		fireEvent.keyDown(input, { key: "Enter" });
		expect(onSend).toHaveBeenCalledWith("/missing", "medium", "local-default");
	});

	it("keeps Shift+Enter and IME composition from selecting or sending", () => {
		const { input, onSend } = renderComposer();
		fireEvent.change(input, { target: { value: "/pi", selectionStart: 3, selectionEnd: 3 } });
		fireEvent.keyDown(input, { key: "Enter", code: "Enter", shiftKey: true });
		expect(input.value).toBe("/pi");
		expect(onSend).not.toHaveBeenCalled();
		fireEvent.compositionStart(input);
		fireEvent.keyDown(input, { key: "Enter", code: "Enter", isComposing: true });
		expect(onSend).not.toHaveBeenCalled();
		expect(input.value).toBe("/pi");
	});

	it("does not show autocomplete while disabled or sending", () => {
		const disabled = renderComposer({ disabled: true });
		fireEvent.change(disabled.input, { target: { value: "/", selectionStart: 1, selectionEnd: 1 } });
		expect(screen.getByTestId("slash-command-menu").style.display).toBe("none");
		cleanup();
		const sending = renderComposer({ isSending: true });
		fireEvent.change(sending.input, { target: { value: "/", selectionStart: 1, selectionEnd: 1 } });
		expect(screen.getByTestId("slash-command-menu").style.display).toBe("none");
	});
});
