// @vitest-environment jsdom

import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { ChatInputArea } from "@/features/chat/components/ChatInputArea";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

function baseProps() {
	return {
		availableReasoningEfforts: ["none" as const],
		modelOptions: [{ value: "model-a", label: "Model A" }],
		selectedModel: "model-a",
		reasoningEffort: "none" as const,
		onCancel: vi.fn(),
		onModelChange: vi.fn(),
		onReasoningEffortChange: vi.fn(),
		onSend: vi.fn(),
	};
}

type Props = ReturnType<typeof baseProps>;

function Harness({ props, isSending, disabled = false }: { props: Props; isSending: boolean; disabled?: boolean }) {
	return (
		<>
			<ChatInputArea {...props} isSending={isSending} disabled={disabled} />
			<button type="button">elsewhere</button>
		</>
	);
}

function composer(): HTMLTextAreaElement {
	return screen.getByRole("textbox") as HTMLTextAreaElement;
}

// The composer stays focusable while a reply streams: disabling the focused textarea would drop focus to the page body.
describe("ChatInputArea focus across a send", () => {
	afterEach(() => {
		cleanup();
	});

	it("keeps focus in the composer through the stream and refuses a second send while it runs", () => {
		const props = baseProps();
		const view = render(<Harness props={props} isSending={false} />, createProvidersWrapper());
		composer().focus();
		fireEvent.change(composer(), { target: { value: "hello" } });
		fireEvent.keyDown(composer(), { key: "Enter" });
		expect(props.onSend).toHaveBeenCalledTimes(1);

		view.rerender(<Harness props={props} isSending={true} />);

		expect(composer().disabled).toBe(false);
		expect(composer().readOnly).toBe(true);
		expect(document.activeElement).toBe(composer());
		fireEvent.change(composer(), { target: { value: "again" } });
		fireEvent.keyDown(composer(), { key: "Enter" });
		expect(props.onSend).toHaveBeenCalledTimes(1);

		view.rerender(<Harness props={props} isSending={false} />);

		expect(composer().readOnly).toBe(false);
		expect(document.activeElement).toBe(composer());
	});

	it("leaves focus where the user moved it when the stream ends", () => {
		const props = baseProps();
		const view = render(<Harness props={props} isSending={true} />, createProvidersWrapper());
		const elsewhere = screen.getByRole("button", { name: "elsewhere" });
		elsewhere.focus();

		view.rerender(<Harness props={props} isSending={false} />);

		expect(document.activeElement).toBe(elsewhere);
	});

	it("keeps a genuinely disabled composer disabled, sending or not", () => {
		const props = baseProps();
		const view = render(<Harness props={props} isSending={false} disabled={true} />, createProvidersWrapper());

		expect(composer().disabled).toBe(true);

		view.rerender(<Harness props={props} isSending={true} disabled={true} />);

		expect(composer().disabled).toBe(true);
	});
});
