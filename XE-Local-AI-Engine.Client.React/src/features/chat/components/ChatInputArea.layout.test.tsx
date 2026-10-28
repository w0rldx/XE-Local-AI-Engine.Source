// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { AgentOption } from "@/core/agents/models/AgentOption";
import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { ChatInputArea } from "@/features/chat/components/ChatInputArea";
import classes from "@/features/chat/components/ChatInputArea/ChatComposerToolbar.module.css";
import { WorkflowSelectorCard } from "@/features/chat/workflow/WorkflowSelectorCard";
import { installJsdomEnvironmentMocks, testMantineTheme } from "@/test/MantineTestRender";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The composer toolbar is ONE row at every width: which pickers fold is fitComposerToolbar's call (unit-tested), so these
// tests pin the row itself — a non-wrapping row that holds every control,
// the context readout included — and leave the visual fold to the browser check.

const longAgent: AgentOption = {
	id: "agent-long",
	name: "Research assistant for very long quarterly financial reporting and compliance reviews",
	description: "Drafts a compliance summary",
	kind: "Single",
	modelProfile: null,
	playbookEnabled: false,
};

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

describe("ChatInputArea one-row toolbar", () => {
	const originalWidth = window.innerWidth;

	afterEach(() => {
		Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: originalWidth });
		cleanup();
	});

	it.each([1280, 520])("keeps the pickers, the context readout and Send on one non-wrapping row at %ipx", (width) => {
		Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: width });
		// The compact button asks before compacting, through the app's confirm dialog.
		renderWithProviders(
			<ConfirmProvider>
				<ChatInputArea
					{...baseProps()}
					contextUsage={{ usedTokens: 1100, isAuthoritative: true, modelLabel: "Model A", nodeLabel: "Local" }}
					agentControlsAvailable={true}
					agentOptions={[longAgent]}
					agentModeEnabled={true}
					selectedAgentId="agent-long"
					onSelectAgent={vi.fn()}
					workflow={{
						selector: <WorkflowSelectorCard options={[]} selectedDefinitionId="" onSelect={vi.fn()} />,
					}}
				/>
			</ConfirmProvider>,
		);

		const send = screen.getByTestId("chat-send-button");
		const toolbar = send.closest(`.${classes["toolbar"]}`) as HTMLElement;
		expect(toolbar).not.toBeNull();
		expect(toolbar.style.getPropertyValue("--group-wrap")).toBe("nowrap");
		const row = within(toolbar);
		expect(row.getByTestId("chat-model-selector-trigger")).toBeTruthy();
		expect(row.getByRole("button", { name: `Agent: ${longAgent.name}` })).toBeTruthy();
		expect(row.getByRole("button", { name: "Workflow: No workflow" })).toBeTruthy();
		expect(row.getByTestId("context-usage-trigger")).toBeTruthy();
		expect(row.getByTestId("compact-conversation-button")).toBeTruthy();
		for (const group of toolbar.querySelectorAll<HTMLElement>(".mantine-Group-root")) {
			expect(group.style.getPropertyValue("--group-wrap")).not.toBe("wrap");
		}
	});

	it("opens the full agent menu from the trigger, long name and all", async () => {
		renderWithProviders(
			<ChatInputArea
				{...baseProps()}
				agentControlsAvailable={true}
				agentOptions={[longAgent]}
				agentModeEnabled={false}
				selectedAgentId=""
				onSelectAgent={vi.fn()}
			/>,
		);

		fireEvent.click(screen.getByRole("button", { name: "Agent: Default Assistant" }));

		expect((await screen.findByTestId(`chat-agent-selector-option-${longAgent.id}`)).textContent).toContain(longAgent.name);
	});
});

// The composer's own width picks the layout, not the viewport's: a desktop window with both side panes open leaves a
// composer as narrow as a phone's.
describe("ChatInputArea composer-width fit", () => {
	const originalWidth = window.innerWidth;
	const originalHeight = window.innerHeight;

	function renderComposerWithWidth(composerWidth: number) {
		installJsdomEnvironmentMocks();
		// Reports `composerWidth` for the observed composer, as a browser would after layout.
		class ResizeObserverStub {
			private readonly callback: ResizeObserverCallback;

			constructor(callback: ResizeObserverCallback) {
				this.callback = callback;
			}

			observe(): void {
				this.callback(
					[{ contentRect: { width: composerWidth, height: 120 } } as unknown as ResizeObserverEntry],
					this as unknown as ResizeObserver,
				);
			}

			unobserve = vi.fn();

			disconnect = vi.fn();
		}
		Object.defineProperty(window, "ResizeObserver", { writable: true, value: ResizeObserverStub });
		Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: 1024 });
		Object.defineProperty(window, "innerHeight", { writable: true, configurable: true, value: 768 });
		render(
			<MantineProvider env="test" theme={testMantineTheme}>
				<ChatInputArea {...baseProps()} agentControlsAvailable={true} agentOptions={[longAgent]} onSelectAgent={vi.fn()} />
			</MantineProvider>,
		);
	}

	afterEach(() => {
		Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: originalWidth });
		Object.defineProperty(window, "innerHeight", { writable: true, configurable: true, value: originalHeight });
		cleanup();
	});

	it("folds the toggles into the overflow menu when the composer is narrow on a desktop viewport", async () => {
		renderComposerWithWidth(250);

		expect(await screen.findByTestId("chat-composer-overflow-trigger")).toBeTruthy();
		expect(screen.queryByTestId("chat-reasoning-effort-menu-trigger")).toBeNull();
		expect(screen.getByRole("button", { name: "Send" }).textContent).not.toContain("Send");
		// The input keeps its desktop height: only a phone viewport starts it at one row.
		expect(screen.getByTestId("chat-input").getAttribute("rows")).toBe("2");
	});

	// Folding costs a click per toggle, so Send's label goes first.
	it("keeps the toggles inline by giving up Send's label first", async () => {
		renderComposerWithWidth(300);

		expect(await screen.findByTestId("chat-reasoning-effort-menu-trigger")).toBeTruthy();
		expect(screen.queryByTestId("chat-composer-overflow-trigger")).toBeNull();
		await vi.waitFor(() => expect(screen.getByRole("button", { name: "Send" }).textContent).not.toContain("Send"));
	});

	it("keeps the full row when the composer has room for it", async () => {
		renderComposerWithWidth(900);

		expect(await screen.findByTestId("chat-reasoning-effort-menu-trigger")).toBeTruthy();
		expect(screen.queryByTestId("chat-composer-overflow-trigger")).toBeNull();
	});
});
