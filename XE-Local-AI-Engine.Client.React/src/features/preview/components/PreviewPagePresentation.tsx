import { Box, Button, Group, Loader, ScrollArea, Stack, Text } from "@mantine/core";
import { IconArrowLeft, IconBinaryTree2, IconDeviceFloppy, IconPlus } from "@tabler/icons-react";
import type { TFunction } from "i18next";

import { FullHeightPage } from "@/core/ui/components/FullHeightPage/FullHeightPage";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { ActiveRunsPanel } from "@/features/preview/components/ActiveRunsPanel";
import { PreviewActiveRunContext } from "@/features/preview/components/PreviewActiveRunContext";
import { WorkflowCanvas } from "@/features/preview/components/WorkflowCanvas";
import { WorkflowList } from "@/features/preview/components/WorkflowList";
import type { PreviewCanvasEdge, PreviewCanvasNode } from "@/features/preview/models/PreviewCanvasModels";
import type {
	PreviewRunSummary,
	PreviewWorkflowGraph,
	PreviewWorkflowSummary,
} from "@/features/preview/models/PreviewWorkflowModels";

interface PreviewPageActionProps {
	readonly isCanvasOpen: boolean;
	readonly closeCanvas: () => void;
	readonly openNew: () => void;
	readonly isSaving: boolean;
	readonly onSave: () => void;
}

interface PreviewCanvasSectionProps {
	readonly openId: string | null;
	readonly detailLoading: boolean;
	readonly activeRunId: string | null;
	readonly initialNodes: PreviewCanvasNode[];
	readonly initialEdges: PreviewCanvasEdge[];
	readonly graph: PreviewWorkflowGraph;
	readonly activeRunStatus?: string;
	readonly isControlBusy: boolean;
	readonly onExecute: (graph: PreviewWorkflowGraph) => void;
	readonly onCancel: () => void;
	readonly onContinue: () => void;
	readonly onGraphChange: (graph: PreviewWorkflowGraph) => void;
}

interface PreviewListSectionProps {
	readonly workflowsLoading: boolean;
	readonly runs: readonly PreviewRunSummary[];
	readonly isCancellingRuns: boolean;
	readonly onReattach: (runId: string) => void;
	readonly onCancelRun: (runId: string) => void;
	readonly onCancelAll: () => void;
	readonly workflows: readonly PreviewWorkflowSummary[];
	readonly isDeletingWorkflow: boolean;
	readonly onOpenWorkflow: (workflowId: string) => void;
	readonly onDeleteWorkflow: (workflow: PreviewWorkflowSummary) => void;
}

interface PreviewPagePresentationProps {
	readonly t: TFunction;
	readonly actions: PreviewPageActionProps;
	readonly canvas: PreviewCanvasSectionProps;
	readonly list: PreviewListSectionProps;
}

function PreviewPageActions({ t, actions }: Pick<PreviewPagePresentationProps, "t" | "actions">) {
	return actions.isCanvasOpen ? (
		<>
			<Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={actions.closeCanvas} data-testid="preview-back">
				{t("pages.preview.back", "Back to list")}
			</Button>
			<Button
				leftSection={<IconDeviceFloppy size={16} />}
				loading={actions.isSaving}
				onClick={actions.onSave}
				data-testid="preview-save"
			>
				{t("common.save", "Save")}
			</Button>
		</>
	) : (
		<Button leftSection={<IconPlus size={16} />} onClick={actions.openNew} data-testid="preview-create-button">
			{t("pages.preview.createButton", "New workflow")}
		</Button>
	);
}

function PreviewCanvasPresentation({ t, canvas }: Pick<PreviewPagePresentationProps, "t" | "canvas">) {
	if (canvas.openId !== null && canvas.detailLoading) {
		return (
			<Group gap="sm" data-testid="preview-canvas-loading">
				<Loader size="sm" />
				<Text c="dimmed">{t("pages.preview.canvasLoading", "Loading workflow…")}</Text>
			</Group>
		);
	}

	return (
		<PreviewActiveRunContext.Provider value={canvas.activeRunId}>
			<WorkflowCanvas
				key={canvas.openId ?? "new"}
				initialNodes={canvas.initialNodes}
				initialEdges={canvas.initialEdges}
				initialStartText={canvas.graph.startText}
				runState={{
					isRunning: canvas.activeRunStatus === "running",
					isPaused: canvas.activeRunStatus === "paused",
				}}
				isControlBusy={canvas.isControlBusy}
				onExecute={canvas.onExecute}
				onCancel={canvas.onCancel}
				onContinue={canvas.onContinue}
				onGraphChange={canvas.onGraphChange}
			/>
		</PreviewActiveRunContext.Provider>
	);
}

function PreviewListPresentation({ t, list }: Pick<PreviewPagePresentationProps, "t" | "list">) {
	if (list.workflowsLoading) {
		return (
			<Group gap="sm" data-testid="preview-list-loading">
				<Loader size="sm" />
				<Text c="dimmed">{t("pages.preview.listLoading", "Loading workflows…")}</Text>
			</Group>
		);
	}

	return (
		<ScrollArea offsetScrollbars="y" scrollbarSize={8} style={{ flex: 1, minHeight: 0 }} type="auto">
			<Stack gap="lg">
				<ActiveRunsPanel
					runs={list.runs}
					isCancelling={list.isCancellingRuns}
					onReattach={list.onReattach}
					onCancel={list.onCancelRun}
					onCancelAll={list.onCancelAll}
				/>
				<WorkflowList
					workflows={list.workflows}
					isMutating={list.isDeletingWorkflow}
					onOpen={list.onOpenWorkflow}
					onDelete={list.onDeleteWorkflow}
				/>
			</Stack>
		</ScrollArea>
	);
}

export function PreviewPagePresentation(props: PreviewPagePresentationProps) {
	return (
		<FullHeightPage>
			<Stack gap="lg" px="md" style={{ flex: 1, minHeight: 0 }}>
				<PageHeader
					title={props.t("pages.preview.title", "Open Canvas")}
					icon={<IconBinaryTree2 size={24} />}
					subtitle={props.t(
						"pages.preview.subtitle",
						"Drag Start, Agent, Debug, Pause, and End blocks onto the canvas, wire them into a linear chain, and run the workflow with live per-node output.",
					)}
					actions={<PreviewPageActions t={props.t} actions={props.actions} />}
				/>
				<Box style={{ flex: 1, minHeight: 0, display: "flex", flexDirection: "column" }}>
					{props.actions.isCanvasOpen ? (
						<PreviewCanvasPresentation t={props.t} canvas={props.canvas} />
					) : (
						<PreviewListPresentation t={props.t} list={props.list} />
					)}
				</Box>
			</Stack>
		</FullHeightPage>
	);
}
