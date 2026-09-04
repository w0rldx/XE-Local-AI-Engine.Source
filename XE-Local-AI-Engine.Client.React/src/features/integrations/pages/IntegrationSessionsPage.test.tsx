// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import type {
	IntegrationExecution,
	IntegrationSession,
	IntegrationSessionFilters,
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

const { sessionHooksMock, executionHooksMock, triggerHooksMock, confirmMock } = vi.hoisted(() => ({
	sessionHooksMock: { useIntegrationSessions: vi.fn(), useDeleteIntegrationSession: vi.fn() },
	executionHooksMock: {
		useIntegrationExecutions: vi.fn(),
		useIntegrationExecution: vi.fn(),
		useIntegrationExecutionEvents: vi.fn(),
		useCancelIntegrationExecution: vi.fn(),
	},
	triggerHooksMock: { useIntegrationTriggers: vi.fn() },
	confirmMock: vi.fn(),
}));

vi.mock("@/features/integrations/queries/useIntegrationSessions", () => sessionHooksMock);
vi.mock("@/features/integrations/queries/useIntegrationExecutions", () => executionHooksMock);
vi.mock("@/features/integrations/queries/useIntegrationTriggers", () => triggerHooksMock);
vi.mock("@/core/ui/hooks/useConfirm", () => ({ useConfirm: () => ({ confirm: confirmMock }) }));

import { IntegrationSessionsPage } from "@/features/integrations/pages/IntegrationSessionsPage";

const triggers: IntegrationTrigger[] = [
	{
		id: "trigger-1",
		name: "sensor-hub",
		displayName: "Sensor hub",
		description: "",
		enabled: true,
		targetAgentDefinitionId: "agent-1",
		sessionPolicy: "CallerManaged",
		acceptedInputKinds: ["text"],
		createdAtUtc: 1000,
		updatedAtUtc: 1000,
		version: 1,
	},
];

const activeSession: IntegrationSession = {
	id: "5c0ffee0-0000-0000-0000-000000000001",
	triggerId: "trigger-1",
	triggerName: "Sensor hub",
	principalId: "77777777-7777-4777-8777-777777777777",
	agentDefinitionId: "agent-1",
	status: "Active",
	createdAtUtc: 1_700_000_000_000,
	lastActivityUtc: 1_700_000_010_000,
	executionCount: 2,
};

/** The trigger behind this one was deleted, which is the only case `triggerName` is empty. */
const closedSession: IntegrationSession = {
	id: "5c0ffee0-0000-0000-0000-000000000002",
	triggerId: "trigger-1",
	triggerName: "",
	principalId: "88888888-8888-4888-8888-888888888888",
	agentDefinitionId: "agent-1",
	status: "Closed",
	createdAtUtc: 1_700_000_020_000,
	lastActivityUtc: 1_700_000_030_000,
	executionCount: 0,
};

// Deliberately NOT ordered by last activity: the server orders this list, so the table must render it as received.
const sessions: IntegrationSession[] = [activeSession, closedSession];

const sessionExecutions: IntegrationExecution[] = [
	{
		id: "exec-1",
		triggerId: "trigger-1",
		sessionId: activeSession.id,
		status: "Completed",
		receivedAtUtc: 1_700_000_001_000,
		startedAtUtc: 1_700_000_002_000,
		endedAtUtc: 1_700_000_004_000,
		failureCategory: null,
		failureSummary: null,
		outputCount: 1,
	},
];

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
	return render(
		<MantineProvider>
			<ConfirmProvider>
				<QueryClientProvider client={queryClient}>
					<IntegrationSessionsPage />
				</QueryClientProvider>
			</ConfirmProvider>
		</MantineProvider>,
	);
}

/** The filters the page most recently asked the sessions list for. */
function lastSessionFilters(): IntegrationSessionFilters {
	const calls = sessionHooksMock.useIntegrationSessions.mock.calls;
	return (calls.at(-1)?.[0] ?? {}) as IntegrationSessionFilters;
}

/** The paging window the page most recently asked the sessions list for. */
function lastSessionWindow(): { limit?: number; offset?: number } {
	const calls = sessionHooksMock.useIntegrationSessions.mock.calls;
	return (calls.at(-1)?.[1] ?? {}) as { limit?: number; offset?: number };
}

/** Every offset the page has asked for, in order — one entry per render, so repeats are expected and a CHANGE is not. */
function requestedOffsets(): number[] {
	return sessionHooksMock.useIntegrationSessions.mock.calls.map(
		(call) => ((call[1] ?? {}) as { offset?: number }).offset ?? 0,
	);
}

describe("IntegrationSessionsPage", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		useIntegrationsUiStore.setState({ selectedExecutionId: null, selectedSessionId: null });
		sessionHooksMock.useIntegrationSessions.mockReturnValue(makeListQuery(sessions));
		sessionHooksMock.useDeleteIntegrationSession.mockReturnValue(makeMutation());
		executionHooksMock.useIntegrationExecutions.mockReturnValue(makeListQuery(sessionExecutions));
		triggerHooksMock.useIntegrationTriggers.mockReturnValue(makeQuery(triggers));
		confirmMock.mockResolvedValue(true);
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders rows in the order the response supplied them, and names a deleted trigger", () => {
		renderPage();

		const rendered = Array.from(screen.getByTestId("integration-sessions-table").querySelectorAll("tbody tr")).map(
			(row) => row.getAttribute("data-testid"),
		);
		expect(rendered).toEqual(sessions.map((row) => `integration-session-row-${row.id}`));

		const closedRow = screen.getByTestId(`integration-session-row-${closedSession.id}`);
		expect(within(closedRow).getByText("Deleted trigger")).toBeTruthy();
	});

	it("names the owning integrator on the row and in the detail, shortened", async () => {
		renderPage();

		// The principal, not the agent: a key rotation keeps the principal, so this is the identity an operator
		// matches against the keys page.
		const cell = screen.getByTestId(`integration-session-principal-${activeSession.id}`);
		expect(cell.textContent).toBe(activeSession.principalId.slice(0, 8));
		expect(cell.getAttribute("title")).toBe(activeSession.principalId);

		fireEvent.click(screen.getByTestId(`integration-session-view-${activeSession.id}`));

		await waitFor(() => {
			expect(screen.getByTestId("integration-session-principal")).toBeTruthy();
		});
		expect(screen.getByTestId("integration-session-principal").getAttribute("title")).toBe(activeSession.principalId);
	});

	it("sends the trigger filter to the request rather than narrowing the window", async () => {
		renderPage();

		expect(lastSessionFilters().triggerId).toBeUndefined();

		fireEvent.click(screen.getByTestId("integration-sessions-filter-trigger"));
		fireEvent.click(await screen.findByRole("option", { name: "Sensor hub", hidden: true }));

		await waitFor(() => {
			expect(lastSessionFilters().triggerId).toBe("trigger-1");
		});
	});

	it("sends the status filter to the request, which is what makes it server-side", async () => {
		renderPage();

		expect(lastSessionFilters().status).toBeUndefined();

		fireEvent.click(screen.getByTestId("integration-sessions-filter-status"));
		fireEvent.click(await screen.findByRole("option", { name: "Closed", hidden: true }));

		await waitFor(() => {
			expect(lastSessionFilters().status).toBe("Closed");
		});
	});

	// D-2: `totalCount` counts every session the filters match, so the pager numbers the whole table rather than the
	// page that happened to load.
	it("shows the server's total and asks for the first page by default", () => {
		sessionHooksMock.useIntegrationSessions.mockReturnValue(makeListQuery(sessions, 130));
		renderPage();

		expect(screen.getByTestId("integration-sessions-pagination-range").textContent).toContain("130");
		expect(lastSessionWindow()).toEqual({ limit: 50, offset: 0 });
		expect(screen.queryByTestId("integration-sessions-window-note")).toBeNull();
	});

	// The pager bounce: the page-2 cache entry starts empty, and a hook that answered `undefined` there let the total
	// read as 0, the page count collapse to 1 and the clamp send the operator straight back to page 1. The hook holds
	// the previous page over (`placeholderData: keepPreviousData`), which is the steady total this mock stands in for.
	it("stays on the next page once it is asked for, and never re-asks for the first one", async () => {
		sessionHooksMock.useIntegrationSessions.mockReturnValue(makeListQuery(sessions, 130));
		renderPage();

		fireEvent.click(within(screen.getByTestId("integration-sessions-pagination-controls")).getByText("2"));

		await waitFor(() => {
			expect(lastSessionWindow().offset).toBe(50);
		});
		expect(lastSessionWindow().limit).toBe(50);

		const offsets = requestedOffsets();
		expect(offsets.slice(offsets.indexOf(50))).toEqual(offsets.slice(offsets.indexOf(50)).map(() => 50));
		expect(
			within(screen.getByTestId("integration-sessions-pagination-controls")).getByText("2").getAttribute("data-active"),
		).toBe("true");
	});

	it("returns to the first page when a filter changes", async () => {
		sessionHooksMock.useIntegrationSessions.mockReturnValue(makeListQuery(sessions, 130));
		renderPage();

		fireEvent.click(within(screen.getByTestId("integration-sessions-pagination-controls")).getByText("3"));
		await waitFor(() => {
			expect(lastSessionWindow().offset).toBe(100);
		});

		fireEvent.click(screen.getByTestId("integration-sessions-filter-status"));
		fireEvent.click(await screen.findByRole("option", { name: "Closed", hidden: true }));

		await waitFor(() => {
			expect(lastSessionWindow().offset).toBe(0);
		});
	});

	it("opens the detail dialog and lists that session's executions", async () => {
		renderPage();

		fireEvent.click(screen.getByTestId(`integration-session-view-${activeSession.id}`));

		await waitFor(() => {
			expect(screen.getByTestId("integration-session-detail")).toBeTruthy();
		});
		expect(screen.getByTestId("integration-session-execution-exec-1")).toBeTruthy();
		// The executions read is scoped by sessionId server-side, not filtered in the dialog.
		expect(executionHooksMock.useIntegrationExecutions).toHaveBeenCalledWith(
			{ sessionId: activeSession.id },
			{ limit: 200 },
		);
	});

	it("confirms before deleting and then deletes that session", async () => {
		const remove = makeMutation();
		sessionHooksMock.useDeleteIntegrationSession.mockReturnValue(remove);
		renderPage();

		fireEvent.click(screen.getByTestId(`integration-session-delete-${activeSession.id}`));

		await waitFor(() => {
			expect(remove.mutate).toHaveBeenCalled();
		});
		expect(confirmMock).toHaveBeenCalledTimes(1);
		expect(remove.mutate.mock.calls.at(0)?.[0]).toEqual({ path: { sessionId: activeSession.id } });
	});

	it("does not delete when the confirmation is declined", async () => {
		const remove = makeMutation();
		sessionHooksMock.useDeleteIntegrationSession.mockReturnValue(remove);
		confirmMock.mockResolvedValue(false);
		renderPage();

		fireEvent.click(screen.getByTestId(`integration-session-delete-${activeSession.id}`));

		await waitFor(() => {
			expect(confirmMock).toHaveBeenCalledTimes(1);
		});
		expect(remove.mutate).not.toHaveBeenCalled();
	});
});
