import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { PreviewPagePresentation } from "@/features/preview/components/PreviewPagePresentation";
import { usePreviewWorkflowHub } from "@/features/preview/hooks/usePreviewWorkflowHub";
import { graphsEqual, graphToCanvas } from "@/features/preview/models/PreviewCanvasModels";
import type {
	PreviewRunStartedResponse,
	PreviewWorkflowGraph,
	PreviewWorkflowSummary,
} from "@/features/preview/models/PreviewWorkflowModels";
import {
	useCancelAllPreviewRuns,
	useCancelPreviewRun,
	useContinuePreviewRun,
	useCreatePreviewWorkflow,
	useDeletePreviewWorkflow,
	useExecuteSavedPreviewWorkflow,
	useExecuteUnsavedPreviewWorkflow,
	usePreviewRun,
	usePreviewRuns,
	usePreviewWorkflow,
	usePreviewWorkflows,
	useUpdatePreviewWorkflow,
} from "@/features/preview/queries/usePreviewWorkflows";
import { usePreviewManagementStore } from "@/features/preview/stores/PreviewManagementStore";
import { usePreviewRunStore } from "@/features/preview/stores/PreviewRunStore";

// Falls back when `error` isn't an Error OR its message is blank (e.g. a 404 ApiError built from a ProblemDetails
// with no title/detail — `error.message === ""` is truthy-checked-as-Error but would otherwise render an empty
// toast).
const errorMessage = (error: unknown, fallback: string): string =>
	error instanceof Error && error.message ? error.message : fallback;

// The empty graph a fresh canvas starts from: a Start and an End block, no agents (the canvas is invalid until
// the operator adds an agent between them — the Execute button stays disabled accordingly).
const EMPTY_GRAPH: PreviewWorkflowGraph = {
	startText: "",
	nodes: [
		{ id: "start", kind: "Start" },
		{ id: "end", kind: "End" },
	],
	edges: [],
};

interface PreviewPageProps {
	// The runId carried in the route's search params. On load this is what a reloaded tab reattaches to — the run
	// itself lives on the node, so the id in the URL is the whole of what has to survive the reload.
	readonly routeRunId?: string | null;
	// Writes the active runId back into the route (null clears it). Supplied by the route component; omitted in tests
	// and anywhere the page is rendered outside the router.
	readonly onRouteRunIdChange?: (runId: string | null) => void;
}

export function PreviewPage({ routeRunId = null, onRouteRunIdChange }: PreviewPageProps = {}) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	// Live run output over the preview hub (the store applies events only for runs this tab started).
	usePreviewWorkflowHub();

	const canvasTarget = usePreviewManagementStore((state) => state.canvasTarget);
	const openNew = usePreviewManagementStore((state) => state.actions.openNew);
	const openWorkflow = usePreviewManagementStore((state) => state.actions.openWorkflow);
	const closeCanvas = usePreviewManagementStore((state) => state.actions.closeCanvas);

	const runActions = usePreviewRunStore((state) => state.actions);

	// The run this tab's canvas is currently displaying (most-recently started). Lives in page state, not the
	// store, because it is a view choice (which of the tracked runs to show), not run output.
	const [activeRunId, setActiveRunId] = useState<string | null>(null);
	const activeRun = usePreviewRunStore((state) => (activeRunId ? state.runs[activeRunId] : undefined));

	// The canvas's current live graph (lifted up via onGraphChange) so Save persists the latest edits. null until
	// the canvas mounts and emits its first graph.
	const [liveGraph, setLiveGraph] = useState<PreviewWorkflowGraph | null>(null);

	const workflowsQuery = usePreviewWorkflows();
	const openId = canvasTarget?.mode === "open" ? canvasTarget.id : null;
	const detailQuery = usePreviewWorkflow(openId);

	const createMutation = useCreatePreviewWorkflow();
	const updateMutation = useUpdatePreviewWorkflow();
	const deleteMutation = useDeletePreviewWorkflow();
	const executeSavedMutation = useExecuteSavedPreviewWorkflow();
	const executeUnsavedMutation = useExecuteUnsavedPreviewWorkflow();
	const continueMutation = useContinuePreviewRun();
	const cancelMutation = useCancelPreviewRun();
	const cancelAllMutation = useCancelAllPreviewRuns();

	// Runs the NODE knows about — the only place a run orphaned by a reload is visible. Polled only while the list is
	// showing (the panel lives there), so an open canvas does not pay for it.
	const runsQuery = usePreviewRuns(canvasTarget === null);
	// Does the runId carried in the route still point at something? null (a 404) means "gone", so the stale id is
	// dropped out of the route rather than left there advertising a run nobody can act on.
	const routeRunQuery = usePreviewRun(routeRunId);

	// On unmount: close the canvas and clear ALL run output so a reload/navigation starts EMPTY (decision: the
	// run-output store is empty on mount; nothing is persisted).
	useEffect(() => {
		return () => {
			closeCanvas();
			runActions.reset();
		};
	}, [closeCanvas, runActions]);

	// Reattach. The run lives on the node and the server keeps a seq-numbered replay log for exactly this case, so
	// registering the runId is all it takes: the hub hook joins the run's group and asks for every event after the
	// highest seq this tab has applied (-1 for a fresh page → the whole log), and the store's seq dedupe makes a
	// replayed-and-live event apply exactly once. Before this, the runId lived only in page state and a reload lost
	// the run permanently — including the result of a run the operator had already paid GPU time for.
	useEffect(() => {
		if (routeRunId === null) {
			return;
		}
		runActions.registerRun(routeRunId);
		setActiveRunId(routeRunId);
	}, [routeRunId, runActions]);

	// A runId in the route that the node no longer knows about is stale (swept, cancelled, or past the replay
	// window) — clear it so the URL stops pointing at nothing.
	useEffect(() => {
		if (routeRunId !== null && routeRunQuery.isSuccess && routeRunQuery.data === null) {
			onRouteRunIdChange?.(null);
		}
	}, [routeRunId, routeRunQuery.isSuccess, routeRunQuery.data, onRouteRunIdChange]);

	const handleReattach = useCallback(
		(runId: string) => {
			runActions.registerRun(runId);
			setActiveRunId(runId);
			onRouteRunIdChange?.(runId);
		},
		[onRouteRunIdChange, runActions],
	);

	const handleCancelRun = useCallback(
		(runId: string) => {
			cancelMutation.mutate(runId, {
				onSuccess: () => runActions.markCancelled(runId),
				onError: (error) => toast.error(errorMessage(error, t("pages.preview.errors.cancel", "Could not cancel the run."))),
			});
		},
		[cancelMutation, runActions, t],
	);

	const handleCancelAll = useCallback(() => {
		cancelAllMutation.mutate(undefined, {
			onSuccess: (result) =>
				toast.success(t("pages.preview.runs.cancelledCount", "Cancelled {{count}} run(s).", { count: result.cancelledCount })),
			onError: (error) => toast.error(errorMessage(error, t("pages.preview.errors.cancelAll", "Could not cancel the runs."))),
		});
	}, [cancelAllMutation, t]);

	const workflows = useMemo(() => workflowsQuery.data ?? [], [workflowsQuery.data]);

	// The graph the canvas opens with: an opened workflow's stored graph, or the empty Start→End scaffold for a
	// new one. Keyed via React `key` on the canvas so opening a different workflow remounts it with fresh state.
	const canvasGraph = useMemo<PreviewWorkflowGraph>(() => {
		if (canvasTarget?.mode === "open") {
			return detailQuery.data?.graph ?? EMPTY_GRAPH;
		}
		return EMPTY_GRAPH;
	}, [canvasTarget, detailQuery.data]);

	const { nodes: initialNodes, edges: initialEdges } = useMemo(() => graphToCanvas(canvasGraph), [canvasGraph]);

	const isControlBusy =
		executeSavedMutation.isPending || executeUnsavedMutation.isPending || continueMutation.isPending || cancelMutation.isPending;

	const handleDelete = useCallback(
		async (workflow: PreviewWorkflowSummary) => {
			const confirmed = await confirm({
				title: t("pages.preview.delete.title", "Delete workflow"),
				description: t("pages.preview.delete.description", "Delete this workflow? This cannot be undone."),
				confirmationText: t("common.delete", "Delete"),
				cancellationText: t("common.cancel", "Cancel"),
			});
			if (!confirmed) {
				return;
			}
			deleteMutation.mutate(workflow.id, {
				onError: (error) => toast.error(errorMessage(error, t("pages.preview.errors.delete", "Could not delete the workflow."))),
			});
		},
		[confirm, deleteMutation, t],
	);

	// Execute the CURRENT canvas graph. The execute path is chosen so unsaved edits always take effect:
	//   - A new (never-saved) canvas executes its inline graph (executeUnsaved) — nothing is persisted.
	//   - An opened (saved) workflow whose live graph still matches the persisted detail executes by id
	//     (executeSaved) — runs the persisted graph, the cheap/canonical path.
	//   - An opened workflow with unsaved edits (live graph DIFFERS from the loaded detail) executes its inline
	//     graph (executeUnsaved) so the operator's pending edits run instead of being silently discarded.
	// Either way the returned runId is registered with the store (so its hub events are applied) and set as the
	// canvas's active run.
	const handleExecute = useCallback(
		(graph: PreviewWorkflowGraph) => {
			const onStarted = (runId: string): void => {
				runActions.registerRun(runId);
				setActiveRunId(runId);
				// Put the runId in the URL so a reload can reattach to this run instead of abandoning it. This is the
				// half of the fix that lives in the client: the server already keeps the replay log, but nothing could
				// reach it once the id existed only in page state.
				onRouteRunIdChange?.(runId);
			};
			const onExecuteError = (error: unknown): void =>
				toast.error(errorMessage(error, t("pages.preview.errors.execute", "Could not start the run.")));
			const onResult = {
				onSuccess: (data: PreviewRunStartedResponse) => onStarted(data.runId),
				onError: onExecuteError,
			};

			const persistedGraph = canvasTarget?.mode === "open" ? detailQuery.data?.graph : undefined;
			const isPristineSaved = persistedGraph !== undefined && graphsEqual(graph, persistedGraph);

			if (canvasTarget?.mode === "open" && isPristineSaved) {
				executeSavedMutation.mutate(canvasTarget.id, onResult);
				return;
			}
			executeUnsavedMutation.mutate(graph, onResult);
		},
		[canvasTarget, detailQuery.data, executeSavedMutation, executeUnsavedMutation, onRouteRunIdChange, runActions, t],
	);

	const handleContinue = useCallback(() => {
		if (activeRunId === null) {
			return;
		}
		continueMutation.mutate(activeRunId, {
			onError: (error) => toast.error(errorMessage(error, t("pages.preview.errors.continue", "Could not continue the run."))),
		});
	}, [activeRunId, continueMutation, t]);

	const handleCancel = useCallback(() => {
		if (activeRunId === null) {
			return;
		}
		cancelMutation.mutate(activeRunId, {
			// Defense-in-depth: mark the run cancelled locally right away so the Cancel button hides even if the
			// authoritative `runCancelled` hub event is somehow delayed. The hub event still arrives and applies
			// normally (seq-deduped like any other event); this is just an optimistic override of `status`.
			onSuccess: () => runActions.markCancelled(activeRunId),
			onError: (error) => toast.error(errorMessage(error, t("pages.preview.errors.cancel", "Could not cancel the run."))),
		});
	}, [activeRunId, cancelMutation, runActions, t]);

	// Save the current canvas graph. A new workflow is created (prompting for a name via confirm-with-input is out
	// of scope here — a default name is used and the operator renames later); an opened one is updated, carrying
	// the loaded Version for optimistic concurrency. A 409 surfaces the stale-version conflict.
	const handleSave = useCallback(
		(graph: PreviewWorkflowGraph) => {
			if (canvasTarget?.mode === "open") {
				const detail = detailQuery.data;
				if (detail === undefined) {
					return;
				}
				updateMutation.mutate(
					{ workflowId: detail.id, name: detail.name, graph, version: detail.version },
					{
						onSuccess: () => toast.success(t("pages.preview.saved", "Workflow saved.")),
						onError: (error) => {
							if (error instanceof ApiError && error.statusCode === 409) {
								toast.error(
									t("pages.preview.errors.conflict", "This workflow changed elsewhere. Reload and reapply your edits."),
								);
								return;
							}
							toast.error(errorMessage(error, t("pages.preview.errors.save", "Could not save the workflow.")));
						},
					},
				);
				return;
			}
			createMutation.mutate(
				{ name: t("pages.preview.defaultName", "Untitled workflow"), graph },
				{
					onSuccess: (detail) => {
						toast.success(t("pages.preview.saved", "Workflow saved."));
						openWorkflow(detail.id);
					},
					onError: (error) => toast.error(errorMessage(error, t("pages.preview.errors.save", "Could not save the workflow."))),
				},
			);
		},
		[canvasTarget, createMutation, detailQuery.data, openWorkflow, t, updateMutation],
	);

	const isCanvasOpen = canvasTarget !== null;
	const isSaving = createMutation.isPending || updateMutation.isPending;
	// Save persists the canvas's CURRENT live graph (the canvas lifts it up via onGraphChange). Falls back to the
	// initial graph until the first change fires.
	const handleSaveCurrent = useCallback(() => handleSave(liveGraph ?? canvasGraph), [handleSave, liveGraph, canvasGraph]);

	return (
		<PreviewPagePresentation
			t={t}
			actions={{
				isCanvasOpen,
				closeCanvas,
				openNew,
				isSaving,
				onSave: handleSaveCurrent,
			}}
			canvas={{
				openId,
				detailLoading: detailQuery.isLoading,
				activeRunId,
				initialNodes,
				initialEdges,
				graph: canvasGraph,
				activeRunStatus: activeRun?.status,
				isControlBusy,
				onExecute: handleExecute,
				onCancel: handleCancel,
				onContinue: handleContinue,
				onGraphChange: setLiveGraph,
			}}
			list={{
				workflowsLoading: workflowsQuery.isLoading,
				runs: runsQuery.data ?? [],
				isCancellingRuns: cancelMutation.isPending || cancelAllMutation.isPending,
				onReattach: handleReattach,
				onCancelRun: handleCancelRun,
				onCancelAll: handleCancelAll,
				workflows,
				isDeletingWorkflow: deleteMutation.isPending,
				onOpenWorkflow: openWorkflow,
				onDeleteWorkflow: handleDelete,
			}}
		/>
	);
}
