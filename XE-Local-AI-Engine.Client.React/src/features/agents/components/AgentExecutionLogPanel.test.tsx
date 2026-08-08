// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string, options?: Record<string, unknown>) => {
			let text = defaultValue ?? _key;
			if (options) {
				for (const [name, value] of Object.entries(options)) {
					text = text.replace(`{{${name}}}`, String(value));
				}
			}
			return text;
		},
	}),
}));

const { hookMock } = vi.hoisted(() => ({
	hookMock: { useAgentExecutionLogs: vi.fn() },
}));

vi.mock("@/features/agents/queries/useAgentExecutionLogs", () => hookMock);

import { AgentExecutionLogPanel } from "@/features/agents/components/AgentExecutionLogPanel";
import type { AgentExecutionLog } from "@/features/agents/models/AgentExecutionLogModels";

function makeLog(overrides: Partial<AgentExecutionLog> = {}): AgentExecutionLog {
	return {
		id: "log-1",
		agentDefinitionId: "agent-1",
		conversationId: "conv-1",
		messageId: "msg-1",
		modelName: "qwen3.5:9b",
		configHash: "abc123",
		latencyMs: 1234,
		promptTokens: 100,
		completionTokens: 50,
		success: true,
		errorClass: null,
		createdAtUtc: 1748600000000,
		...overrides,
	};
}

function installJsdomEnvironmentMocks(): void {
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
}

function renderPanel(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("AgentExecutionLogPanel", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		hookMock.useAgentExecutionLogs.mockReturnValue({ data: [makeLog()], isLoading: false, error: null });
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders nothing when the capability gate is off", () => {
		renderPanel(<AgentExecutionLogPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={false} />);

		expect(screen.queryByTestId("agent-execution-log-panel-agent-1")).toBeNull();
		// The query is disabled (null agent id) when the panel is gated off.
		expect(hookMock.useAgentExecutionLogs).toHaveBeenCalledWith(null);
	});

	it("renders a metadata row with the success outcome", () => {
		renderPanel(<AgentExecutionLogPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		const row = screen.getByTestId("agent-execution-log-row-log-1");
		expect(row).toBeTruthy();
		expect(row.textContent).toContain("Success");
		expect(row.textContent).toContain("qwen3.5:9b");
		expect(row.textContent).toContain("1234 ms");
	});

	it("surfaces the error class (type name only) for a failed run", () => {
		hookMock.useAgentExecutionLogs.mockReturnValue({
			data: [makeLog({ id: "log-2", success: false, errorClass: "TimeoutException", promptTokens: null, completionTokens: null })],
			isLoading: false,
			error: null,
		});

		renderPanel(<AgentExecutionLogPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		const row = screen.getByTestId("agent-execution-log-row-log-2");
		expect(row.textContent).toContain("Failed");
		expect(row.textContent).toContain("TimeoutException");
	});

	it("shows the empty state when there are no runs", () => {
		hookMock.useAgentExecutionLogs.mockReturnValue({ data: [], isLoading: false, error: null });

		renderPanel(<AgentExecutionLogPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("agent-execution-log-empty")).toBeTruthy();
	});
});
