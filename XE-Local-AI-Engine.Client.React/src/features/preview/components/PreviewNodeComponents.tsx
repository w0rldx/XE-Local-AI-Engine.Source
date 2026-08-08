import { Badge, Group, Stack, Text } from "@mantine/core";
import {
	IconBug,
	IconFlag,
	IconFlagCheck,
	IconPlayerPause,
	IconRobot,
} from "@tabler/icons-react";
import { Handle, type NodeProps, Position } from "@xyflow/react";
import { useTranslation } from "react-i18next";

import { useActiveRunId } from "@/features/preview/components/PreviewActiveRunContext";
import classes from "@/features/preview/components/PreviewNodes.module.css";
import type { PreviewCanvasNode } from "@/features/preview/models/PreviewCanvasModels";
import { type PreviewNodeStatus, usePreviewRunStore } from "@/features/preview/stores/PreviewRunStore";

// Live per-node state from the store for the canvas's active run, or undefined when no run is active or this node
// has not emitted yet. Centralized so every node component reads its run state the same way.
function useNodeRunState(nodeId: string) {
	const runId = useActiveRunId();
	return usePreviewRunStore((state) => (runId ? state.runs[runId]?.nodes[nodeId] : undefined));
}

function statusClass(status: PreviewNodeStatus | undefined): string | undefined {
	if (status === "running") {
		return classes["node-running"];
	}
	if (status === "completed") {
		return classes["node-completed"];
	}
	if (status === "failed") {
		return classes["node-failed"];
	}
	return undefined;
}

function cx(...values: Array<string | false | undefined>): string {
	return values.filter(Boolean).join(" ");
}

// A live output panel shown under a node. Empty content renders nothing so an idle node stays compact.
function OutputPanel({ content }: { content: string | null | undefined }) {
	if (!content) {
		return null;
	}
	return <div className={classes["output-box"]}>{content}</div>;
}

export function StartNode({ selected }: NodeProps<PreviewCanvasNode>) {
	const { t } = useTranslation();
	return (
		<div className={cx(classes["node"], selected && classes["node-selected"])} data-testid="preview-node-start">
			<div className={classes["node-title"]}>
				<IconFlag size={16} />
				<Text size="sm">{t("pages.preview.nodes.start", "Start")}</Text>
			</div>
			<Handle type="source" position={Position.Bottom} />
		</div>
	);
}

export function EndNode({ selected }: NodeProps<PreviewCanvasNode>) {
	const { t } = useTranslation();
	return (
		<div className={cx(classes["node"], selected && classes["node-selected"])} data-testid="preview-node-end">
			<Handle type="target" position={Position.Top} />
			<div className={classes["node-title"]}>
				<IconFlagCheck size={16} />
				<Text size="sm">{t("pages.preview.nodes.end", "End")}</Text>
			</div>
		</div>
	);
}

export function AgentNode({ id, data, selected }: NodeProps<PreviewCanvasNode>) {
	const { t } = useTranslation();
	const runState = useNodeRunState(id);

	return (
		<div
			className={cx(classes["node"], selected && classes["node-selected"], statusClass(runState?.status))}
			data-testid="preview-node-agent"
		>
			<Handle type="target" position={Position.Top} />
			<Stack gap={4}>
				<div className={classes["node-title"]}>
					<IconRobot size={16} />
					<Text size="sm" lineClamp={1}>
						{data.label?.trim() || t("pages.preview.nodes.agent", "Agent")}
					</Text>
				</div>
				{data.model ? (
					<Badge size="xs" variant="light">
						{data.model}
					</Badge>
				) : (
					<Text size="xs" c="red">
						{t("pages.preview.nodes.agentNeedsModel", "Set a model")}
					</Text>
				)}
				<OutputPanel content={runState?.output} />
				{runState?.error ? (
					<Text size="xs" c="red">
						{runState.error}
					</Text>
				) : null}
			</Stack>
			<Handle type="source" position={Position.Bottom} />
		</div>
	);
}

// Debug node — shows the RAW output of the immediately-upstream node live (the backend pushes preview.node.debug
// to this node id with the upstream output as the payload).
export function DebugNode({ id, selected }: NodeProps<PreviewCanvasNode>) {
	const { t } = useTranslation();
	const runState = useNodeRunState(id);

	return (
		<div
			className={cx(classes["node"], selected && classes["node-selected"], statusClass(runState?.status))}
			data-testid="preview-node-debug"
		>
			<Handle type="target" position={Position.Top} />
			<Stack gap={4}>
				<div className={classes["node-title"]}>
					<IconBug size={16} />
					<Text size="sm">{t("pages.preview.nodes.debug", "Debug")}</Text>
				</div>
				<Text size="xs" c="dimmed">
					{t("pages.preview.nodes.debugHint", "Live upstream output")}
				</Text>
				<OutputPanel content={runState?.debugOutput} />
			</Stack>
			<Handle type="source" position={Position.Bottom} />
		</div>
	);
}

// Pause node — shows the upstream output and (when this run is paused at this node) a Continue button. The
// Continue action is owned by the canvas toolbar (run-scoped), so this node surfaces the pause output only; the
// toolbar drives the resume.
export function PauseNode({ id, selected }: NodeProps<PreviewCanvasNode>) {
	const { t } = useTranslation();
	const runId = useActiveRunId();
	const runState = useNodeRunState(id);
	const isPausedHere = usePreviewRunStore((state) =>
		runId ? state.runs[runId]?.status === "paused" && state.runs[runId]?.pausedNodeId === id : false,
	);
	const pauseOutput = usePreviewRunStore((state) => (runId ? state.runs[runId]?.pauseOutput : undefined));

	return (
		<div
			className={cx(classes["node"], selected && classes["node-selected"], statusClass(runState?.status))}
			data-testid="preview-node-pause"
		>
			<Handle type="target" position={Position.Top} />
			<Stack gap={4}>
				<div className={classes["node-title"]}>
					<IconPlayerPause size={16} />
					<Text size="sm">{t("pages.preview.nodes.pause", "Pause")}</Text>
				</div>
				<OutputPanel content={pauseOutput ?? runState?.output} />
				{isPausedHere ? (
					<Group gap={4}>
						<IconPlayerPause size={12} />
						<Text size="xs" c="orange">
							{t("pages.preview.nodes.pausedHere", "Paused — continue from the toolbar")}
						</Text>
					</Group>
				) : null}
			</Stack>
			<Handle type="source" position={Position.Bottom} />
		</div>
	);
}
