// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
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

const { hooksMock, confirmMock } = vi.hoisted(() => ({
	hooksMock: {
		usePlaybookActions: vi.fn(),
		useCreatePlaybookAction: vi.fn(),
		useUpdatePlaybookAction: vi.fn(),
		useDeletePlaybookAction: vi.fn(),
	},
	confirmMock: vi.fn(),
}));

vi.mock("@/features/agents/queries/usePlaybookActions", () => hooksMock);
vi.mock("@/core/ui/hooks/useConfirm", () => ({
	useConfirm: () => ({ confirm: confirmMock }),
}));

import { PlaybookPanel } from "@/features/agents/components/PlaybookPanel";
import type { PlaybookAction } from "@/features/agents/models/PlaybookActionModels";

function makeAction(overrides: Partial<PlaybookAction> = {}): PlaybookAction {
	return {
		id: "action-1",
		agentDefinitionId: "agent-1",
		state: "Enabled",
		source: "Manual",
		triggerCondition: null,
		behavior: "Always cite your sources",
		scope: null,
		priority: 0,
		version: 1,
		createdAtUtc: 1000,
		updatedAtUtc: 2000,
		...overrides,
	};
}

function makeMutation() {
	return { mutate: vi.fn(), isPending: false, error: null };
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
	Object.defineProperty(document, "fonts", {
		writable: true,
		value: { ready: Promise.resolve(), addEventListener: vi.fn(), removeEventListener: vi.fn() },
	});
}

function renderPanel(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("PlaybookPanel", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		hooksMock.usePlaybookActions.mockReturnValue({ data: [makeAction()], isLoading: false, error: null });
		hooksMock.useCreatePlaybookAction.mockReturnValue(makeMutation());
		hooksMock.useUpdatePlaybookAction.mockReturnValue(makeMutation());
		hooksMock.useDeletePlaybookAction.mockReturnValue(makeMutation());
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders nothing when the capability gate is off", () => {
		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={false} />);

		// No panel surface is rendered (the component returns null behind the gate).
		expect(screen.queryByTestId("playbook-panel-agent-1")).toBeNull();
		expect(screen.queryByTestId("playbook-add-button")).toBeNull();
		// The query is disabled (null agent id) when the panel is gated off.
		expect(hooksMock.usePlaybookActions).toHaveBeenCalledWith(null);
	});

	it("lists actions and renders the Manual provenance badge", () => {
		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("playbook-action-action-1")).toBeTruthy();
		expect(screen.getByTestId("playbook-source-action-1").textContent).toBe("Manual");
		expect(screen.getByText("Always cite your sources")).toBeTruthy();
	});

	it("shows the empty state when there are no actions", () => {
		hooksMock.usePlaybookActions.mockReturnValue({ data: [], isLoading: false, error: null });

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("playbook-empty")).toBeTruthy();
	});

	it("toggles an action's state through the update mutation without dropping its fields", () => {
		const updateMutation = makeMutation();
		hooksMock.useUpdatePlaybookAction.mockReturnValue(updateMutation);

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("playbook-toggle-action-1"));

		expect(updateMutation.mutate).toHaveBeenCalledWith(
			expect.objectContaining({
				actionId: "action-1",
				request: expect.objectContaining({ state: "Disabled", behavior: "Always cite your sources" }),
			}),
		);
	});

	it("opens the add editor and creates an action", () => {
		const createMutation = makeMutation();
		hooksMock.useCreatePlaybookAction.mockReturnValue(createMutation);
		hooksMock.usePlaybookActions.mockReturnValue({ data: [], isLoading: false, error: null });

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("playbook-add-button"));
		const behavior = screen.getByTestId("playbook-form-behavior") as HTMLTextAreaElement;
		fireEvent.change(behavior, { target: { value: "Be concise" } });
		fireEvent.click(screen.getByTestId("playbook-form-submit"));

		expect(createMutation.mutate).toHaveBeenCalledWith(
			expect.objectContaining({ behavior: "Be concise", state: "Enabled" }),
			expect.anything(),
		);
	});

	it("blocks create submit when behavior is empty", () => {
		const createMutation = makeMutation();
		hooksMock.useCreatePlaybookAction.mockReturnValue(createMutation);

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("playbook-add-button"));
		fireEvent.click(screen.getByTestId("playbook-form-submit"));

		expect(createMutation.mutate).not.toHaveBeenCalled();
		expect(screen.getByText("Behavior is required")).toBeTruthy();
	});

	it("surfaces a load error", () => {
		hooksMock.usePlaybookActions.mockReturnValue({ data: undefined, isLoading: false, error: new Error("boom") });

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("playbook-list-error")).toBeTruthy();
	});
});
