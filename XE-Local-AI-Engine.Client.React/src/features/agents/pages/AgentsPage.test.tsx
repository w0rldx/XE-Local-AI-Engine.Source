// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
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
		useRunEval: vi.fn(),
	},
	confirmMock: vi.fn(),
}));

vi.mock("@/features/agents/queries/useAgentDefinitions", () => hooksMock);
vi.mock("@/features/agents/queries/usePlaybookActions", () => playbookHooksMock);
vi.mock("@/core/ui/hooks/useConfirm", () => ({
	useConfirm: () => ({ confirm: confirmMock }),
}));
// The page now arms useUnsavedChangesGuard, which calls TanStack Router's useBlocker — that requires a Router
// context the page tests don't provide. Override only useBlocker (idle state) while keeping every other export real
// (the dependency graph pulls in route helpers like createRootRouteWithContext). The guard's behavior is covered by
// useUnsavedChangesGuard.test.tsx.
vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useBlocker: () => ({ status: "idle", proceed: undefined, reset: undefined }),
}));
// AgentsPage repointed its model dropdown onto the generated SDK (listLocalModelsOptions). Partially mock the
// generated TanStack module so the page's useQuery(withResponseValidation(listLocalModelsOptions())) resolves an
// empty list without a real request, while every other generated options/mutation export stays real — the editor
// mounts the playbook/feedback/golden panels, whose hooks reach other exports from this same module. The real
// withResponseValidation bridge still wraps the mocked queryFn.
vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	listLocalModelsOptions: vi.fn(() => ({
		// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
		queryKey: [{ _id: "listLocalModels" }],
		queryFn: async () => ({ items: [] }),
	})),
}));
// The agent form's tool selector fetches the catalog via useToolCatalog (dynamic tool-catalog). Mock it so opening the
// editor never issues a real request and renders deterministically.
// The agent form's skill selector fetches the node skill library via useSkills. Mock it so opening the editor in
// these tests is deterministic and never hits the network (the skill rows aren't asserted here).
vi.mock("@/features/skills/queries/useSkills", () => ({
	useSkills: () => ({ data: [], isLoading: false, error: null }),
}));

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
	allowedSkillIds: [],
	orchestrationTopologyJson: null,
	playbookEnabled: false,
	defaultTemporaryChat: false,
	memoryExtractionEnabled: true,
	disableBaseScaffold: false,
	disableToolRelevanceFilter: false,
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
		playbookHooksMock.useRunEval.mockReturnValue(makeMutation());
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

	it("opens the create editor as a dialog with always-visible Save/Cancel from the new-agent button", async () => {
		renderPage();

		// Editor is closed initially — no dialog, list shown.
		expect(screen.queryByRole("dialog")).toBeNull();

		fireEvent.click(screen.getByTestId("agent-create-button"));

		// Editor now lives inside a dialog; the form and the sticky-footer Save/Cancel are present.
		const dialog = await screen.findByRole("dialog");
		const within = (testId: string) => dialog.querySelector(`[data-testid="${testId}"]`);
		expect(within("agent-editor-card")).toBeTruthy();
		expect(within("agent-definition-form")).toBeTruthy();
		expect(screen.getByTestId("agent-form-submit")).toBeTruthy();
		expect(screen.getByTestId("agent-form-cancel")).toBeTruthy();
		// The list stays mounted underneath (no page-takeover).
		expect(screen.getByTestId("agent-definitions-table")).toBeTruthy();
	});

	it("opens the edit editor pre-filled from a row action", async () => {
		renderPage();

		fireEvent.click(screen.getByTestId("agent-definition-edit-agent-1"));

		const dialog = await screen.findByRole("dialog");
		const nameInput = dialog.querySelector('[data-testid="agent-form-name"]') as HTMLInputElement;
		expect(nameInput.value).toBe("Research assistant");
	});

	it("prompts to discard when Cancel is clicked with unsaved edits and stays open if the user keeps editing", async () => {
		// User chooses "Keep editing" → confirm resolves false → editor must NOT close.
		confirmMock.mockResolvedValue(false);
		renderPage();

		fireEvent.click(screen.getByTestId("agent-create-button"));
		const dialog = await screen.findByRole("dialog");

		// Make the form dirty (reports up via onDirtyChange → page isEditorDirty).
		const instructions = dialog.querySelector('[data-testid="agent-form-instructions-textarea"]') as HTMLTextAreaElement;
		fireEvent.change(instructions, { target: { value: "Some unsaved instructions" } });

		fireEvent.click(screen.getByTestId("agent-form-cancel"));

		await waitFor(() => expect(confirmMock).toHaveBeenCalledTimes(1));
		// The discard prompt copy is wired through (keys resolve to their defaults in the i18n mock).
		expect(confirmMock).toHaveBeenCalledWith(
			expect.objectContaining({ confirmationText: "Discard", cancellationText: "Keep editing" }),
		);
		// Declined → still open.
		expect(screen.getByRole("dialog")).toBeTruthy();
	});

	it("closes on Cancel with unsaved edits once the discard is confirmed", async () => {
		// User chooses "Discard" → confirm resolves true → editor closes.
		confirmMock.mockResolvedValue(true);
		renderPage();

		fireEvent.click(screen.getByTestId("agent-create-button"));
		const dialog = await screen.findByRole("dialog");

		const instructions = dialog.querySelector('[data-testid="agent-form-instructions-textarea"]') as HTMLTextAreaElement;
		fireEvent.change(instructions, { target: { value: "Some unsaved instructions" } });

		fireEvent.click(screen.getByTestId("agent-form-cancel"));

		await waitFor(() => expect(confirmMock).toHaveBeenCalledTimes(1));
		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
	});

	it("closes immediately on Cancel without prompting when there are no unsaved edits", async () => {
		renderPage();

		fireEvent.click(screen.getByTestId("agent-create-button"));
		await screen.findByRole("dialog");

		// No edits made → Cancel closes without any confirm.
		fireEvent.click(screen.getByTestId("agent-form-cancel"));

		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
		expect(confirmMock).not.toHaveBeenCalled();
	});

	it("resets the editor on unmount so navigating away and back does not reopen it (stuck-bug fix)", () => {
		// Open the editor, then unmount the page (simulating route navigation away).
		useAgentManagementStore.setState({ editorTarget: { mode: "create" } });
		const { unmount } = renderPage();
		expect(screen.getByRole("dialog")).toBeTruthy();

		unmount();

		// The page's unmount effect must have cleared the singleton store's editorTarget.
		expect(useAgentManagementStore.getState().editorTarget).toBeNull();

		// Remounting (navigating back) shows the list, not the editor.
		renderPage();
		expect(screen.queryByRole("dialog")).toBeNull();
		expect(screen.getByTestId("agent-definitions-table")).toBeTruthy();
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
