// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import type {
	IntegrationAgentOption,
	IntegrationToolFacts,
	IntegrationTrigger,
} from "@/features/integrations/models/IntegrationModels";
import { useIntegrationsUiStore } from "@/features/integrations/stores/IntegrationsUiStore";

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

vi.mock("@/core/ui/hooks/useUnsavedChangesGuard", () => ({
	useUnsavedChangesGuard: () => undefined,
}));

const { triggerHooksMock, agentHookMock, confirmMock } = vi.hoisted(() => ({
	triggerHooksMock: {
		useIntegrationTriggers: vi.fn(),
		useCreateIntegrationTrigger: vi.fn(),
		useUpdateIntegrationTrigger: vi.fn(),
		useDeleteIntegrationTrigger: vi.fn(),
	},
	agentHookMock: { useIntegrationAgentOptions: vi.fn() },
	confirmMock: vi.fn(),
}));

vi.mock("@/features/integrations/queries/useIntegrationTriggers", () => triggerHooksMock);
vi.mock("@/features/integrations/queries/useIntegrationAgentOptions", () => agentHookMock);
vi.mock("@/core/ui/hooks/useConfirm", () => ({ useConfirm: () => ({ confirm: confirmMock }) }));

import { IntegrationTriggersPage } from "@/features/integrations/pages/IntegrationTriggersPage";

const readOnlyAgent: IntegrationAgentOption = {
	id: "agent-read",
	name: "Reader",
	description: "",
	allowedToolNames: ["read_file"],
	toolApprovals: {},
};

const writeAgent: IntegrationAgentOption = {
	id: "agent-write",
	name: "Writer",
	description: "",
	allowedToolNames: ["run_command"],
	toolApprovals: {},
};

const unknownToolAgent: IntegrationAgentOption = {
	id: "agent-ghost",
	name: "Ghost",
	description: "",
	allowedToolNames: ["ghost_tool"],
	toolApprovals: {},
};

const toolsByName: ReadonlyMap<string, IntegrationToolFacts> = new Map([
	["read_file", { effectiveRequiresApproval: false, category: "ReadLocal", unattendedBehaviour: "runs" }],
	["run_command", { effectiveRequiresApproval: false, category: "WriteExecute", unattendedBehaviour: "runs" }],
]);

const trigger: IntegrationTrigger = {
	id: "trigger-1",
	name: "sensor-hub",
	displayName: "Sensor hub",
	description: "Ingests sensor payloads",
	enabled: true,
	targetAgentDefinitionId: "agent-read",
	sessionPolicy: "PerInvocation",
	acceptedInputKinds: ["text"],
	createdAtUtc: 1000,
	updatedAtUtc: 2000,
	version: 3,
};

function makeMutation() {
	return { mutate: vi.fn(), isPending: false, error: null };
}

function makeQuery<T>(data: T) {
	return { data, isLoading: false, error: null };
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
	window.HTMLElement.prototype.scrollIntoView = vi.fn();
}

function pageElement() {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	return (
		<MantineProvider>
			<ConfirmProvider>
				<QueryClientProvider client={queryClient}>
					<IntegrationTriggersPage />
				</QueryClientProvider>
			</ConfirmProvider>
		</MantineProvider>
	);
}

function renderPage() {
	return render(pageElement());
}

async function openEditEditor(): Promise<void> {
	fireEvent.click(screen.getByTestId("integration-trigger-edit-trigger-1"));
	await waitFor(() => {
		expect(screen.getByTestId("integration-trigger-form")).toBeTruthy();
	});
}

async function openCreateEditor(): Promise<void> {
	fireEvent.click(screen.getByTestId("integration-trigger-create-button"));
	await waitFor(() => {
		expect(screen.getByTestId("integration-trigger-form")).toBeTruthy();
	});
}

async function fillRequiredFields(agentName: string): Promise<void> {
	fireEvent.change(screen.getByTestId("integration-trigger-form-name"), { target: { value: "sensor-hub" } });
	fireEvent.change(screen.getByTestId("integration-trigger-form-display-name"), { target: { value: "Sensor hub" } });
	fireEvent.click(screen.getByTestId("integration-trigger-form-agent"));
	fireEvent.click(await screen.findByRole("option", { name: agentName, hidden: true }));
}

async function selectCallerManaged(): Promise<void> {
	fireEvent.click(screen.getByTestId("integration-trigger-form-session-policy"));
	fireEvent.click(await screen.findByRole("option", { name: "CallerManaged", hidden: true }));
}

describe("IntegrationTriggersPage", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		useIntegrationsUiStore.setState({ editorTarget: null, keyDialogOpen: false });
		triggerHooksMock.useIntegrationTriggers.mockReturnValue(makeQuery([trigger]));
		triggerHooksMock.useCreateIntegrationTrigger.mockReturnValue(makeMutation());
		triggerHooksMock.useUpdateIntegrationTrigger.mockReturnValue(makeMutation());
		triggerHooksMock.useDeleteIntegrationTrigger.mockReturnValue(makeMutation());
		agentHookMock.useIntegrationAgentOptions.mockReturnValue({
			options: [readOnlyAgent, writeAgent, unknownToolAgent],
			toolsByName,
			isLoading: false,
		});
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders the trigger rows", () => {
		renderPage();

		const row = screen.getByTestId("integration-trigger-row-trigger-1");
		expect(within(row).getByText("Sensor hub")).toBeTruthy();
		expect(within(row).getByText("sensor-hub")).toBeTruthy();
		expect(within(row).getByText("Reader")).toBeTruthy();
	});

	it("shows the empty state when there are no triggers", () => {
		triggerHooksMock.useIntegrationTriggers.mockReturnValue(makeQuery([]));

		renderPage();

		expect(screen.getByTestId("integration-triggers-empty")).toBeTruthy();
	});

	it("surfaces a load error", () => {
		triggerHooksMock.useIntegrationTriggers.mockReturnValue({ data: undefined, isLoading: false, error: new Error("boom") });

		renderPage();

		expect(screen.getByTestId("integration-triggers-error")).toBeTruthy();
	});

	it("opens the editor dialog from the create button", async () => {
		renderPage();

		await openCreateEditor();

		expect(screen.getByTestId("integration-trigger-form-submit")).toBeTruthy();
	});

	it("shows the slug format error live and blocks Save", async () => {
		const createMutation = makeMutation();
		triggerHooksMock.useCreateIntegrationTrigger.mockReturnValue(createMutation);
		renderPage();
		await openCreateEditor();

		fireEvent.change(screen.getByTestId("integration-trigger-form-name"), { target: { value: "Bad_Name" } });

		expect(screen.getByText("Use 2-64 lowercase letters, digits or hyphens, starting with a letter or digit.")).toBeTruthy();

		fireEvent.click(screen.getByTestId("integration-trigger-form-submit"));

		expect(createMutation.mutate).not.toHaveBeenCalled();
	});

	it("creates a trigger from a valid form", async () => {
		const createMutation = makeMutation();
		triggerHooksMock.useCreateIntegrationTrigger.mockReturnValue(createMutation);
		renderPage();
		await openCreateEditor();

		await fillRequiredFields("Reader");
		fireEvent.click(screen.getByTestId("integration-trigger-form-submit"));

		expect(createMutation.mutate).toHaveBeenCalledWith(
			{
				body: {
					name: "sensor-hub",
					displayName: "Sensor hub",
					description: null,
					enabled: true,
					targetKind: "Agent",
					targetAgentDefinitionId: "agent-read",
					sessionPolicy: "PerInvocation",
					acceptedInputKinds: ["text"],
				},
			},
			{ onSuccess: expect.any(Function) },
		);
	});

	it("asks for confirmation before deleting a trigger", async () => {
		confirmMock.mockResolvedValueOnce(false);
		renderPage();

		fireEvent.click(screen.getByTestId("integration-trigger-delete-trigger-1"));

		await waitFor(() => {
			expect(confirmMock).toHaveBeenCalledTimes(1);
		});
	});

	it("saves a caller-managed trigger when every resolved tool is read-local", async () => {
		const createMutation = makeMutation();
		triggerHooksMock.useCreateIntegrationTrigger.mockReturnValue(createMutation);
		renderPage();
		await openCreateEditor();

		await fillRequiredFields("Reader");
		await selectCallerManaged();
		fireEvent.click(screen.getByTestId("integration-trigger-form-submit"));

		expect(createMutation.mutate).toHaveBeenCalledTimes(1);
		expect(createMutation.mutate.mock.calls[0]?.[0]).toMatchObject({ body: { sessionPolicy: "CallerManaged" } });
	});

	it("blocks a caller-managed trigger whose agent resolves a side-effecting tool", async () => {
		const createMutation = makeMutation();
		triggerHooksMock.useCreateIntegrationTrigger.mockReturnValue(createMutation);
		renderPage();
		await openCreateEditor();

		await fillRequiredFields("Writer");
		await selectCallerManaged();

		expect(screen.getByText(/side-effecting tools: run_command/)).toBeTruthy();

		fireEvent.click(screen.getByTestId("integration-trigger-form-submit"));

		expect(createMutation.mutate).not.toHaveBeenCalled();
	});

	it("still saves the same agent under the per-invocation policy", async () => {
		const createMutation = makeMutation();
		triggerHooksMock.useCreateIntegrationTrigger.mockReturnValue(createMutation);
		renderPage();
		await openCreateEditor();

		await fillRequiredFields("Writer");
		fireEvent.click(screen.getByTestId("integration-trigger-form-submit"));

		expect(createMutation.mutate).toHaveBeenCalledTimes(1);
	});

	it("blocks caller-managed for a tool the catalog does not know (fail-closed)", async () => {
		// The point of this case: a fail-open resolver would let exactly the trigger the backend forbids reach it.
		const createMutation = makeMutation();
		triggerHooksMock.useCreateIntegrationTrigger.mockReturnValue(createMutation);
		renderPage();
		await openCreateEditor();

		await fillRequiredFields("Ghost");
		await selectCallerManaged();

		expect(screen.getByText(/side-effecting tools: ghost_tool/)).toBeTruthy();

		fireEvent.click(screen.getByTestId("integration-trigger-form-submit"));

		expect(createMutation.mutate).not.toHaveBeenCalled();
	});

	it("confirms exactly once when a dirty editor is closed from the title bar", async () => {
		// The page owns the confirm path. With DialogShell's own confirmCloseWhen also on, the X prompted twice and
		// answering Discard then Keep editing left the dialog open.
		confirmMock.mockResolvedValue(true);
		renderPage();
		await openCreateEditor();

		fireEvent.change(screen.getByTestId("integration-trigger-form-display-name"), { target: { value: "Dirty" } });
		fireEvent.click(screen.getByLabelText("close"));

		await waitFor(() => {
			expect(confirmMock).toHaveBeenCalledTimes(1);
		});
		await waitFor(() => {
			expect(screen.queryByTestId("integration-trigger-form")).toBeNull();
		});
		expect(confirmMock).toHaveBeenCalledTimes(1);
	});

	it("blocks Save and shows the field error for a description above the backend limit", async () => {
		const createMutation = makeMutation();
		triggerHooksMock.useCreateIntegrationTrigger.mockReturnValue(createMutation);
		renderPage();
		await openCreateEditor();

		await fillRequiredFields("Reader");
		fireEvent.change(screen.getByTestId("integration-trigger-form-description"), { target: { value: "x".repeat(1025) } });
		fireEvent.click(screen.getByTestId("integration-trigger-form-submit"));

		expect(screen.getByText("The description is longer than the 1024-character limit.")).toBeTruthy();
		expect(createMutation.mutate).not.toHaveBeenCalled();
	});

	it("blocks Save when the edited trigger has left the list", async () => {
		// The point of this case: an edit whose row is gone must NOT fall through to create with the same click.
		const createMutation = makeMutation();
		const updateMutation = makeMutation();
		triggerHooksMock.useCreateIntegrationTrigger.mockReturnValue(createMutation);
		triggerHooksMock.useUpdateIntegrationTrigger.mockReturnValue(updateMutation);
		const view = renderPage();
		await openEditEditor();

		triggerHooksMock.useIntegrationTriggers.mockReturnValue(makeQuery([]));
		view.rerender(pageElement());

		expect(screen.getByText("This trigger no longer exists. Close the editor and reload the list.")).toBeTruthy();

		fireEvent.click(screen.getByTestId("integration-trigger-form-submit"));

		expect(updateMutation.mutate).not.toHaveBeenCalled();
		expect(createMutation.mutate).not.toHaveBeenCalled();
	});

	it("sends the row's expectedVersion when an edit is saved", async () => {
		const updateMutation = makeMutation();
		triggerHooksMock.useUpdateIntegrationTrigger.mockReturnValue(updateMutation);
		renderPage();
		await openEditEditor();

		fireEvent.change(screen.getByTestId("integration-trigger-form-display-name"), { target: { value: "Sensor hub v2" } });
		fireEvent.click(screen.getByTestId("integration-trigger-form-submit"));

		expect(updateMutation.mutate).toHaveBeenCalledWith(
			{
				path: { triggerId: "trigger-1" },
				body: {
					displayName: "Sensor hub v2",
					description: "Ingests sensor payloads",
					enabled: true,
					targetAgentDefinitionId: "agent-read",
					sessionPolicy: "PerInvocation",
					acceptedInputKinds: ["text"],
					expectedVersion: 3,
				},
			},
			{ onSuccess: expect.any(Function) },
		);
	});

	it("sends the row's own expectedVersion and the inverted flag from the enable switch", () => {
		const updateMutation = makeMutation();
		triggerHooksMock.useUpdateIntegrationTrigger.mockReturnValue(updateMutation);
		renderPage();

		fireEvent.click(screen.getByTestId("integration-trigger-enabled-trigger-1"));

		expect(updateMutation.mutate).toHaveBeenCalledWith(
			{
				path: { triggerId: "trigger-1" },
				body: {
					displayName: "Sensor hub",
					description: "Ingests sensor payloads",
					enabled: false,
					targetAgentDefinitionId: "agent-read",
					sessionPolicy: "PerInvocation",
					acceptedInputKinds: ["text"],
					expectedVersion: 3,
				},
			},
			{ onError: expect.any(Function) },
		);
	});

	it("resets the editor target on unmount", () => {
		useIntegrationsUiStore.setState({ editorTarget: { mode: "create" }, keyDialogOpen: false });

		const { unmount } = renderPage();
		unmount();

		expect(useIntegrationsUiStore.getState().editorTarget).toBeNull();
	});
});
