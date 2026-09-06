// The card React Flow draws for every node kind. The `nodeTypes` registry that maps a type name to it lives in
// `GraphWorkflowEditorCanvas`, the way `DevWorkflowGraphView` holds its own.
//
// The cards differ in exactly two things: their icon, and their HANDLES. Everything else a kind carries lives in the
// config panel, which already dispatches on kind — the devWorkflows split, kept. Handles are the exception because
// `Condition` and `Pause` are the only kinds where a handle id means something: `onConnect` copies it into the new
// edge's `sourceHandle`, its label and its condition, which is what routes the branch.
//
// The canvas is a pointer surface. The node-run table stays the keyboard and screen-reader path in the run view, which
// is why a card is a div and not a button.

import { Badge, Group, Stack, Text } from "@mantine/core";
import {
	IconArrowsJoin2,
	IconArrowsSplit2,
	IconFlag,
	IconFlagCheck,
	IconGitBranch,
	IconPlayerPause,
	IconRobot,
	IconTool,
} from "@tabler/icons-react";
import { Handle, type NodeProps, Position } from "@xyflow/react";
import { useTranslation } from "react-i18next";

import classes from "@/features/graphWorkflows/components/GraphWorkflowNodes.module.css";
import type {
	GraphWorkflowCanvasNode,
	GraphWorkflowCanvasNodeData,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import type { GraphWorkflowNodeKind, GraphWorkflowNodeRunStatus } from "@/features/graphWorkflows/models/GraphWorkflowModels";

const nodeKindIcons: Record<GraphWorkflowNodeKind, typeof IconRobot> = {
	Start: IconFlag,
	Agent: IconRobot,
	Tool: IconTool,
	Condition: IconGitBranch,
	Parallel: IconArrowsSplit2,
	Join: IconArrowsJoin2,
	Pause: IconPlayerPause,
	End: IconFlagCheck,
};

/**
 * The run view sets `data.runState`; the editor never does. A local colour map rather than a shared badge component —
 * the run view's own badge file belongs to another lane and a card needs nothing more than a colour and a label.
 */
const nodeStatusColors: Record<GraphWorkflowNodeRunStatus, string> = {
	Pending: "gray",
	Queued: "gray",
	Running: "blue",
	WaitingForApproval: "orange",
	Succeeded: "teal",
	Failed: "red",
	Skipped: "gray",
	Cancelled: "gray",
};

function cx(...values: Array<string | false | undefined>): string {
	return values.filter(Boolean).join(" ");
}

/**
 * A source handle plus the caption naming the branch that leaves it. `top` spreads several down the card's right edge,
 * because `layoutGraphWorkflow` ranks left to right — a source on the bottom would draw every laid-out edge as a loop.
 */
function BranchHandle({ id, top, caption }: { readonly id: string; readonly top: string; readonly caption: string }) {
	return (
		<Handle type="source" position={Position.Right} id={id} style={{ top }} data-testid={`graph-workflow-handle-source-${id}`}>
			<span className={classes["handle-caption"]}>{caption}</span>
		</Handle>
	);
}

/** The source handles for one kind. Condition and Pause are the two that carry meaning; everything else has one. */
function SourceHandles({ data }: { readonly data: GraphWorkflowCanvasNodeData }) {
	const { t } = useTranslation();
	if (data.kind === "End") {
		// An End node has no successor: offering a handle would let an operator author an edge the validator refuses.
		return null;
	}
	if (data.kind === "Condition") {
		return (
			<>
				<BranchHandle id="true" top="30%" caption="true" />
				<BranchHandle id="false" top="70%" caption="false" />
			</>
		);
	}
	if (data.kind === "Pause") {
		// One handle per ALLOWED decision, id = the decision. That is what makes the Pause pre-flight rule satisfiable by
		// dragging: `onConnect` reads the id and prefills `output.decision Eq <decision>`.
		const decisions = data.allowedDecisions;
		return (
			<>
				{decisions.map((decision, index) => (
					<BranchHandle
						key={decision}
						// The id stays the wire token — `onConnect` reads it — while the caption is the operator's word for it.
						id={decision}
						top={`${Math.round(((index + 1) / (decisions.length + 1)) * 100)}%`}
						caption={t(`pages.graphWorkflows.decision.${decision}`, decision)}
					/>
				))}
			</>
		);
	}
	return <Handle type="source" position={Position.Right} data-testid="graph-workflow-handle-source-default" />;
}

export function GraphWorkflowNodeCard({ data, selected }: NodeProps<GraphWorkflowCanvasNode>) {
	const { t } = useTranslation();
	const Icon = nodeKindIcons[data.kind];
	const runState = data.runState;
	// A boolean the CANVAS writes from the issue list, so the card needs no knowledge of the validator.
	const hasIssue = data["hasIssue"] === true;

	return (
		<div
			className={cx(classes["node"], selected === true && classes["node-selected"], hasIssue && classes["node-issue"])}
			data-testid={`graph-workflow-node-${data.key}`}
			data-kind={data.kind}
			data-has-issue={hasIssue ? "true" : "false"}
			{...(runState ? { "data-status": runState.status } : {})}
		>
			{/* A Start node has no predecessor, so it gets no target handle — same reason End gets no source. */}
			{data.kind === "Start" ? null : (
				<Handle type="target" position={Position.Left} data-testid="graph-workflow-handle-target" />
			)}
			<Stack gap={4}>
				<div className={classes["node-title"]}>
					<Icon size={14} />
					<Text size="sm" lineClamp={1}>
						{data.label.trim().length > 0 ? data.label : t(`pages.graphWorkflows.nodeKind.${data.kind}`, data.kind)}
					</Text>
				</div>
				<Group gap={4} wrap="wrap">
					<Badge size="xs" variant="light" color="gray">
						{t(`pages.graphWorkflows.nodeKind.${data.kind}`, data.kind)}
					</Badge>
					{/* Only `Any` is shown: `All` is the parser's default and a badge on every card would say nothing. */}
					{data.joinPolicy === "Any" ? (
						<Badge size="xs" variant="outline" color="gray" data-testid={`graph-workflow-node-join-${data.key}`}>
							{t("pages.graphWorkflows.node.joinAny", "First branch wins")}
						</Badge>
					) : null}
					{runState ? (
						<Badge
							size="xs"
							variant="light"
							color={nodeStatusColors[runState.status]}
							data-testid={`graph-workflow-node-status-${data.key}`}
						>
							{t(`pages.graphWorkflows.nodeStatus.${runState.status}`, runState.status)}
						</Badge>
					) : null}
				</Group>
				<Text size="xs" c="dimmed">
					{data.key}
				</Text>
			</Stack>
			<SourceHandles data={data} />
		</div>
	);
}
