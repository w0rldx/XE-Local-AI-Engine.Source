// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";
import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import type {
	IntegrationExecution,
	IntegrationExecutionFilters,
	IntegrationTrigger,
} from "@/features/integrations/models/IntegrationModels";
import { useIntegrationsUiStore } from "@/features/integrations/stores/IntegrationsUiStore";
import { installJsdomEnvironmentMocks } from "@/test/MantineTestRender";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string, defaultValue?: string | Record<string, unknown>, options?: Record<string, unknown>) => {
			const config = typeof defaultValue === "object" ? defaultValue : options;
			let text = typeof defaultValue === "string" ? defaultValue : ((config?.["defaultValue"] as string) ?? key);
			if (config) {
				for (const [name, value] of Object.entries(config)) {
					text = text.replace(`{{${name}}}`, String(value));
				}
			}
			return text;
		},
	}),
}));

const { executionHooksMock, sessionHooksMock, triggerHooksMock, confirmMock, toastMock } = vi.hoisted(() => ({
	executionHooksMock: {
		useIntegrationExecutions: vi.fn(),
		useIntegrationExecution: vi.fn(),
		useIntegrationExecutionEvents: vi.fn(),
		useCancelIntegrationExecution: vi.fn(),
	},
	sessionHooksMock: { useIntegrationSessions: vi.fn(), useDeleteIntegrationSession: vi.fn() },
	triggerHooksMock: { useIntegrationTriggers: vi.fn() },
	confirmMock: vi.fn(),
	toastMock: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warn: vi.fn(), warning: vi.fn(), progress: vi.fn() },
}));

vi.mock("@/features/integrations/queries/useIntegrationExecutions", () => executionHooksMock);
vi.mock("@/features/integrations/queries/useIntegrationSessions", () => sessionHooksMock);
vi.mock("@/features/integrations/queries/useIntegrationTriggers", () => triggerHooksMock);
vi.mock("@/core/ui/hooks/useConfirm", () => ({ useConfirm: () => ({ confirm: confirmMock }) }));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

import { IntegrationExecutionsPage } from "@/features/integrations/pages/IntegrationExecutionsPage";

const triggers: IntegrationTrigger[] = [
	{
		id: "trigger-1",
		name: "sensor-hub",
		displayName: "Sensor hub",
		description: "",
		enabled: true,
		targetAgentDefinitionId: "agent-1",
		sessionPolicy: "PerInvocation",
		acceptedInputKinds: ["text"],
		createdAtUtc: 1000,
		updatedAtUtc: 1000,
		version: 1,
	},
];

function execution(overrides: Partial<IntegrationExecution> & { id: string }): IntegrationExecution {
	return {
		triggerId: "trigger-1",
		sessionId: "5c0ffee0-0000-0000-0000-000000000001",
		status: "Completed",
		receivedAtUtc: 1_700_000_000_000,
		startedAtUtc: 1_700_000_001_000,
		endedAtUtc: 1_700_000_003_000,
		failureCategory: null,
		failureSummary: null,
		outputCount: 0,
		...overrides,
	};
}

// One row per state the cancel action has to distinguish, plus the two R3-2 edges that terminalise a run which never
// started (Cancelled and Failed with no startedAtUtc). The order is deliberately NOT sorted by any visible column:
// the table must render the response as it arrived.
const executions: IntegrationExecution[] = [
	execution({ id: "exec-running", status: "Running", endedAtUtc: null, receivedAtUtc: 1_700_000_000_500 }),
	execution({ id: "exec-accepted", status: "Accepted", startedAtUtc: null, endedAtUtc: null }),
	execution({ id: "exec-queued", status: "Queued", startedAtUtc: null, endedAtUtc: null }),
	execution({ id: "exec-completed", status: "Completed", outputCount: 2 }),
	execution({
		id: "exec-cancelled",
		status: "Cancelled",
		startedAtUtc: null,
		endedAtUtc: 1_700_000_002_000,
	}),
	execution({
		id: "exec-approval",
		status: "Failed",
		startedAtUtc: null,
		endedAtUtc: 1_700_000_002_000,
		failureCategory: "approval-required",
		failureSummary: "A tool needed manual approval.",
	}),
	execution({ id: "exec-timeout", status: "Failed", failureCategory: "queue-timeout" }),
	execution({ id: "exec-policy", status: "Failed", failureCategory: "session-policy" }),
	execution({ id: "exec-unknown", status: "Failed", failureCategory: "a-category-this-client-has-never-seen" }),
];

/** The per-execution audit read, which carries the whole record and so outranks the row found in the list window. */
const detail = {
	execution: execution({ id: "exec-completed", outputCount: 2 }),
	principalId: "principal-0000-0000-0000-000000000001",
	keyPrefix: "xeint_ab",
	requestId: "req-1",
	invocationId: "inv-1",
	outputBytes: 512,
	stopRequestedAtUtc: null,
};

function makeMutation() {
	return { mutate: vi.fn(), isPending: false, error: null };
}

function makeQuery<T>(data: T) {
	return { data, isLoading: false, error: null };
}

/** A list page as the hook now returns it: the rows, plus the server's count of every row the filters match. */
function makeListQuery<T>(items: readonly T[], totalCount = items.length) {
	return makeQuery({ items, totalCount });
}

function renderPage() {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	// A FRESH element each time: re-rendering the identical one lets React bail out, and a poll test needs the page
	// to actually read its hooks again.
	const tree = () => (
		<MantineProvider>
			<ConfirmProvider>
				<QueryClientProvider client={queryClient}>
					<IntegrationExecutionsPage />
				</QueryClientProvider>
			</ConfirmProvider>
		</MantineProvider>
	);
	const result = render(tree());
	return { ...result, repoll: () => result.rerender(tree()) };
}

/** The filters the page most recently asked the executions list for. */
function lastExecutionFilters(): IntegrationExecutionFilters {
	const calls = executionHooksMock.useIntegrationExecutions.mock.calls;
	return (calls.at(-1)?.[0] ?? {}) as IntegrationExecutionFilters;
}

/** The paging window the page most recently asked the executions list for. */
function lastExecutionWindow(): { limit?: number; offset?: number } {
	const calls = executionHooksMock.useIntegrationExecutions.mock.calls;
	return (calls.at(-1)?.[1] ?? {}) as { limit?: number; offset?: number };
}

/** Every offset the page has asked for, in order — one entry per render, so repeats are expected and a CHANGE is not. */
function requestedOffsets(): number[] {
	return executionHooksMock.useIntegrationExecutions.mock.calls.map(
		(call) => ((call[1] ?? {}) as { offset?: number }).offset ?? 0,
	);
}

/** Clicks one status chip and waits for the status SET the query is then asked for (undefined for the All chip). */
async function clickStatusChip(label: string, expected: readonly string[] | undefined): Promise<void> {
	fireEvent.click(within(screen.getByTestId("integration-executions-status-chips")).getByText(label));
	await waitFor(() => {
		expect(lastExecutionFilters().status).toEqual(expected);
	});
}

describe("IntegrationExecutionsPage", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		useIntegrationsUiStore.setState({ selectedExecutionId: null, selectedSessionId: null });
		executionHooksMock.useIntegrationExecutions.mockReturnValue(makeListQuery(executions));
		executionHooksMock.useIntegrationExecution.mockReturnValue(makeQuery(undefined));
		executionHooksMock.useIntegrationExecutionEvents.mockReturnValue(makeQuery([]));
		executionHooksMock.useCancelIntegrationExecution.mockReturnValue(makeMutation());
		sessionHooksMock.useIntegrationSessions.mockReturnValue(makeListQuery([]));
		triggerHooksMock.useIntegrationTriggers.mockReturnValue(makeQuery(triggers));
		confirmMock.mockResolvedValue(true);
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders rows in the order the response supplied them, without re-sorting", () => {
		renderPage();

		const rendered = Array.from(screen.getByTestId("integration-executions-table").querySelectorAll("tbody tr")).map(
			(row) => row.getAttribute("data-testid"),
		);
		expect(rendered).toEqual(executions.map((row) => `integration-execution-row-${row.id}`));
	});

	it("sends exactly one status to the query per chip and none for All", async () => {
		renderPage();

		expect(lastExecutionFilters().status).toBeUndefined();

		await clickStatusChip("Accepted", ["Accepted"]);
		await clickStatusChip("Queued", ["Queued"]);
		await clickStatusChip("Running", ["Running"]);
		await clickStatusChip("Completed", ["Completed"]);
		await clickStatusChip("Failed", ["Failed"]);
		await clickStatusChip("Cancelled", ["Cancelled"]);
		await clickStatusChip("All", undefined);
	});

	// D-3: everything in flight in one click. The endpoint takes a repeated `status`, so this is one server-side
	// question — the browser never unions three separately-paged reads.
	it("sends the three in-flight states together for the Active chip", async () => {
		renderPage();

		await clickStatusChip("Active", ["Accepted", "Queued", "Running"]);
		await clickStatusChip("All", undefined);
	});

	it("offers cancel only on the three active statuses", () => {
		renderPage();

		expect(screen.getByTestId("integration-execution-cancel-exec-accepted")).toBeTruthy();
		expect(screen.getByTestId("integration-execution-cancel-exec-queued")).toBeTruthy();
		expect(screen.getByTestId("integration-execution-cancel-exec-running")).toBeTruthy();
		expect(screen.queryByTestId("integration-execution-cancel-exec-completed")).toBeNull();
		expect(screen.queryByTestId("integration-execution-cancel-exec-cancelled")).toBeNull();
		expect(screen.queryByTestId("integration-execution-cancel-exec-timeout")).toBeNull();
	});

	it("confirms before cancelling and then requests cancellation of that execution", async () => {
		const cancel = makeMutation();
		executionHooksMock.useCancelIntegrationExecution.mockReturnValue(cancel);
		renderPage();

		fireEvent.click(screen.getByTestId("integration-execution-cancel-exec-running"));

		await waitFor(() => {
			expect(cancel.mutate).toHaveBeenCalled();
		});
		expect(confirmMock).toHaveBeenCalledTimes(1);
		expect(cancel.mutate.mock.calls.at(0)?.[0]).toEqual({ path: { executionId: "exec-running" } });
	});

	// The 409 body is a FastEndpoints validation problem whose only readable field is a generic English title, so the
	// page must not echo it: this race gets its own localized sentence.
	it("says the run already finished when cancellation is refused with 409", async () => {
		const cancel = makeMutation();
		cancel.mutate.mockImplementation((_variables: unknown, handlers: { onError?: (error: unknown) => void }) => {
			handlers.onError?.(new ApiError(409, { title: "One or more errors occurred!" } as ProblemDetails));
		});
		executionHooksMock.useCancelIntegrationExecution.mockReturnValue(cancel);
		renderPage();

		fireEvent.click(screen.getByTestId("integration-execution-cancel-exec-running"));

		await waitFor(() => {
			expect(toastMock.error).toHaveBeenCalled();
		});
		expect(toastMock.error).toHaveBeenCalledWith(expect.stringContaining("already finished"));
	});

	it("falls back to the generic cancel failure on any other status", async () => {
		const cancel = makeMutation();
		cancel.mutate.mockImplementation((_variables: unknown, handlers: { onError?: (error: unknown) => void }) => {
			handlers.onError?.(new ApiError(500, {} as ProblemDetails));
		});
		executionHooksMock.useCancelIntegrationExecution.mockReturnValue(cancel);
		renderPage();

		fireEvent.click(screen.getByTestId("integration-execution-cancel-exec-running"));

		await waitFor(() => {
			expect(toastMock.error).toHaveBeenCalled();
		});
		expect(toastMock.error).toHaveBeenCalledWith("Could not cancel the execution.");
	});

	it("does not cancel when the confirmation is declined", async () => {
		const cancel = makeMutation();
		executionHooksMock.useCancelIntegrationExecution.mockReturnValue(cancel);
		confirmMock.mockResolvedValue(false);
		renderPage();

		fireEvent.click(screen.getByTestId("integration-execution-cancel-exec-running"));

		await waitFor(() => {
			expect(confirmMock).toHaveBeenCalledTimes(1);
		});
		expect(cancel.mutate).not.toHaveBeenCalled();
	});

	it("renders a dash for started and duration on the two terminal edges that never ran", () => {
		renderPage();

		for (const id of ["exec-cancelled", "exec-approval"]) {
			const cells = Array.from(screen.getByTestId(`integration-execution-row-${id}`).querySelectorAll("td")).map(
				(cell) => cell.textContent,
			);
			// Columns: trigger, session, execution, status, received, started, ended, duration, outputs, actions.
			expect(cells[5]).toBe("—");
			expect(cells[7]).toBe("—");
		}
	});

	it("renders every failure category verbatim, including one it does not recognise", () => {
		renderPage();

		expect(screen.getByTestId("integration-execution-category-exec-approval").textContent).toBe("approval-required");
		expect(screen.getByTestId("integration-execution-category-exec-timeout").textContent).toBe("queue-timeout");
		expect(screen.getByTestId("integration-execution-category-exec-policy").textContent).toBe("session-policy");
		expect(screen.getByTestId("integration-execution-category-exec-unknown").textContent).toBe(
			"a-category-this-client-has-never-seen",
		);
		expect(screen.queryByTestId("integration-execution-category-exec-completed")).toBeNull();
	});

	// D-2: the response now carries `totalCount`, so the page states the real size of the filtered table instead of
	// describing a window it could not number.
	it("shows the server's total and asks for the first page by default", () => {
		executionHooksMock.useIntegrationExecutions.mockReturnValue(makeListQuery(executions, 412));
		renderPage();

		expect(screen.getByTestId("integration-executions-pagination-range").textContent).toContain("412");
		expect(lastExecutionWindow()).toEqual({ refetchInterval: 5000, limit: 50, offset: 0 });
		expect(screen.queryByTestId("integration-executions-window-note")).toBeNull();
	});

	// The pager bounce: the page-2 cache entry starts empty, and a hook that answered `undefined` there let the total
	// read as 0, the page count collapse to 1 and the clamp send the operator straight back to page 1. The hooks hold
	// the previous page over (`placeholderData: keepPreviousData`), which is the steady total these mocks stand in for.
	it("stays on the next page once it is asked for, and never re-asks for the first one", async () => {
		executionHooksMock.useIntegrationExecutions.mockReturnValue(makeListQuery(executions, 412));
		const { repoll } = renderPage();

		fireEvent.click(within(screen.getByTestId("integration-executions-pagination-controls")).getByText("2"));

		await waitFor(() => {
			expect(lastExecutionWindow().offset).toBe(50);
		});
		expect(lastExecutionWindow().limit).toBe(50);

		// The page-2 response lands, and the poll that follows it must not move the operator either.
		repoll();

		const offsets = requestedOffsets();
		expect(offsets.slice(offsets.indexOf(50))).toEqual(offsets.slice(offsets.indexOf(50)).map(() => 50));
		expect(
			within(screen.getByTestId("integration-executions-pagination-controls")).getByText("2").getAttribute("data-active"),
		).toBe("true");
	});

	// A narrowed filter is a DIFFERENT list, so page 4 of the old one means nothing against it.
	it("returns to the first page when a filter changes", async () => {
		executionHooksMock.useIntegrationExecutions.mockReturnValue(makeListQuery(executions, 412));
		renderPage();

		fireEvent.click(within(screen.getByTestId("integration-executions-pagination-controls")).getByText("3"));
		await waitFor(() => {
			expect(lastExecutionWindow().offset).toBe(100);
		});

		await clickStatusChip("Failed", ["Failed"]);

		expect(lastExecutionWindow().offset).toBe(0);
	});

	// The one case the pager has to recover from on its own: rows disappear under it (a session delete cascades) and
	// the active page no longer exists.
	it("falls back to the last page that still exists when the total shrinks below it", async () => {
		executionHooksMock.useIntegrationExecutions.mockReturnValue(makeListQuery(executions, 412));
		const { repoll } = renderPage();

		fireEvent.click(within(screen.getByTestId("integration-executions-pagination-controls")).getByText("5"));
		await waitFor(() => {
			expect(lastExecutionWindow().offset).toBe(200);
		});

		executionHooksMock.useIntegrationExecutions.mockReturnValue(makeListQuery(executions, 60));
		repoll();

		await waitFor(() => {
			expect(lastExecutionWindow().offset).toBe(50);
		});
	});

	it("opens the detail dialog from the row's eye action", async () => {
		renderPage();

		fireEvent.click(screen.getByTestId("integration-execution-view-exec-completed"));

		await waitFor(() => {
			expect(screen.getByTestId("integration-execution-detail")).toBeTruthy();
		});
		expect(executionHooksMock.useIntegrationExecutionEvents).toHaveBeenCalledWith("exec-completed", {
			refetchInterval: 5000,
		});
	});

	// R1-11: the list poll is UNCONDITIONAL. Gating it on "any row is active" would read the very list a poll has to
	// fetch, so a fresh node with an empty or all-terminal window would never discover a run started elsewhere.
	it("polls the executions list even when the window is empty, which is where gating it would break", () => {
		executionHooksMock.useIntegrationExecutions.mockReturnValue(makeListQuery([]));
		renderPage();

		expect(executionHooksMock.useIntegrationExecutions).toHaveBeenCalledWith(
			expect.anything(),
			expect.objectContaining({ refetchInterval: 5000 }),
		);
	});

	it("polls the per-execution read while the run is still active", async () => {
		renderPage();

		fireEvent.click(screen.getByTestId("integration-execution-view-exec-running"));

		await waitFor(() => {
			expect(screen.getByTestId("integration-execution-detail")).toBeTruthy();
		});
		expect(executionHooksMock.useIntegrationExecution).toHaveBeenCalledWith("exec-running", {
			refetchInterval: 5000,
		});
	});

	it("leaves the per-execution read unpolled once the run is terminal", async () => {
		renderPage();

		fireEvent.click(screen.getByTestId("integration-execution-view-exec-completed"));

		await waitFor(() => {
			expect(screen.getByTestId("integration-execution-detail")).toBeTruthy();
		});
		expect(executionHooksMock.useIntegrationExecution).toHaveBeenCalledWith("exec-completed", {
			refetchInterval: undefined,
		});
	});

	it("keeps the open dialog when the next poll returns a window without its row", async () => {
		executionHooksMock.useIntegrationExecution.mockReturnValue(makeQuery(detail));
		const { repoll } = renderPage();

		fireEvent.click(screen.getByTestId("integration-execution-view-exec-completed"));
		await waitFor(() => {
			expect(screen.getByTestId("integration-execution-detail")).toBeTruthy();
		});

		executionHooksMock.useIntegrationExecutions.mockReturnValue(
			makeListQuery(executions.filter((row) => row.id !== "exec-completed")),
		);
		repoll();

		expect(screen.queryByTestId("integration-execution-row-exec-completed")).toBeNull();
		expect(screen.getByTestId("integration-execution-detail")).toBeTruthy();
		expect(screen.getByTestId("integration-execution-request-id").textContent).toBe("req-1");
	});

	it("says so when the per-execution read fails, rather than just omitting the principal", async () => {
		executionHooksMock.useIntegrationExecution.mockReturnValue({
			data: undefined,
			isLoading: false,
			error: new Error("boom"),
		});
		renderPage();

		fireEvent.click(screen.getByTestId("integration-execution-view-exec-completed"));

		await waitFor(() => {
			expect(screen.getByTestId("integration-execution-detail-error")).toBeTruthy();
		});
		expect(screen.queryByTestId("integration-execution-principal")).toBeNull();
	});

	it("names the status chip group for a screen reader", () => {
		renderPage();

		const group = screen.getByTestId("integration-executions-status-chips");
		expect(group.getAttribute("role")).toBe("group");
		expect(group.getAttribute("aria-label")).toBe("Filter executions by status");
	});

	it("sends the trigger and session filters to the query", async () => {
		sessionHooksMock.useIntegrationSessions.mockReturnValue(
			makeListQuery([
				{
					id: "session-1",
					triggerId: "trigger-1",
					triggerName: "Sensor hub",
					principalId: "77777777-7777-4777-8777-777777777777",
					agentDefinitionId: "agent-1",
					status: "Active",
					createdAtUtc: 1000,
					lastActivityUtc: 2000,
					executionCount: 1,
				},
			]),
		);
		renderPage();

		fireEvent.click(screen.getByTestId("integration-executions-filter-trigger"));
		fireEvent.click(await screen.findByRole("option", { name: "Sensor hub", hidden: true }));
		await waitFor(() => {
			expect(lastExecutionFilters().triggerId).toBe("trigger-1");
		});

		fireEvent.click(screen.getByTestId("integration-executions-filter-session"));
		fireEvent.click(await screen.findByRole("option", { name: "session-1", hidden: true }));
		await waitFor(() => {
			expect(lastExecutionFilters().sessionId).toBe("session-1");
		});
	});
});
