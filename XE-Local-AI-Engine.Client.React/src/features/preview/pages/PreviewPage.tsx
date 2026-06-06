import { Button, Container, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { IconArrowLeft, IconBinaryTree2, IconDeviceFloppy, IconPlus } from "@tabler/icons-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { PreviewActiveRunContext } from "@/features/preview/components/PreviewActiveRunContext";
import { WorkflowCanvas } from "@/features/preview/components/WorkflowCanvas";
import { WorkflowList } from "@/features/preview/components/WorkflowList";
import { usePreviewWorkflowHub } from "@/features/preview/hooks/usePreviewWorkflowHub";
import { graphsEqual, graphToCanvas } from "@/features/preview/models/PreviewCanvasModels";
import type {
	PreviewRunStartedResponse,
	PreviewWorkflowGraph,
	PreviewWorkflowSummary,
} from "@/features/preview/models/PreviewWorkflowModels";
import {
	useCancelPreviewRun,
	useContinuePreviewRun,
	useCreatePreviewWorkflow,
	useDeletePreviewWorkflow,
	useExecuteSavedPreviewWorkflow,
	useExecuteUnsavedPreviewWorkflow,
	usePreviewWorkflow,
	usePreviewWorkflows,
	useUpdatePreviewWorkflow,
} from "@/features/preview/queries/usePreviewWorkflows";
import { usePreviewManagementStore } from "@/features/preview/stores/PreviewManagementStore";
import { usePreviewRunStore } from "@/features/preview/stores/PreviewRunStore";

const errorMessage = (error: unknown, fallback: string): string => (error instanceof Error ? error.message : fallback);

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

export function PreviewPage() {
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

	// On unmount: close the canvas and clear ALL run output so a reload/navigation starts EMPTY (decision: the
	// run-output store is empty on mount; nothing is persisted).
	useEffect(() => {
		return () => {
			closeCanvas();
			runActions.reset();
		};
		// Stable store action refs.
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, [closeCanvas, runActions]);

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
		executeSavedMutation.isPending ||
		executeUnsavedMutation.isPending ||
		continueMutation.isPending ||
		cancelMutation.isPending;

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
		[canvasTarget, detailQuery.data, executeSavedMutation, executeUnsavedMutation, runActions, t],
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
			onError: (error) => toast.error(errorMessage(error, t("pages.preview.errors.cancel", "Could not cancel the run."))),
		});
	}, [activeRunId, cancelMutation, t]);

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
								toast.error(t("pages.preview.errors.conflict", "This workflow changed elsewhere. Reload and reapply your edits."));
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
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Group justify="space-between" align="flex-start">
					<Stack gap={4}>
						<Text size="sm" tt="uppercase" fw={700} c="dimmed">
							{t("pages.preview.eyebrow", "Worker Node")}
						</Text>
						<Group gap="xs" align="center">
							<IconBinaryTree2 size={24} />
							<Title order={2}>{t("pages.preview.title", "Open Canvas")}</Title>
						</Group>
						<Text c="dimmed">
							{t(
								"pages.preview.subtitle",
								"Drag Start, Agent, Debug, Pause, and End blocks onto the canvas, wire them into a linear chain, and run the workflow with live per-node output.",
							)}
						</Text>
					</Stack>
					{isCanvasOpen ? (
						<Group gap="xs">
							<Button
								variant="subtle"
								leftSection={<IconArrowLeft size={16} />}
								onClick={closeCanvas}
								data-testid="preview-back"
							>
								{t("pages.preview.back", "Back to list")}
							</Button>
							<Button
								leftSection={<IconDeviceFloppy size={16} />}
								loading={isSaving}
								onClick={handleSaveCurrent}
								data-testid="preview-save"
							>
								{t("common.save", "Save")}
							</Button>
						</Group>
					) : (
						<Button leftSection={<IconPlus size={16} />} onClick={openNew} data-testid="preview-create-button">
							{t("pages.preview.createButton", "New workflow")}
						</Button>
					)}
				</Group>

				{isCanvasOpen ? (
					openId !== null && detailQuery.isLoading ? (
						<Loader data-testid="preview-canvas-loading" />
					) : (
						<PreviewActiveRunContext.Provider value={activeRunId}>
							<WorkflowCanvas
								key={openId ?? "new"}
								initialNodes={initialNodes}
								initialEdges={initialEdges}
								initialStartText={canvasGraph.startText}
								runState={{
									isRunning: activeRun?.status === "running",
									isPaused: activeRun?.status === "paused",
								}}
								isControlBusy={isControlBusy}
								onExecute={handleExecute}
								onCancel={handleCancel}
								onContinue={handleContinue}
								onGraphChange={setLiveGraph}
							/>
						</PreviewActiveRunContext.Provider>
					)
				) : workflowsQuery.isLoading ? (
					<Loader data-testid="preview-list-loading" />
				) : (
					<WorkflowList
						workflows={workflows}
						isMutating={deleteMutation.isPending}
						onOpen={openWorkflow}
						onDelete={handleDelete}
					/>
				)}
			</Stack>
		</Container>
	);
}
