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
	useRegisterDevelopmentRepository: vi.fn(),
	useRegisterDevelopmentTemplate: vi.fn(),
	useRemoveDevelopmentTemplate: vi.fn(),
	useCreateDevelopmentRepositoryFromTemplate: vi.fn(),
	useCreateDevelopmentProject: vi.fn(),
	useReconnectDevelopmentRepository: vi.fn(),
	useStartDevelopmentNextAction: vi.fn(),
	useCancelDevelopmentAttempt: vi.fn(),
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

function renderPage(): void {
	render(
		<MantineProvider>
			<DevelopmentPage />
		</MantineProvider>,
	);
}

describe("DevelopmentPage", () => {
	beforeEach(() => {
		installDomMocks();
		vi.clearAllMocks();
		hooksMock.useDevelopmentCapability.mockReturnValue({ data: { enabled: true }, isLoading: false, error: null });
		hooksMock.useDevelopmentProfileDetection.mockReturnValue({ data: undefined, isFetching: false, error: null });
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
});
