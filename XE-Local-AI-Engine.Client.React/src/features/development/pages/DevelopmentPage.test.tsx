// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const hooksMock = vi.hoisted(() => ({
	useDevelopmentCapability: vi.fn(),
	useDevelopmentRepositories: vi.fn(),
	useDevelopmentTemplates: vi.fn(),
	useDevelopmentProfileDetection: vi.fn(),
	useDevelopmentProjects: vi.fn(),
	useDevelopmentProject: vi.fn(),
	useDevelopmentTaskWorkflowRun: vi.fn(),
	useRegisterDevelopmentRepository: vi.fn(),
	useRegisterDevelopmentTemplate: vi.fn(),
	useRemoveDevelopmentTemplate: vi.fn(),
	useCreateDevelopmentRepositoryFromTemplate: vi.fn(),
	useCreateDevelopmentProject: vi.fn(),
	useReconnectDevelopmentRepository: vi.fn(),
	useStartDevelopmentNextAction: vi.fn(),
	useCancelDevelopmentAttempt: vi.fn(),
	useConfirmDevelopmentContainerRuntime: vi.fn(),
	usePreviewDevelopmentPatch: vi.fn(),
	useApplyDevelopmentPatch: vi.fn(),
}));

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}));

vi.mock("@/features/development/queries/useDevelopment", () => hooksMock);
vi.mock("@/features/development/hooks/useDevelopmentAttemptHub", () => ({
	useDevelopmentAttemptHub: () => ({
		connectionState: "idle",
		watermark: 0,
		droppedOrCoalescedUpdateCount: 0,
		latest: null,
		updates: [],
	}),
}));
vi.mock("@/features/development/components/DevelopmentProjectForm", () => ({
	DevelopmentProjectForm: () => <div data-testid="development-project-form" />,
}));
vi.mock("@/features/development/components/DevelopmentLivePanel", () => ({
	DevelopmentLivePanel: () => <div data-testid="development-live-panel" />,
}));

import { DevelopmentPage } from "@/features/development/pages/DevelopmentPage";

interface MutationMock {
	readonly mutate: ReturnType<typeof vi.fn>;
	readonly mutateAsync: ReturnType<typeof vi.fn>;
	readonly isPending: boolean;
	readonly error: null;
	readonly data?: Record<string, unknown>;
}

function mutation(data?: Record<string, unknown>): MutationMock {
	return { mutate: vi.fn(), mutateAsync: vi.fn(), isPending: false, error: null, data };
}

/** A project holding several tasks, which is what a workflow decomposition leaves behind (Phase W dropped the index). */
function decomposedDetail(...titles: readonly string[]) {
	const base = detail("Planned");
	return {
		...base,
		tasks: titles.map((title, index) => ({
			task: { id: `task-${index + 1}`, projectId: "project-1", title, requirements: "…", status: "Planned" },
			attempts: [],
			artifacts: [],
		})),
	};
}

function detail(status: string, attempts: readonly Record<string, unknown>[] = [], repositoryConnectionRequired = false) {
	return {
		project: {
			id: "project-1",
			objective: "Ship Development mode",
			selectedFolderId: repositoryConnectionRequired ? null : "repository-1",
			repositoryConnectionRequired,
			baseBranch: "main",
			egressPolicy: "LocalOnly",
			version: 4,
		},
		tasks: [
			{
				task: {
					id: "task-1",
					projectId: "project-1",
					title: "Implement feature",
					requirements: "Keep actions explicitly gated.",
					status,
				},
				attempts,
				artifacts: [],
			},
		],
		events: [],
	};
}

function attempt(status: string) {
	return {
		id: "attempt-1",
		taskId: "task-1",
		role: "Coder",
		modelId: "coder-model",
		provider: "local",
		status,
		inputTokens: 0,
		outputTokens: 0,
	};
}

function installDomMocks(): void {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
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

function renderPage(props: { initialProjectId?: string; initialTaskId?: string } = {}): void {
	render(
		<MantineProvider>
			<DevelopmentPage {...props} />
		</MantineProvider>,
	);
}

describe("DevelopmentPage", () => {
	beforeEach(() => {
		installDomMocks();
		vi.clearAllMocks();
		hooksMock.useDevelopmentCapability.mockReturnValue({ data: { enabled: true }, isLoading: false, error: null });
		hooksMock.useDevelopmentProfileDetection.mockReturnValue({ data: undefined, isFetching: false, error: null });
		// The Y3 banner's deep link only. Most tasks carry no workflow run, so the default is an unresolved lookup.
		hooksMock.useDevelopmentTaskWorkflowRun.mockReturnValue({ data: undefined, isLoading: false, error: null });
		hooksMock.useDevelopmentRepositories.mockReturnValue({
			data: [{ id: "repository-1", alias: "Workspace", availability: "Available" }],
			isLoading: false,
			error: null,
		});
		hooksMock.useDevelopmentProjects.mockReturnValue({
			data: [{ id: "project-1", objective: "Ship Development mode" }],
			isLoading: false,
			error: null,
		});
		hooksMock.useDevelopmentTemplates.mockReturnValue({ data: [], isLoading: false, error: null });
		hooksMock.useRegisterDevelopmentRepository.mockReturnValue(mutation());
		hooksMock.useRegisterDevelopmentTemplate.mockReturnValue(mutation());
		hooksMock.useRemoveDevelopmentTemplate.mockReturnValue(mutation());
		hooksMock.useCreateDevelopmentRepositoryFromTemplate.mockReturnValue(mutation());
		hooksMock.useCreateDevelopmentProject.mockReturnValue(mutation());
		hooksMock.useReconnectDevelopmentRepository.mockReturnValue(mutation());
		hooksMock.useStartDevelopmentNextAction.mockReturnValue(mutation());
		hooksMock.useCancelDevelopmentAttempt.mockReturnValue(mutation());
		hooksMock.useConfirmDevelopmentContainerRuntime.mockReturnValue(mutation());
		hooksMock.usePreviewDevelopmentPatch.mockReturnValue(mutation());
		hooksMock.useApplyDevelopmentPatch.mockReturnValue(mutation());
	});

	afterEach(() => {
		cleanup();
	});

	it("starts the next action from the persisted repository binding without sending a host path", async () => {
		const start = mutation();
		hooksMock.useDevelopmentProject.mockReturnValue({ data: detail("Planned"), isLoading: false, error: null, refetch: vi.fn() });
		hooksMock.useStartDevelopmentNextAction.mockReturnValue(start);
		renderPage();
		const button = await screen.findByTestId("development-start-next");

		expect((button as HTMLButtonElement).disabled).toBe(false);
		fireEvent.click(button);

		expect(start.mutate).toHaveBeenCalledWith({
			path: { projectId: "project-1", taskId: "task-1" },
			body: { operationId: expect.any(String) },
		});
	});

	it("blocks a second start and exposes cancellation while an attempt is active", async () => {
		const cancel = mutation();
		hooksMock.useDevelopmentProject.mockReturnValue({
			data: detail("InProgress", [attempt("Running")]),
			isLoading: false,
			error: null,
			refetch: vi.fn(),
		});
		hooksMock.useCancelDevelopmentAttempt.mockReturnValue(cancel);
		renderPage();

		expect(((await screen.findByTestId("development-start-next")) as HTMLButtonElement).disabled).toBe(true);
		fireEvent.click(screen.getByTestId("development-cancel-attempt"));

		expect(cancel.mutate).toHaveBeenCalledWith({
			path: { projectId: "project-1", taskId: "task-1", attemptId: "attempt-1" },
		});
	});

	it("enables apply only after a successful preview for the current task", async () => {
		const preview = mutation({ subjectHash: "subject", patchHash: "patch", manifestHash: "manifest", patch: "diff" });
		preview.mutate.mockImplementation((_input, options?: { onSuccess?: () => void }) => options?.onSuccess?.());
		const apply = mutation();
		hooksMock.useDevelopmentProject.mockReturnValue({
			data: detail("AwaitingApply"),
			isLoading: false,
			error: null,
			refetch: vi.fn(),
		});
		hooksMock.usePreviewDevelopmentPatch.mockReturnValue(preview);
		hooksMock.useApplyDevelopmentPatch.mockReturnValue(apply);
		renderPage();
		const applyButton = await screen.findByTestId("development-apply-patch");

		expect((applyButton as HTMLButtonElement).disabled).toBe(true);
		fireEvent.click(screen.getByTestId("development-preview-patch"));
		await waitFor(() => expect((applyButton as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(applyButton);

		expect(apply.mutate).toHaveBeenCalledWith({
			path: { projectId: "project-1", taskId: "task-1" },
			body: { operationId: expect.any(String) },
		});
	});

	it("fails closed when the authenticated runtime capability is disabled", () => {
		hooksMock.useDevelopmentCapability.mockReturnValue({ data: { enabled: false }, isLoading: false, error: null });
		hooksMock.useDevelopmentProject.mockReturnValue({ data: undefined, isLoading: false, error: null, refetch: vi.fn() });

		renderPage();

		expect(screen.getByText("Development Mode is disabled by this node's runtime configuration.")).toBeTruthy();
		expect(screen.queryByTestId("development-project-form")).toBeNull();
	});

	it("reconnects a migrated project through an available registered repository", async () => {
		const reconnect = mutation();
		hooksMock.useDevelopmentProject.mockReturnValue({
			data: detail("Planned", [], true),
			isLoading: false,
			error: null,
			refetch: vi.fn(),
		});
		hooksMock.useReconnectDevelopmentRepository.mockReturnValue(reconnect);
		renderPage();

		fireEvent.click(await screen.findByTestId("development-reconnect-select"));
		fireEvent.click(await screen.findByText("Workspace"));
		fireEvent.click(screen.getByTestId("development-reconnect-repository"));

		expect(reconnect.mutate).toHaveBeenCalledWith(
			{
				path: { projectId: "project-1" },
				body: { selectedFolderId: "repository-1", expectedVersion: 4 },
			},
			expect.objectContaining({ onSuccess: expect.any(Function) }),
		);
	});

	// X8 — the two-file search-param addition that lets a workflow's DevTask node land on the project it drove.
	it("seeds the initial project selection from the deep link's search params", async () => {
		hooksMock.useDevelopmentProjects.mockReturnValue({
			data: [
				{ id: "project-1", objective: "Ship Development mode" },
				{ id: "project-2", objective: "The one the workflow drove" },
			],
			isLoading: false,
			error: null,
		});
		hooksMock.useDevelopmentProject.mockReturnValue({ data: undefined, isLoading: true, error: null, refetch: vi.fn() });

		renderPage({ initialProjectId: "project-2", initialTaskId: "task-9" });

		// The first render already asks for the linked project: seeding through the default-to-first effect instead
		// would have made the page flash the wrong project's evidence before correcting itself.
		await waitFor(() => expect(hooksMock.useDevelopmentProject).toHaveBeenCalled());
		expect(hooksMock.useDevelopmentProject.mock.calls[0]?.[0]).toBe("project-2");
		expect(hooksMock.useDevelopmentProject.mock.calls.at(-1)?.[0]).toBe("project-2");
	});

	it("still defaults to the first project when no search params are given", async () => {
		hooksMock.useDevelopmentProjects.mockReturnValue({
			data: [
				{ id: "project-1", objective: "Ship Development mode" },
				{ id: "project-2", objective: "The one the workflow drove" },
			],
			isLoading: false,
			error: null,
		});
		hooksMock.useDevelopmentProject.mockReturnValue({ data: undefined, isLoading: true, error: null, refetch: vi.fn() });

		renderPage();

		await waitFor(() => expect(hooksMock.useDevelopmentProject.mock.calls.at(-1)?.[0]).toBe("project-1"));
	});
	it("offers no task switcher for a project with one task, and one for a decomposed project", async () => {
		hooksMock.useDevelopmentProject.mockReturnValue({ data: detail("Planned"), isLoading: false, error: null, refetch: vi.fn() });
		renderPage();
		await screen.findByTestId("development-project-detail");

		// A picker with one entry is a question with one answer.
		expect(screen.queryByTestId("development-task-switcher")).toBeNull();

		cleanup();
		hooksMock.useDevelopmentProject.mockReturnValue({
			data: decomposedDetail("Operator request", "Slice one", "Slice two"),
			isLoading: false,
			error: null,
			refetch: vi.fn(),
		});
		renderPage();

		// The ordinary decomposed case is three tasks in one project, two of which had no way to be reached at all.
		expect(await screen.findByTestId("development-task-switcher")).toBeDefined();
		// `ListTasksAsync` orders by CreatedAtUtc, so the row it opens on is the operator's own task, not a child.
		const switcher = screen.getByTestId("development-task-switcher");
		expect((switcher.querySelector("input") as HTMLInputElement).value).toBe("Operator request");
	});

	it("opens on the task the deep link names, not on the project's first one", async () => {
		hooksMock.useDevelopmentProject.mockReturnValue({
			data: decomposedDetail("Operator request", "Slice one", "Slice two"),
			isLoading: false,
			error: null,
			refetch: vi.fn(),
		});
		const start = mutation();
		hooksMock.useStartDevelopmentNextAction.mockReturnValue(start);
		renderPage({ initialTaskId: "task-3" });

		fireEvent.click(await screen.findByTestId("development-start-next"));

		// The selection reaches the mutation, so it is the shown task and not merely a highlighted row.
		expect(start.mutate).toHaveBeenCalledWith({
			path: { projectId: "project-1", taskId: "task-3" },
			body: { operationId: expect.any(String) },
		});
	});

	/** A task a workflow created and still owns, plus whatever the run lookup answers about that run. */
	function workflowTask(runQuery: Record<string, unknown>) {
		const base = detail("AwaitingApply");
		hooksMock.useDevelopmentProject.mockReturnValue({
			data: { ...base, tasks: [{ ...base.tasks[0], task: { ...base.tasks[0]?.task, workflowRunId: "run-9" } }] },
			isLoading: false,
			error: null,
			refetch: vi.fn(),
		});
		hooksMock.useDevelopmentTaskWorkflowRun.mockReturnValue(runQuery);
	}

	it("gives the apply button BACK when the workflow run has ended, so a validated patch is not stranded", async () => {
		// A terminal run can answer no further gate — DecideAsync refuses anything that is not WaitingForApproval or
		// Blocked, and the dispatcher does not tick a terminal run at all. Withholding Apply here would strand a
		// hash-locked, already-validated patch for good rather than protect it.
		workflowTask({ data: { id: "run-9", workItemId: "item-4", status: "Failed" }, isError: false, isLoading: false, error: null });
		renderPage();

		expect(await screen.findByTestId("development-apply-patch")).toBeDefined();
		expect(screen.getByTestId("development-workflow-banner").textContent).toContain("can no longer approve");
	});

	it("keeps the patch read-only when the run status cannot be read, and offers the read again", async () => {
		// An unreadable status is not an ended run. Ownership is enforced by the server — Dev Mode's apply refuses a
		// task a live run drives — so an Apply button offered on a failed read buys a 409, while withholding it costs
		// a retry. Fail CLOSED, and say so with a way out.
		const refetch = vi.fn();
		workflowTask({ data: undefined, isError: true, isLoading: false, error: new Error("gone"), refetch });
		renderPage();

		expect((await screen.findByTestId("development-workflow-banner")).textContent).toContain("could not be read");
		expect(screen.queryByTestId("development-apply-patch")).toBeNull();
		// The evidence stays readable, as it does for a live run.
		expect(screen.getByTestId("development-preview-patch")).toBeDefined();

		fireEvent.click(screen.getByTestId("development-workflow-retry"));
		expect(refetch).toHaveBeenCalled();
	});

	it("stays read-only while the run status is still in flight, which is the safe side of that race", async () => {
		// A live run's gate is the authority; offering a second Apply for the moment before the status lands is a bypass.
		workflowTask({ data: undefined, isError: false, isLoading: true, error: null });
		renderPage();

		await screen.findByTestId("development-workflow-banner");
		expect(screen.queryByTestId("development-apply-patch")).toBeNull();
	});

	it("takes the apply button away from a workflow-driven task and says where the decision lives (Y3)", async () => {
		workflowTask({
			data: { id: "run-9", workItemId: "item-4", status: "Running" },
			isError: false,
			isLoading: false,
			error: null,
		});
		renderPage();

		expect((await screen.findByTestId("development-workflow-banner")).textContent).toContain("gate node");
		// A second Apply button here would be a duplicate authority or a bypass of the graph's audit trail.
		expect(screen.queryByTestId("development-apply-patch")).toBeNull();
		// The evidence stays: reading the patch is not deciding on it.
		expect(screen.getByTestId("development-preview-patch")).toBeDefined();
		// And the way back to the run is offered, keyed by the WORK ITEM the run lookup resolved.
		expect(screen.getByTestId("development-workflow-link")).toBeDefined();
	});
});
