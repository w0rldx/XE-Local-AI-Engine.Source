import { Badge, Collapse, Group, Paper, Stack, Text, UnstyledButton } from "@mantine/core";
import { IconChevronDown, IconSitemap } from "@tabler/icons-react";
import { type ReactNode, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import {
	formatWorkflowElapsed,
	isWaitingForWorkflowInput,
	toWorkflowActivity,
	toWorkflowPath,
	type WorkflowActivityEntry,
	workflowActivityDetailKinds,
	workflowNodeGlyph,
} from "@/features/chat/workflow/ChatWorkflowModels";
import { GraphWorkflowRunStatusBadge } from "@/features/graphWorkflows/components/GraphWorkflowStatusBadge";
import {
	useGraphWorkflowRun,
	useGraphWorkflowRunEvents,
	useSettledGraphWorkflowNodeRuns,
} from "@/features/graphWorkflows/queries/useGraphWorkflows";

interface WorkflowActivityBlockProps {
	readonly runId: string;
	readonly workflowName: string;
	/** Polling cadence while the run hub is down; only the live run ever gets one. */
	readonly pollIntervalMs?: number;
	/** The live run's running node and its live output; rendered under that node's row. */
	readonly liveNodeKey?: string;
	readonly liveDetail?: ReactNode;
}

/** An Agent / LLM Call node's intermediate text, collapsed by default the way a reply's thoughts are. */
function IntermediateText({ nodeKey, text }: { nodeKey: string; text: string }) {
	const { t } = useTranslation();
	const [opened, setOpened] = useState(false);
	return (
		<Stack gap={2}>
			<UnstyledButton
				onClick={() => setOpened((current) => !current)}
				aria-expanded={opened}
				data-testid={`chat-workflow-activity-output-toggle-${nodeKey}`}
			>
				<Group gap={4} wrap="nowrap">
					<IconChevronDown
						size={12}
						style={{ transform: opened ? "rotate(180deg)" : undefined, transition: "transform 150ms ease" }}
					/>
					<Text size="xs" c="dimmed">
						{t("pages.chat.workflow.activity.output", "Output")}
					</Text>
				</Group>
			</UnstyledButton>
			<Collapse expanded={opened}>
				<Text size="xs" style={{ whiteSpace: "pre-wrap" }} data-testid={`chat-workflow-activity-output-${nodeKey}`}>
					{text}
				</Text>
			</Collapse>
		</Stack>
	);
}

function ActivityRow({ entry, liveDetail }: { entry: WorkflowActivityEntry; liveDetail?: ReactNode }) {
	const { t } = useTranslation();
	const dimmed = entry.status === "Skipped" || entry.status === "Cancelled";
	return (
		<Stack gap={2} data-testid={`chat-workflow-activity-node-${entry.key}`} data-status={entry.status}>
			<Group gap={6} wrap="wrap">
				<Text size="xs" fw={600} c={dimmed ? "dimmed" : entry.status === "Failed" ? "red" : undefined}>
					{`${workflowNodeGlyph(entry.status)} ${entry.label}`}
				</Text>
				<Text size="xs" c="dimmed">
					{entry.kind === "ChatInput" && entry.status === "WaitingForApproval"
						? t("pages.graphWorkflows.runStatus.WaitingForInput", "Waiting for your input")
						: t(`pages.graphWorkflows.nodeStatus.${entry.status}`, entry.status)}
				</Text>
				{entry.durationMs !== undefined ? (
					<Text size="xs" c="dimmed">
						{formatWorkflowElapsed(entry.durationMs)}
					</Text>
				) : null}
				{entry.route ? (
					<Badge size="xs" variant="light" data-testid={`chat-workflow-activity-route-${entry.key}`}>
						{t("pages.chat.workflow.activity.route", "→ {{route}}", { route: entry.route })}
					</Badge>
				) : null}
				{entry.published ? (
					<Badge size="xs" variant="outline" color="green" data-testid={`chat-workflow-activity-published-${entry.key}`}>
						{t("pages.chat.workflow.activity.published", "Published to chat")}
					</Badge>
				) : null}
			</Group>
			{entry.input ? (
				<Text size="xs" c="dimmed" data-testid={`chat-workflow-activity-input-${entry.key}`}>
					{entry.input.answer
						? t("pages.chat.workflow.activity.inputAnswered", "Asked: {{prompt}} — Answer: {{answer}}", {
								prompt: entry.input.prompt,
								answer: entry.input.answer,
							})
						: t("pages.chat.workflow.activity.inputAsked", "Asked: {{prompt}}", { prompt: entry.input.prompt })}
				</Text>
			) : null}
			{entry.tool ? (
				<Text size="xs" c="dimmed" lineClamp={2} data-testid={`chat-workflow-activity-tool-${entry.key}`}>
					{entry.tool.summary
						? t("pages.chat.workflow.activity.toolResult", "{{tool}}: {{summary}}", {
								tool: entry.tool.name,
								summary: entry.tool.summary,
							})
						: entry.tool.name}
				</Text>
			) : null}
			{entry.steering?.map((steer) => (
				<Text key={steer.seq} size="xs" c="dimmed" fs="italic" data-testid={`chat-workflow-activity-steer-${entry.key}`}>
					{steer.applied
						? steer.message
							? t("pages.chat.workflow.activity.steered", "Steered: {{message}}", { message: steer.message })
							: t("pages.chat.workflow.activity.steeredNoText", "Steered")
						: t("pages.chat.workflow.activity.steerIgnored", "Steering arrived after the node finished")}
				</Text>
			))}
			{entry.attachmentsSkipped ? (
				<Text size="xs" c="orange" data-testid={`chat-workflow-activity-attachments-skipped-${entry.key}`}>
					{t("pages.chat.workflow.activity.attachmentsSkipped", "Skipped attachments: {{names}}", {
						names: entry.attachmentsSkipped.join(", "),
					})}
				</Text>
			) : null}
			{entry.error ? (
				<Text size="xs" c="red">
					{entry.error}
				</Text>
			) : null}
			{entry.text ? <IntermediateText nodeKey={entry.key} text={entry.text} /> : null}
			{liveDetail}
		</Stack>
	);
}

/**
 * One bound run's activity, rendered right after the user message that triggered it: routing, each node's state and
 * duration, tool summaries, the ChatInput question and answer, and publish markers. Built only from durable state (run
 * detail, node documents, events) — the live run's hub subscription is what keeps these queries fresh.
 */
export function WorkflowActivityBlock({
	runId,
	workflowName,
	pollIntervalMs,
	liveNodeKey,
	liveDetail,
}: WorkflowActivityBlockProps) {
	const { t } = useTranslation();
	const feed = { pollIntervalMs };
	const runQuery = useGraphWorkflowRun(runId, feed);
	const eventsQuery = useGraphWorkflowRunEvents(runId, feed);
	// The feed is cursor-paged (a page is 200 events) and a chat block has no "Load more", so it reads to the end on its
	// own. A failed page stops the chase rather than retrying in a loop.
	const { hasNextPage, isFetchingNextPage, isFetchNextPageError, fetchNextPage } = eventsQuery;
	useEffect(() => {
		if (hasNextPage && !isFetchingNextPage && !isFetchNextPageError) {
			fetchNextPage().catch(() => undefined);
		}
	}, [hasNextPage, isFetchingNextPage, isFetchNextPageError, fetchNextPage]);
	const graph = runQuery.data?.graph;
	const nodeRuns = runQuery.data?.nodeRuns ?? [];
	// Only the nodes that finished and whose documents say something: a node detail is one request each.
	// Only settled nodes (Succeeded / Failed), so each document is read once and never again.
	const detailKeys = nodeRuns
		.filter(
			(nodeRun) =>
				workflowActivityDetailKinds.has(nodeRun.kind ?? "") && (nodeRun.status === "Succeeded" || nodeRun.status === "Failed"),
		)
		.map((nodeRun) => ({ nodeKey: nodeRun.nodeKey ?? "", attempt: nodeRun.attempt ?? 0 }));
	const details = useSettledGraphWorkflowNodeRuns(runId, detailKeys);
	const path = toWorkflowPath(graph, nodeRuns);
	const entries = toWorkflowActivity(graph, path, details, eventsQuery.data?.events ?? []);

	return (
		<Paper withBorder={true} radius="md" p="xs" data-testid={`chat-workflow-activity-${runId}`}>
			<Stack gap={6}>
				<Group gap="xs" wrap="nowrap">
					<IconSitemap size={14} color="var(--mantine-color-dimmed)" />
					<Text size="xs" fw={700} lineClamp={1}>
						{t("pages.chat.workflow.activity.title", "Workflow: {{name}}", { name: workflowName })}
					</Text>
					{runQuery.data ? (
						<GraphWorkflowRunStatusBadge
							status={runQuery.data.run.status}
							waitingForInput={isWaitingForWorkflowInput(path)}
							data-testid={`chat-workflow-activity-status-${runId}`}
						/>
					) : null}
				</Group>
				{entries.length === 0 ? (
					<Text size="xs" c="dimmed">
						{t("pages.chat.workflow.activity.empty", "Nothing has run yet.")}
					</Text>
				) : (
					entries.map((entry) => (
						<ActivityRow
							key={entry.key}
							entry={entry}
							liveDetail={entry.key === liveNodeKey && entry.status === "Running" ? liveDetail : undefined}
						/>
					))
				)}
			</Stack>
		</Paper>
	);
}
