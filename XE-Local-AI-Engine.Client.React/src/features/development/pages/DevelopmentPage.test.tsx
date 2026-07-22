// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const hooksMock = vi.hoisted(() => ({
	useDevelopmentProjects: vi.fn(),
	useDevelopmentProject: vi.fn(),
	useCreateDevelopmentProject: vi.fn(),
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
	readonly isPending: boolean;
	readonly error: null;
	readonly data?: Record<string, unknown>;
}

function mutation(data?: Record<string, unknown>): MutationMock {
	return { mutate: vi.fn(), isPending: false, error: null, data };
}

function detail(status: string, attempts: readonly Record<string, unknown>[] = []) {
	return {
		project: {
			id: "project-1",
			objective: "Ship Development mode",
			baseBranch: "main",
			egressPolicy: "LocalOnly",
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
		hooksMock.useDevelopmentProjects.mockReturnValue({
			data: [{ id: "project-1", objective: "Ship Development mode" }],
			isLoading: false,
			error: null,
		});
		hooksMock.useCreateDevelopmentProject.mockReturnValue(mutation());
		hooksMock.useStartDevelopmentNextAction.mockReturnValue(mutation());
		hooksMock.useCancelDevelopmentAttempt.mockReturnValue(mutation());
		hooksMock.usePreviewDevelopmentPatch.mockReturnValue(mutation());
		hooksMock.useApplyDevelopmentPatch.mockReturnValue(mutation());
	});

	afterEach(() => {
		cleanup();
	});

	it("keeps the next action disabled until a repository root is supplied", async () => {
		const start = mutation();
		hooksMock.useDevelopmentProject.mockReturnValue({ data: detail("Planned"), isLoading: false, error: null, refetch: vi.fn() });
		hooksMock.useStartDevelopmentNextAction.mockReturnValue(start);
		renderPage();
		const button = await screen.findByTestId("development-start-next");

		expect((button as HTMLButtonElement).disabled).toBe(true);
		fireEvent.change(screen.getByTestId("development-action-repository-root"), {
			target: { value: "/repo" },
		});
		expect((button as HTMLButtonElement).disabled).toBe(false);
		fireEvent.click(button);

		expect(start.mutate).toHaveBeenCalledWith({
			path: { projectId: "project-1", taskId: "task-1" },
			body: { operationId: expect.any(String), repositoryRoot: "/repo" },
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
		fireEvent.change(await screen.findByTestId("development-action-repository-root"), {
			target: { value: "/repo" },
		});

		expect((screen.getByTestId("development-start-next") as HTMLButtonElement).disabled).toBe(true);
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
		fireEvent.change(screen.getByTestId("development-action-repository-root"), { target: { value: "/repo" } });
		fireEvent.click(screen.getByTestId("development-preview-patch"));
		await waitFor(() => expect((applyButton as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(applyButton);

		expect(apply.mutate).toHaveBeenCalledWith({
			path: { projectId: "project-1", taskId: "task-1" },
			body: { operationId: expect.any(String), repositoryRoot: "/repo" },
		});
	});
});
