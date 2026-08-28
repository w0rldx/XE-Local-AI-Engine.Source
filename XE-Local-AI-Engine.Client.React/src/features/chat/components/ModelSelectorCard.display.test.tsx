// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { sourceThemeConfiguration } from "@/core/theme/config/ThemeConfiguration";
import { ModelSelectorCard } from "@/features/chat/components/ModelSelectorCard";
import type { ModelOption } from "@/features/chat/models/ChatModels";
import { localDefaultModelValue } from "@/features/chat/models/NodeChatModelSelection";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The trigger is the only place an operator can read which model the next turn goes to, and it is the narrowest
// control in the composer. These cover the two-line layout, the tooltip that carries what the clamp cut off, and the
// compact branch below the theme's `sm` — derived from the theme rather than hardcoded so the assertions follow
// theme.json if it ever retunes, the same way LayoutBreakpoints.test.ts cross-checks the constant itself.
const { sm } = sourceThemeConfiguration.breakpoints.values;

function setViewportWidth(width: number): void {
	Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: width });
}

function localDefaultOption(): ModelOption {
	return { value: localDefaultModelValue, label: "Local default", displayName: "Local runtime default", isAvailable: true };
}

function ggufOption(): ModelOption {
	return {
		value: "unsloth/Qwen3-8B-Instruct-GGUF:Q4_K_M",
		label: "unsloth/Qwen3-8B-Instruct-GGUF:Q4_K_M",
		isAvailable: true,
		provider: "llamacpp",
		statusLabel: "8B · Q4_K_M",
	};
}

function renderPicker(selectedModel: string, options: ModelOption[] = [localDefaultOption(), ggufOption()]) {
	return renderWithProviders(<ModelSelectorCard modelOptions={options} selectedModel={selectedModel} onModelChange={vi.fn()} />);
}

describe("ModelSelectorCard trigger", () => {
	afterEach(() => {
		cleanup();
		// jsdom's default, and the width the rest of the suite means by "desktop".
		setViewportWidth(1024);
	});

	it("names the model on line one and what it weighs on line two", () => {
		renderPicker("unsloth/Qwen3-8B-Instruct-GGUF:Q4_K_M");

		// The publisher prefix, the -GGUF marker and the quant tag are gone from the name — clamped to one line, they
		// are exactly the characters that used to survive while the model itself was cut off.
		expect(screen.getByTestId("chat-model-selector-trigger-name").textContent).toBe("Qwen3-8B-Instruct");
		expect(screen.getByTestId("chat-model-selector-trigger-detail").textContent).toBe("8B · Q4_K_M");
	});

	it("offers the untruncated identity as a tooltip on the trigger", async () => {
		renderPicker("unsloth/Qwen3-8B-Instruct-GGUF:Q4_K_M");

		// The BUTTON, not the paper around it: the tooltip wraps the button so the paper can stay Popover.Target's
		// direct child (the target clones its child to attach the popover anchor ref, and a tooltip in between
		// swallowed it — the dropdown then rendered against the viewport's top-left corner).
		fireEvent.mouseEnter(screen.getByTestId("chat-model-selector-trigger"));

		expect(await screen.findByText("unsloth/Qwen3-8B-Instruct-GGUF:Q4_K_M")).toBeTruthy();
	});

	it("still opens the picker when the trigger is clicked, tooltip and all", async () => {
		renderPicker("unsloth/Qwen3-8B-Instruct-GGUF:Q4_K_M");

		fireEvent.click(screen.getByTestId("chat-model-selector-trigger"));

		expect(await screen.findByTestId("chat-model-selector-option-unsloth/Qwen3-8B-Instruct-GGUF:Q4_K_M")).toBeTruthy();
	});

	it("shows no second line for a selection that has nothing to say on one", () => {
		renderPicker(localDefaultModelValue);

		expect(screen.getByTestId("chat-model-selector-trigger-name").textContent).toBe("Local runtime default");
		expect(screen.queryByTestId("chat-model-selector-trigger-detail")).toBeNull();
	});

	it("names the serving connection on line two for an external model", () => {
		const external: ModelOption = {
			value: "ext:workstation/qwen3-coder-30b",
			label: "ext:workstation/qwen3-coder-30b",
			isAvailable: true,
			provider: "external",
			externalConnectionId: "workstation",
			externalConnectionName: "Workstation",
			declaredLocality: "local",
		};

		renderWithProviders(
			<ModelSelectorCard
				modelOptions={[localDefaultOption()]}
				cloudModelOptions={[external]}
				selectedModel="ext:workstation/qwen3-coder-30b"
				onModelChange={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("chat-model-selector-trigger-name").textContent).toBe("qwen3-coder-30b");
		expect(screen.getByTestId("chat-model-selector-trigger-detail").textContent).toBe("Workstation");
	});
});

describe("ModelSelectorCard trigger below the theme sm breakpoint", () => {
	afterEach(() => {
		cleanup();
		setViewportWidth(1024);
	});

	it("keeps the model name — the control must never go back to a nameless icon", () => {
		setViewportWidth(sm - 1);
		renderPicker("unsloth/Qwen3-8B-Instruct-GGUF:Q4_K_M");

		expect(screen.getByTestId("chat-model-selector-trigger-name").textContent).toBe("Qwen3-8B-Instruct");
	});

	it("drops the second line, which is what buys the width back", () => {
		setViewportWidth(sm - 1);
		renderPicker("unsloth/Qwen3-8B-Instruct-GGUF:Q4_K_M");

		expect(screen.queryByTestId("chat-model-selector-trigger-detail")).toBeNull();
	});

	it("keeps both lines at exactly the breakpoint", () => {
		setViewportWidth(sm);
		renderPicker("unsloth/Qwen3-8B-Instruct-GGUF:Q4_K_M");

		expect(screen.getByTestId("chat-model-selector-trigger-detail").textContent).toBe("8B · Q4_K_M");
	});
});

describe("ModelSelectorCard option rows", () => {
	afterEach(() => {
		cleanup();
	});

	it("shortens the row's name and moves the raw id into its tooltip", async () => {
		renderPicker(localDefaultModelValue);

		fireEvent.click(screen.getByTestId("chat-model-selector-trigger"));
		const row = await screen.findByTestId("chat-model-selector-option-unsloth/Qwen3-8B-Instruct-GGUF:Q4_K_M");

		expect(row.textContent).toContain("Qwen3-8B-Instruct");
		expect(row.textContent).toContain("8B · Q4_K_M");
		// The raw id used to be rendered as a third, dimmed fragment inside the row, where it was clamped away anyway.
		expect(row.textContent).not.toContain("unsloth/");

		// The tooltip hosts the NAME element, not the whole row: the row is the click target that selects the model.
		fireEvent.mouseEnter(screen.getByText("Qwen3-8B-Instruct"));
		expect(await screen.findByText("unsloth/Qwen3-8B-Instruct-GGUF:Q4_K_M")).toBeTruthy();
	});
});
