// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ComponentProps } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { PreviewWorkflowDetail, PreviewWorkflowGraph } from "@/features/preview/models/PreviewWorkflowModels";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, defaultValue?: string) => defaultValue ?? _key }),
}));

// The hub side-effect and the confirm dialog are irrelevant to the Execute-path decision under test.
vi.mock("@/features/preview/hooks/usePreviewWorkflowHub", () => ({ usePreviewWorkflowHub: vi.fn() }));
vi.mock("@/core/ui/hooks/useConfirm", () => ({ useConfirm: () => ({ confirm: vi.fn() }) }));

// Replace the React Flow canvas with a stub that exposes an Execute button which fires onExecute with a graph the
// test controls. This isolates the page's run-control decision (which execute mutation to call) from React Flow.
let canvasGraph: PreviewWorkflowGraph;
vi.mock("@/features/preview/components/WorkflowCanvas", () => ({
	WorkflowCanvas: ({ onExecute }: { onExecute: (graph: PreviewWorkflowGraph) => void }) => (
		<button type="button" data-testid="stub-execute" onClick={() => onExecute(canvasGraph)}>
			execute
		</button>
	),
}));

const { hooksMock, storeMock, runStoreMock } = vi.hoisted(() => ({
	hooksMock: {
		usePreviewWorkflows: vi.fn(),
		usePreviewWorkflow: vi.fn(),
		useCreatePreviewWorkflow: vi.fn(),
		useUpdatePreviewWorkflow: vi.fn(),
		useDeletePreviewWorkflow: vi.fn(),
		useExecuteSavedPreviewWorkflow: vi.fn(),
		useExecuteUnsavedPreviewWorkflow: vi.fn(),
		useContinuePreviewRun: vi.fn(),
		useCancelPreviewRun: vi.fn(),
		useCancelAllPreviewRuns: vi.fn(),
		usePreviewRuns: vi.fn(),
		usePreviewRun: vi.fn(),
	},
	storeMock: vi.fn(),
	runStoreMock: vi.fn(),
}));

vi.mock("@/features/preview/queries/usePreviewWorkflows", () => hooksMock);
vi.mock("@/features/preview/stores/PreviewManagementStore", () => ({
	usePreviewManagementStore: (selector: (state: unknown) => unknown) => selector(storeMock()),
}));
vi.mock("@/features/preview/stores/PreviewRunStore", () => ({
	usePreviewRunStore: (selector: (state: unknown) => unknown) => selector(runStoreMock()),
}));

import { PreviewPage } from "@/features/preview/pages/PreviewPage";

const PERSISTED_GRAPH: PreviewWorkflowGraph = {
	startText: "hello",
	nodes: [
		{ id: "start", kind: "Start" },
		{ id: "agent", kind: "Agent", label: "A", model: "qwen3:8b", instructions: "Respond." },
		{ id: "end", kind: "End" },
	],
	edges: [
		{ sourceId: "start", targetId: "agent" },
		{ sourceId: "agent", targetId: "end" },
	],
};

const DETAIL: PreviewWorkflowDetail = {
	id: "wf-1",
	name: "Workflow",
	version: 3,
	graph: PERSISTED_GRAPH,
	createdAtUtc: 0,
	updatedAtUtc: 0,
};

const executeSaved = vi.fn();
const executeUnsaved = vi.fn();
const registerRun = vi.fn();
const onRouteRunIdChange = vi.fn();

function idleMutation(mutate: ReturnType<typeof vi.fn>) {
	return { mutate, isPending: false } as const;
}

function renderOpenWorkflow(props: Partial<ComponentProps<typeof PreviewPage>> = {}): void {
	render(
		<MantineProvider>
			<PreviewPage {...props} />
		</MantineProvider>,
	);
}

// Mantine reads window.matchMedia; jsdom does not provide it, so stub it (mirrors the canvas test).
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
}

describe("PreviewPage Execute path", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		executeSaved.mockReset();
		executeUnsaved.mockReset();
		registerRun.mockReset();
		onRouteRunIdChange.mockReset();

		storeMock.mockReturnValue({
			canvasTarget: { mode: "open", id: "wf-1" },
			actions: { openNew: vi.fn(), openWorkflow: vi.fn(), closeCanvas: vi.fn() },
		});
		runStoreMock.mockReturnValue({
			runs: {},
			actions: { registerRun, reset: vi.fn(), markCancelled: vi.fn() },
		});

		hooksMock.usePreviewWorkflows.mockReturnValue({ data: [], isLoading: false });
		hooksMock.usePreviewWorkflow.mockReturnValue({ data: DETAIL, isLoading: false });
		hooksMock.useCreatePreviewWorkflow.mockReturnValue(idleMutation(vi.fn()));
		hooksMock.useUpdatePreviewWorkflow.mockReturnValue(idleMutation(vi.fn()));
		hooksMock.useDeletePreviewWorkflow.mockReturnValue(idleMutation(vi.fn()));
		hooksMock.useExecuteSavedPreviewWorkflow.mockReturnValue(idleMutation(executeSaved));
		hooksMock.useExecuteUnsavedPreviewWorkflow.mockReturnValue(idleMutation(executeUnsaved));
		hooksMock.useContinuePreviewRun.mockReturnValue(idleMutation(vi.fn()));
		hooksMock.useCancelPreviewRun.mockReturnValue(idleMutation(vi.fn()));
		hooksMock.useCancelAllPreviewRuns.mockReturnValue(idleMutation(vi.fn()));
		hooksMock.usePreviewRuns.mockReturnValue({ data: [], isLoading: false });
		hooksMock.usePreviewRun.mockReturnValue({ data: undefined, isSuccess: false });
	});

	afterEach(cleanup);

	it("executes a saved workflow by id when the canvas is pristine (matches the persisted graph)", () => {
		canvasGraph = PERSISTED_GRAPH;
		renderOpenWorkflow();

		fireEvent.click(screen.getByTestId("stub-execute"));

		expect(executeSaved).toHaveBeenCalledTimes(1);
		expect(executeSaved.mock.calls[0]?.[0]).toBe("wf-1");
		expect(executeUnsaved).not.toHaveBeenCalled();
	});

	it("executes the inline graph (unsaved path) when the open canvas has unsaved edits", () => {
		// Same workflow, but the live graph differs from the persisted detail (an edited instruction).
		canvasGraph = {
			...PERSISTED_GRAPH,
			nodes: PERSISTED_GRAPH.nodes.map((node) =>
				node.kind === "Agent" ? { ...node, instructions: "Edited instruction." } : node,
			),
		};
		renderOpenWorkflow();

		fireEvent.click(screen.getByTestId("stub-execute"));

		expect(executeUnsaved).toHaveBeenCalledTimes(1);
		expect(executeUnsaved.mock.calls[0]?.[0]).toEqual(canvasGraph);
		expect(executeSaved).not.toHaveBeenCalled();
	});

	it("puts the new runId in the route so a reload can find the run again", () => {
		// The reload leak, client side: before this the runId lived only in page state, so reloading abandoned the run
		// on the node — unreachable and (parked on Pause) holding its concurrency slot until a node restart.
		canvasGraph = PERSISTED_GRAPH;
		executeSaved.mockImplementation((_id: string, handlers: { onSuccess: (data: { runId: string }) => void }) => {
			handlers.onSuccess({ runId: "run-99" });
		});
		renderOpenWorkflow({ onRouteRunIdChange });

		fireEvent.click(screen.getByTestId("stub-execute"));

		expect(onRouteRunIdChange).toHaveBeenCalledWith("run-99");
	});

	it("reattaches to the runId carried in the route on load", () => {
		// A reloaded tab knows nothing except the id in its URL. Registering it is the whole reattach: the hub hook
		// then joins the run's group and replays every buffered event the tab has not applied.
		canvasGraph = PERSISTED_GRAPH;
		renderOpenWorkflow({ routeRunId: "run-from-url" });

		expect(registerRun).toHaveBeenCalledWith("run-from-url");
	});

	it("drops a route runId the node no longer knows about", () => {
		// A 404 from GET preview/runs/{id} resolves to null: the run was swept, cancelled, or aged out of the replay
		// window, so the URL must stop advertising a run nobody can act on.
		canvasGraph = PERSISTED_GRAPH;
		hooksMock.usePreviewRun.mockReturnValue({ data: null, isSuccess: true });

		renderOpenWorkflow({ routeRunId: "run-gone", onRouteRunIdChange });

		expect(onRouteRunIdChange).toHaveBeenCalledWith(null);
	});

	it("keeps a route runId the node still knows about", () => {
		// The discriminating other half: a live run must NOT be cleared out of the route.
		canvasGraph = PERSISTED_GRAPH;
		hooksMock.usePreviewRun.mockReturnValue({
			data: { runId: "run-live", state: "Paused", isLive: true, startedAtUtc: 0, lastSeq: 3, subscriberCount: 0 },
			isSuccess: true,
		});

		renderOpenWorkflow({ routeRunId: "run-live", onRouteRunIdChange });

		expect(registerRun).toHaveBeenCalledWith("run-live");
		expect(onRouteRunIdChange).not.toHaveBeenCalledWith(null);
	});
});
