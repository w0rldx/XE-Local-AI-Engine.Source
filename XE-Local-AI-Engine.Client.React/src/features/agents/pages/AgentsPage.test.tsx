// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { AgentDefinition } from "@/features/agents/models/AgentDefinitionModels";
import { useAgentManagementStore } from "@/features/agents/stores/AgentManagementStore";

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

const { hooksMock, playbookHooksMock, confirmMock } = vi.hoisted(() => ({
	hooksMock: {
		useAgentDefinitions: vi.fn(),
		useToolCapableModels: vi.fn(),
		useCreateAgentDefinition: vi.fn(),
		useUpdateAgentDefinition: vi.fn(),
		useDeleteAgentDefinition: vi.fn(),
	},
	// The edit editor mounts PlaybookPanel; mock its query hooks so opening the editor never fires a real request.
	playbookHooksMock: {
		usePlaybookActions: vi.fn(),
		useCreatePlaybookAction: vi.fn(),
		useUpdatePlaybookAction: vi.fn(),
		useUpdateSuggestedAction: vi.fn(),
		useDeletePlaybookAction: vi.fn(),
		useAnalyzePlaybook: vi.fn(),
		usePromoteSuggestedAction: vi.fn(),
		useRejectSuggestedAction: vi.fn(),
	},
	confirmMock: vi.fn(),
}));

vi.mock("@/features/agents/queries/useAgentDefinitions", () => hooksMock);
vi.mock("@/features/agents/queries/usePlaybookActions", () => playbookHooksMock);
vi.mock("@/core/ui/hooks/useConfirm", () => ({
	useConfirm: () => ({ confirm: confirmMock }),
}));
vi.mock("@/features/models/api/LocalModelsApi", () => ({
	listLocalModels: vi.fn().mockResolvedValue({ items: [] }),
}));
// The agent form's tool selector fetches the catalog via useToolCatalog (loop P4). Mock it so opening the
// editor never issues a real request and renders deterministically.
vi.mock("@/features/tools/queries/useToolCatalog", () => ({
	useToolCatalog: () => ({ data: [], isLoading: false, error: null }),
}));

import { AgentsPage } from "@/features/agents/pages/AgentsPage";

const sampleDefinition: AgentDefinition = {
	id: "agent-1",
	name: "Research assistant",
	description: "Helps with research",
	instructions: "You are a research assistant.",
	modelProfile: "qwen3:8b",
	reasoningEffort: "medium",
	kind: "Single",
	allowedToolNames: ["GetCurrentTime"],
	toolApprovals: { GetCurrentTime: true },
	orchestrationTopologyJson: null,
	playbookEnabled: false,
	version: 1,
	createdAtUtc: 1000,
	updatedAtUtc: 2000,
};

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

function renderPage() {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	return render(
		<MantineProvider>
			<QueryClientProvider client={queryClient}>
				<AgentsPage />
			</QueryClientProvider>
		</MantineProvider>,
	);
}

describe("AgentsPage", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		useAgentManagementStore.setState({ editorTarget: null });
		hooksMock.useAgentDefinitions.mockReturnValue({ data: [sampleDefinition], isLoading: false, error: null });
		hooksMock.useToolCapableModels.mockReturnValue({ data: ["qwen3:8b"] });
		hooksMock.useCreateAgentDefinition.mockReturnValue(makeMutation());
		hooksMock.useUpdateAgentDefinition.mockReturnValue(makeMutation());
		hooksMock.useDeleteAgentDefinition.mockReturnValue(makeMutation());
		playbookHooksMock.usePlaybookActions.mockReturnValue({ data: [], isLoading: false, error: null });
		playbookHooksMock.useCreatePlaybookAction.mockReturnValue(makeMutation());
		playbookHooksMock.useUpdatePlaybookAction.mockReturnValue(makeMutation());
		playbookHooksMock.useUpdateSuggestedAction.mockReturnValue(makeMutation());
		playbookHooksMock.useDeletePlaybookAction.mockReturnValue(makeMutation());
		playbookHooksMock.useAnalyzePlaybook.mockReturnValue(makeMutation());
		playbookHooksMock.usePromoteSuggestedAction.mockReturnValue(makeMutation());
		playbookHooksMock.useRejectSuggestedAction.mockReturnValue(makeMutation());
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders the list of agent definitions", () => {
		renderPage();

		expect(screen.getByTestId("agent-definitions-table")).toBeTruthy();
		expect(screen.getByTestId("agent-definition-row-agent-1")).toBeTruthy();
		expect(screen.getByText("Research assistant")).toBeTruthy();
	});

	it("shows the empty state when there are no definitions", () => {
		hooksMock.useAgentDefinitions.mockReturnValue({ data: [], isLoading: false, error: null });

		renderPage();

		expect(screen.getByTestId("agent-definitions-empty")).toBeTruthy();
	});

	it("opens the create editor from the new-agent button", () => {
		renderPage();

		fireEvent.click(screen.getByTestId("agent-create-button"));

		expect(screen.getByTestId("agent-editor-card")).toBeTruthy();
		expect(screen.getByTestId("agent-definition-form")).toBeTruthy();
	});

	it("opens the edit editor pre-filled from a row action", () => {
		renderPage();

		fireEvent.click(screen.getByTestId("agent-definition-edit-agent-1"));

		const nameInput = screen.getByTestId("agent-form-name") as HTMLInputElement;
		expect(nameInput.value).toBe("Research assistant");
	});

	it("surfaces a load error", () => {
		hooksMock.useAgentDefinitions.mockReturnValue({
			data: undefined,
			isLoading: false,
			error: new Error("boom"),
		});

		renderPage();

		expect(screen.getByTestId("agent-list-error")).toBeTruthy();
	});
});
