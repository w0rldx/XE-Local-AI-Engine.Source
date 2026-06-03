// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// AgentSelectorCard is purely presentational — it receives agentOptions as a prop and does NOT call
// useAgentDefinitions internally. Tests pass the derived list directly, mirroring Chat.tsx behaviour.
import { AgentSelectorCard } from "@/features/chat/components/AgentSelectorCard";
import type { AgentOption } from "@/features/chat/models/ChatModels";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

function makeOption(overrides: Partial<AgentOption> = {}): AgentOption {
	return {
		id: "agent-1",
		name: "Test Agent",
		description: "A test agent",
		kind: "Single",
		modelProfile: null,
		...overrides,
	};
}

describe("AgentSelectorCard", () => {
	beforeEach(() => {
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

	it("disables the trigger when there are no agents", () => {
		renderWithProviders(<AgentSelectorCard agentOptions={[]} selectedAgentId="" onAgentChange={vi.fn()} />);

		// No jest-dom in this project — check the aria-disabled attribute directly.
		const trigger = screen.getByTestId("chat-agent-selector-trigger");
		expect(trigger.getAttribute("disabled") !== null || trigger.getAttribute("aria-disabled") === "true").toBe(true);
	});

	it("lists agents in the dropdown when opened", async () => {
		const options = [makeOption({ id: "agent-1", name: "Alpha Agent" }), makeOption({ id: "agent-2", name: "Beta Agent" })];

		renderWithProviders(<AgentSelectorCard agentOptions={options} selectedAgentId="" onAgentChange={vi.fn()} />);

		fireEvent.click(screen.getByTestId("chat-agent-selector-trigger"));

		expect(await screen.findByTestId("chat-agent-selector-option-agent-1")).toBeTruthy();
		expect(screen.getByTestId("chat-agent-selector-option-agent-2")).toBeTruthy();
	});

	it("calls onAgentChange with the selected agent id on click", async () => {
		const onAgentChange = vi.fn();
		const options = [makeOption({ id: "agent-1", name: "Alpha Agent" })];

		renderWithProviders(<AgentSelectorCard agentOptions={options} selectedAgentId="" onAgentChange={onAgentChange} />);

		fireEvent.click(screen.getByTestId("chat-agent-selector-trigger"));
		fireEvent.click(await screen.findByTestId("chat-agent-selector-option-agent-1"));

		expect(onAgentChange).toHaveBeenCalledWith("agent-1");
	});

	it("shows the Orchestrator badge for orchestrator-kind agents", async () => {
		const options = [makeOption({ id: "orch-1", name: "Orch Agent", kind: "Orchestrator" })];

		renderWithProviders(<AgentSelectorCard agentOptions={options} selectedAgentId="" onAgentChange={vi.fn()} />);

		fireEvent.click(screen.getByTestId("chat-agent-selector-trigger"));

		// Badge text comes from i18n fallback string: "Orchestrator"
		expect(await screen.findByText("Orchestrator")).toBeTruthy();
	});

	it("renders a pinned-model hint element for agents with a modelProfile", async () => {
		const options = [makeOption({ id: "agent-1", name: "Pinned Agent", modelProfile: "llama3:8b" })];

		renderWithProviders(<AgentSelectorCard agentOptions={options} selectedAgentId="" onAgentChange={vi.fn()} />);

		fireEvent.click(screen.getByTestId("chat-agent-selector-trigger"));

		const option = await screen.findByTestId("chat-agent-selector-option-agent-1");
		// The i18n fallback renders "Uses model: {{model}}" in tests without a provider; assert the hint key is present.
		expect(option.textContent).toContain("Uses model");
	});

	it("treats a stale selectedAgentId (deleted agent) as unselected, showing the placeholder", () => {
		const options = [makeOption({ id: "agent-1", name: "Live Agent" })];

		renderWithProviders(<AgentSelectorCard agentOptions={options} selectedAgentId="deleted-agent-id" onAgentChange={vi.fn()} />);

		// Trigger label shows the placeholder when the persisted id maps to no live agent.
		const trigger = screen.getByTestId("chat-agent-selector-trigger");
		expect(trigger.textContent).toContain("Select agent");
	});
});
