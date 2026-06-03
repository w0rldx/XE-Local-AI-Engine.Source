// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import type { ScheduledJob } from "@/features/scheduler/models/SchedulerModels";
import { useSchedulerManagementStore } from "@/features/scheduler/stores/SchedulerManagementStore";

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

// useUnsavedChangesGuard wraps useBlocker which requires a TanStack Router context.
// In page-level unit tests we render without a full router, so stub the hook to a no-op.
vi.mock("@/core/ui/hooks/useUnsavedChangesGuard", () => ({
	useUnsavedChangesGuard: () => undefined,
}));

const { hooksMock, confirmMock, hubMock } = vi.hoisted(() => ({
	hooksMock: {
		useScheduledJobTemplates: vi.fn(),
		useScheduledJobs: vi.fn(),
		useScheduledJobRuns: vi.fn(),
		useScheduledJobRun: vi.fn(),
		useCreateScheduledJob: vi.fn(),
		useUpdateScheduledJob: vi.fn(),
		useDeleteScheduledJob: vi.fn(),
		useSetScheduledJobEnabled: vi.fn(),
		useTriggerScheduledJob: vi.fn(),
		useCancelScheduledJobRun: vi.fn(),
	},
	confirmMock: vi.fn(),
	hubMock: vi.fn(),
}));

vi.mock("@/features/scheduler/queries/useScheduler", () => hooksMock);
vi.mock("@/features/scheduler/hooks/useSchedulerHub", () => ({ useSchedulerHub: hubMock }));
vi.mock("@/core/ui/hooks/useConfirm", () => ({
	useConfirm: () => ({ confirm: confirmMock }),
}));

import { SchedulerPage } from "@/features/scheduler/pages/SchedulerPage";

const cronJob: ScheduledJob = {
	id: "job-1",
	templateId: "cleanup",
	displayName: "Nightly cleanup",
	description: "Removes old rows",
	enabled: false,
	scheduleKind: "Cron",
	cronExpression: "0 0 3 * * ?",
	intervalSeconds: null,
	repeatCount: null,
	startAtUtc: null,
	endAtUtc: null,
	timeZoneId: "UTC",
	misfirePolicy: "Smart",
	preventOverlap: true,
	maxRuntimeSeconds: null,
	hasParameters: false,
	createdBy: "User",
	createdAtUtc: 1000,
	updatedAtUtc: 2000,
	disabledAtUtc: null,
	deletedAtUtc: null,
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
}

function renderPage() {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	return render(
		<MantineProvider>
			<ConfirmProvider>
				<QueryClientProvider client={queryClient}>
					<SchedulerPage />
				</QueryClientProvider>
			</ConfirmProvider>
		</MantineProvider>,
	);
}

describe("SchedulerPage", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		useSchedulerManagementStore.setState({ editorTarget: null, selectedRunId: null });
		hooksMock.useScheduledJobTemplates.mockReturnValue(makeQuery([]));
		hooksMock.useScheduledJobs.mockReturnValue(makeQuery([cronJob]));
		hooksMock.useScheduledJobRuns.mockReturnValue(makeQuery([]));
		hooksMock.useScheduledJobRun.mockReturnValue(makeQuery(undefined));
		hooksMock.useCreateScheduledJob.mockReturnValue(makeMutation());
		hooksMock.useUpdateScheduledJob.mockReturnValue(makeMutation());
		hooksMock.useDeleteScheduledJob.mockReturnValue(makeMutation());
		hooksMock.useSetScheduledJobEnabled.mockReturnValue(makeMutation());
		hooksMock.useTriggerScheduledJob.mockReturnValue(makeMutation());
		hooksMock.useCancelScheduledJobRun.mockReturnValue(makeMutation());
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("mounts the scheduler hub for live updates", () => {
		renderPage();

		expect(hubMock).toHaveBeenCalled();
	});

	it("polls run history as a REST fallback when scheduler hub events are missed", () => {
		renderPage();

		expect(hooksMock.useScheduledJobRuns).toHaveBeenCalledWith({}, { refetchInterval: 5000 });
	});

	it("renders the list of scheduled jobs", () => {
		renderPage();

		expect(screen.getByTestId("scheduler-jobs-table")).toBeTruthy();
		const row = screen.getByTestId("scheduler-job-row-job-1");
		expect(within(row).getByText("Nightly cleanup")).toBeTruthy();
	});

	it("shows the empty state when there are no jobs", () => {
		hooksMock.useScheduledJobs.mockReturnValue(makeQuery([]));

		renderPage();

		expect(screen.getByTestId("scheduler-jobs-empty")).toBeTruthy();
	});

	it("opens the create editor dialog from the create button", async () => {
		renderPage();

		fireEvent.click(screen.getByTestId("scheduler-create-button"));

		// Mantine Modal portals content asynchronously — waitFor covers the async mount.
		await waitFor(() => {
			expect(screen.getByTestId("scheduled-job-form")).toBeTruthy();
		});
	});

	it("Save and Cancel buttons are present in the editor dialog footer", async () => {
		renderPage();

		fireEvent.click(screen.getByTestId("scheduler-create-button"));

		await waitFor(() => {
			expect(screen.getByTestId("scheduler-form-submit")).toBeTruthy();
		});
		expect(screen.getByTestId("scheduler-form-cancel")).toBeTruthy();
	});

	it("opens the edit editor pre-filled from a row action", async () => {
		renderPage();

		fireEvent.click(screen.getByTestId("scheduler-job-edit-job-1"));

		await waitFor(() => {
			expect(screen.getByTestId("scheduler-form-name")).toBeTruthy();
		});
		const nameInput = screen.getByTestId("scheduler-form-name") as HTMLInputElement;
		expect(nameInput.value).toBe("Nightly cleanup");
		const cronInput = screen.getByTestId("scheduler-form-cron") as HTMLInputElement;
		expect(cronInput.value).toBe("0 0 3 * * ?");
	});

	it("toggles a job enabled through the row switch", () => {
		const enableMutation = makeMutation();
		hooksMock.useSetScheduledJobEnabled.mockReturnValue(enableMutation);

		renderPage();

		fireEvent.click(screen.getByTestId("scheduler-job-enabled-job-1"));

		expect(enableMutation.mutate).toHaveBeenCalledWith({ id: "job-1", enabled: true });
	});

	it("triggers a job through the row action", () => {
		const triggerMutation = makeMutation();
		hooksMock.useTriggerScheduledJob.mockReturnValue(triggerMutation);

		renderPage();

		fireEvent.click(screen.getByTestId("scheduler-job-trigger-job-1"));

		expect(triggerMutation.mutate).toHaveBeenCalledWith({ path: { scheduledJobId: "job-1" } });
	});

	it("surfaces a load error", () => {
		hooksMock.useScheduledJobs.mockReturnValue({ data: undefined, isLoading: false, error: new Error("boom") });

		renderPage();

		expect(screen.getByTestId("scheduler-list-error")).toBeTruthy();
	});

	it("closing the editor dialog (clean form) resets editorTarget immediately", async () => {
		renderPage();

		// Open the editor
		fireEvent.click(screen.getByTestId("scheduler-create-button"));
		await waitFor(() => {
			expect(screen.getByTestId("scheduler-form-cancel")).toBeTruthy();
		});

		// Click cancel — form is clean so no confirm, closes immediately
		fireEvent.click(screen.getByTestId("scheduler-form-cancel"));

		// editorTarget is null — the store was reset
		expect(useSchedulerManagementStore.getState().editorTarget).toBeNull();
	});

	it("Cancel while dirty prompts for confirmation before closing", async () => {
		// Resolve to true = user chose Discard
		confirmMock.mockResolvedValueOnce(true);
		renderPage();

		fireEvent.click(screen.getByTestId("scheduler-create-button"));
		await waitFor(() => {
			expect(screen.getByTestId("scheduler-form-name")).toBeTruthy();
		});

		// Dirty the form
		fireEvent.change(screen.getByTestId("scheduler-form-name"), { target: { value: "Edited name" } });

		fireEvent.click(screen.getByTestId("scheduler-form-cancel"));

		// requestCloseEditor awaits confirm; after discard the editor closes
		await waitFor(() => {
			expect(confirmMock).toHaveBeenCalledTimes(1);
		});
		await waitFor(() => {
			expect(useSchedulerManagementStore.getState().editorTarget).toBeNull();
		});
	});

	it("Cancel while dirty keeps the editor open when user chooses keep-editing", async () => {
		// Resolve to false = user chose Keep editing
		confirmMock.mockResolvedValueOnce(false);
		renderPage();

		fireEvent.click(screen.getByTestId("scheduler-create-button"));
		await waitFor(() => {
			expect(screen.getByTestId("scheduler-form-name")).toBeTruthy();
		});

		// Dirty the form
		fireEvent.change(screen.getByTestId("scheduler-form-name"), { target: { value: "Edited name" } });

		fireEvent.click(screen.getByTestId("scheduler-form-cancel"));

		await waitFor(() => {
			expect(confirmMock).toHaveBeenCalledTimes(1);
		});
		// Editor should remain open — editorTarget is not null
		expect(useSchedulerManagementStore.getState().editorTarget).not.toBeNull();
	});

	it("resets editorTarget on unmount (stuck-bug fix)", () => {
		// Start with an open editor
		useSchedulerManagementStore.setState({ editorTarget: { mode: "create" }, selectedRunId: null });

		const { unmount } = renderPage();

		// Unmounting the page should clear the store
		unmount();

		expect(useSchedulerManagementStore.getState().editorTarget).toBeNull();
		expect(useSchedulerManagementStore.getState().selectedRunId).toBeNull();
	});

	it("successful save clears isFormDirty so no spurious nav guard fires afterwards", async () => {
		// Wire update mutation to call onSuccess immediately — simulates a successful save.
		const updateMutation = {
			mutate: vi.fn((_vars: unknown, opts: { onSuccess?: () => void }) => opts.onSuccess?.()),
			isPending: false,
			error: null,
		};
		hooksMock.useUpdateScheduledJob.mockReturnValue(updateMutation);

		// Open the editor pre-filled with the existing cronJob (all required fields valid).
		useSchedulerManagementStore.setState({ editorTarget: { mode: "edit", id: "job-1" }, selectedRunId: null });
		renderPage();

		// Wait for the form to appear in the dialog portal
		await waitFor(() => {
			expect(screen.getByTestId("scheduler-form-name")).toBeTruthy();
		});

		// Dirty the form — isFormDirty becomes true
		fireEvent.change(screen.getByTestId("scheduler-form-name"), { target: { value: "Updated name" } });

		// Click the footer Save — formRef.current.submit() → Zod validates (all fields valid) → handleSubmit → mutate → onSuccess → closeEditorClean
		fireEvent.click(screen.getByTestId("scheduler-form-submit"));

		// After successful save: editor is closed and no confirm prompt was shown
		await waitFor(() => {
			expect(useSchedulerManagementStore.getState().editorTarget).toBeNull();
		});
		// closeEditorClean cleared isFormDirty — confirm was never called
		expect(confirmMock).not.toHaveBeenCalled();
	});
});
