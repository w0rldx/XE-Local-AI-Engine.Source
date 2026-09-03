import { Badge, Group, Stack, Text } from "@mantine/core";
import {
	IconAlertTriangle,
	IconArrowsJoin2,
	IconArrowsSplit2,
	IconCode,
	IconGitBranch,
	IconRobot,
	IconTool,
	IconUserCheck,
} from "@tabler/icons-react";
import { Handle, type NodeProps, Position } from "@xyflow/react";
import { useReducedMotion } from "framer-motion";
import { useTranslation } from "react-i18next";

import { DevWorkflowNodeStatusBadge } from "@/features/devWorkflows/components/DevWorkflowStatusBadge";
import classes from "@/features/devWorkflows/components/DevWorkflowNodes.module.css";
import type {
	DevWorkflowAnchorNode,
	DevWorkflowCanvasNode,
	DevWorkflowCanvasNodeData,
} from "@/features/devWorkflows/models/DevWorkflowGraphModels";
import {
	type DevWorkflowNodeStatus,
	type DevWorkflowNodeType,
	devWorkflowAttemptCounts,
	devWorkflowAttemptLabel,
	isDevWorkflowNodeInProgress,
} from "@/features/devWorkflows/models/DevWorkflowModels";

const nodeTypeIcons: Record<DevWorkflowNodeType, typeof IconRobot> = {
	Agent: IconRobot,
	Tool: IconTool,
	DevTask: IconCode,
	HumanGate: IconUserCheck,
	Gate: IconGitBranch,
	Parallel: IconArrowsSplit2,
	Join: IconArrowsJoin2,
};

/**
 * The card's border says what the table's badge says, in the same vocabulary (O9). `Queued` gets no border of its own
 * and no motion — it is waiting for a slot another node is holding, and a canvas that animated it would claim a GPU is
 * working on it. The `Running` spinner comes from the shared status badge, which already gates itself on reduced motion.
 */
function statusClass(status: DevWorkflowNodeStatus | undefined): string | undefined {
	switch (status) {
		case "Running":
			return classes["node-running"];
		case "Blocked":
			return classes["node-blocked"];
		case "WaitingForApproval":
			return classes["node-waiting"];
		case "Succeeded":
			return classes["node-succeeded"];
		case "Failed":
			return classes["node-failed"];
		default:
			return undefined;
	}
}

function cx(...values: Array<string | false | undefined>): string {
	return values.filter(Boolean).join(" ");
}

/**
 * ONE card for all seven node types (Y6). ponytail: the per-type difference on the card is an icon and a translated
 * kind badge — everything that actually diverges per type (a validation report, a Dev Mode deep link, an embedded
 * transcript) lives in the node PANEL, which already dispatches on kind. The registry below keeps seven entries so a
 * type that earns its own body later can be swapped in without touching the mapper or the view.
 *
 * The canvas is a pointer surface; the node-run TABLE stays the keyboard and screen-reader path to every node (P4 §2.2),
 * which is why this card is a div and not a button.
 */
export function DevWorkflowNodeCard({ id, data, selected }: NodeProps<DevWorkflowCanvasNode>) {
	const { t } = useTranslation();
	const reduced = useReducedMotion();
	const nodeData = data as DevWorkflowCanvasNodeData;
	const Icon = nodeTypeIcons[nodeData.nodeType];
	// A definition render carries no status — nothing has run — so nothing here may imply one.
	const isRunning = nodeData.status !== undefined && isDevWorkflowNodeInProgress(nodeData.status);
	const showsAttempt = nodeData.maxAttempts > 1 && nodeData.attempt > 1;

	return (
		<div
			className={cx(
				classes["node"],
				selected && classes["node-selected"],
				nodeData.isMaterialized && classes["node-materialized"],
				statusClass(nodeData.status),
				isRunning && !reduced && classes["node-pulse"],
			)}
			data-testid={`dev-workflow-graph-node-${id}`}
		>
			<Handle type="target" position={Position.Left} isConnectable={false} />
			<Stack gap={4}>
				<div className={classes["node-title"]}>
					<Icon size={14} />
					<Text size="sm" lineClamp={1}>
						{nodeData.label}
					</Text>
				</div>
				<Group gap={4} wrap="wrap">
					{nodeData.status ? (
						<DevWorkflowNodeStatusBadge status={nodeData.status} testId={`dev-workflow-graph-node-status-${id}`} />
					) : null}
					<Badge size="xs" variant="light" color="gray">
						{t(`pages.devWorkflows.nodeType.${nodeData.nodeType}`, nodeData.nodeType)}
					</Badge>
					{/* An apply node LANDS patches; a validation node judges a checkout. Same kind badge, opposite
					    consequence — and on a DRAFT preview the card is the only place that difference can be seen at all,
					    because nothing has run and there is no report to read it off. */}
					{nodeData.isApplyTool ? (
						<Badge size="xs" variant="light" color="grape" data-testid={`dev-workflow-graph-node-apply-${id}`}>
							{t("pages.devWorkflows.nodes.appliesPatches", "applies patches")}
						</Badge>
					) : null}
					{/* "3 of 5" names the CHILD this card belongs to, so a card says how much of a decomposition it is one
					    card of. The index is the server's, rendered unchanged: `MaterializationIndex` is already 1-based
					    (C2), and adding one to it made the only child of a decomposition read "2 of 2". A materialization
					    of one carries no count — "1 of 1" is noise, and the dashed border already said it. */}
					{nodeData.isMaterialized ? (
						<Badge size="xs" variant="outline" color="gray" data-testid={`dev-workflow-graph-node-materialized-${id}`}>
							{(nodeData.materializationCount ?? 0) > 1 && nodeData.materializationIndex !== undefined
								? t("pages.devWorkflows.nodes.materializedOf", "generated · {{index}} of {{count}}", {
										index: nodeData.materializationIndex,
										count: nodeData.materializationCount ?? 0,
									})
								: t("pages.devWorkflows.nodes.materialized", "generated")}
						</Badge>
					) : null}
					{nodeData.hasStaleInputs ? (
						<Badge size="xs" variant="light" color="orange">
							{t("pages.devWorkflows.nodes.staleInputs", "Stale inputs")}
						</Badge>
					) : null}
				</Group>
				{nodeData.status === "Blocked" ? (
					<Group gap={4} wrap="nowrap">
						<IconAlertTriangle size={12} color="var(--mantine-color-red-6)" />
						<Text size="xs" c="red" data-testid={`dev-workflow-graph-node-intervention-${id}`}>
							{t("pages.devWorkflows.nodes.needsIntervention", "needs your intervention")}
						</Text>
					</Group>
				) : null}
				{showsAttempt ? (
					<Text size="xs" c="dimmed" data-testid={`dev-workflow-graph-node-attempt-${id}`}>
						{devWorkflowAttemptLabel(
							t,
							devWorkflowAttemptCounts(nodeData.attempt, nodeData.maxAttempts, nodeData.operatorRetries),
						)}
					</Text>
				) : null}
				{nodeData.agentDisplayName ? (
					<Text size="xs" c="dimmed" lineClamp={1}>
						{nodeData.modelLabel
							? t("pages.devWorkflows.nodes.agentWithModel", "{{agent}} · {{model}}", {
									agent: nodeData.agentDisplayName,
									model: nodeData.modelLabel,
								})
							: nodeData.agentDisplayName}
					</Text>
				) : null}
			</Stack>
			<Handle type="source" position={Position.Right} isConnectable={false} />
		</div>
	);
}

/** Y6: Start and End are not node types. This is the visual that keeps a DAG from reading as truncated at its edges. */
export function DevWorkflowAnchorCard({ data }: NodeProps<DevWorkflowAnchorNode>) {
	const { t } = useTranslation();
	const isStart = data.anchor === "start";
	return (
		<div className={classes["anchor"]} data-testid={`dev-workflow-graph-anchor-${data.anchor}-${data.nodeKey}`}>
			{isStart ? null : <Handle type="target" position={Position.Left} isConnectable={false} />}
			{isStart ? t("pages.devWorkflows.graph.start", "Start") : t("pages.devWorkflows.graph.end", "End")}
			{isStart ? <Handle type="source" position={Position.Right} isConnectable={false} /> : null}
		</div>
	);
}
