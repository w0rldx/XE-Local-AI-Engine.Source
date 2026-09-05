import { Badge, Button, Group, Stack, Text } from "@mantine/core";
import { IconExternalLink } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import type { DevWorkflowNodeRunDetailResponse } from "@/features/devWorkflows/models/DevWorkflowModels";

export interface DevWorkflowDevTaskNodePanelProps {
	readonly nodeRun: DevWorkflowNodeRunDetailResponse;
}

/**
 * The stage a DevTask node's development task reached, and the way through to Dev Mode, which owns everything else.
 *
 * The executor creates a real `DevelopmentTask` and drives the existing attempt → validation → review chain, so this
 * panel re-implements none of it: re-hosting the hash-locked evidence chain would fork the one place it is rendered.
 * The workflow's contribution is the pointer and the node's own status.
 *
 * **Per Y3 the approval that authorises an apply is a workflow HumanGate upstream of the integration node, not the
 * Dev Mode panel** — which is why this links out to evidence rather than to a second approve button.
 */
export function DevWorkflowDevTaskNodePanel({ nodeRun }: DevWorkflowDevTaskNodePanelProps) {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const stage = useMemo(() => readDevTaskStage(nodeRun.outputJson, nodeRun.attempt ?? 1), [nodeRun.outputJson, nodeRun.attempt]);

	if (!nodeRun.developmentTaskId) {
		return null;
	}

	const projectId = nodeRun.developmentProjectId ?? undefined;
	const taskId = nodeRun.developmentTaskId;

	return (
		<SectionCard title={t("pages.devWorkflows.node.devTask", "Development task")} gap="xs" data-testid="dev-workflow-node-devtask">
			{/* The stage is read off the node's own output document, which the executor writes when the row settles. A
			    node still working has none, and saying "still running" beats printing a stage from a previous attempt. */}
			{stage ? (
				<Group gap="xs" wrap="wrap">
					<Badge size="sm" variant="light" data-testid="dev-workflow-node-devtask-stage">
						{t(`pages.devWorkflows.devTaskStatus.${stage.taskStatus}`, stage.taskStatus)}
					</Badge>
					{stage.reviewRound > 0 ? (
						<Text size="xs" c="dimmed" data-testid="dev-workflow-node-devtask-round">
							{t("pages.devWorkflows.node.devTaskReviewRound", "round {{round}}", { round: stage.reviewRound })}
						</Text>
					) : null}
				</Group>
			) : (
				<Text size="xs" c="dimmed" data-testid="dev-workflow-node-devtask-nostage">
					{t("pages.devWorkflows.node.devTaskNoStage", "This task has not reported a stage yet.")}
				</Text>
			)}

			<Text size="xs" c="dimmed" data-testid="dev-workflow-node-devtask-id">
				{taskId}
			</Text>

			{nodeCapabilities.development ? (
				<Stack gap={4}>
					<Button
						size="xs"
						variant="subtle"
						leftSection={<IconExternalLink size={14} />}
						// X8: the deep link carries the project so Dev Mode opens on the right one instead of on whichever
						// project happens to be first. Without a project there is nothing to seed — the task belongs to one
						// this node cannot name — so the plain link is the honest one.
						onClick={() =>
							navigate(projectId ? { to: "/development", search: { project: projectId, task: taskId } } : { to: "/development" })
						}
						data-testid="dev-workflow-node-development-link"
					>
						{t("pages.devWorkflows.node.openDevelopment", "Open Development Mode")}
					</Button>
					<Text size="xs" c="dimmed">
						{t(
							"pages.devWorkflows.node.devTaskEvidence",
							"Dev Mode shows this task's attempts, patch and validation evidence. The approval that lets a patch land is a gate node in this workflow.",
						)}
					</Text>
				</Stack>
			) : null}
		</SectionCard>
	);
}

/**
 * The slice of the node's output document this panel reads. Shaped by `DevWorkflowDevTaskExecutor`, so it is a
 * workflow contract rather than a Dev Mode one — the task's own status string travels through it, which is why the
 * label is looked up by token with the raw value as its fallback.
 *
 * The document is only this node's stage while it describes the attempt the row is ON. A re-attempt writes Pending
 * WITHOUT clearing OutputJson, so the previous attempt's `taskStatus` — an "Awaiting apply" that no longer awaits
 * anything — would otherwise be printed over a task that is being implemented again. An attempt the document does not
 * name cannot be checked, and an unverifiable stage is exactly the one not to invent.
 */
function readDevTaskStage(
	outputJson: string | null | undefined,
	currentAttempt: number,
): { taskStatus: string; reviewRound: number } | null {
	if (typeof outputJson !== "string" || outputJson.length === 0) {
		return null;
	}
	let parsed: unknown;
	try {
		parsed = JSON.parse(outputJson);
	} catch {
		return null;
	}
	if (typeof parsed !== "object" || parsed === null) {
		return null;
	}
	if ((parsed as Record<string, unknown>)["attempt"] !== currentAttempt) {
		return null;
	}
	const taskStatus = (parsed as Record<string, unknown>)["taskStatus"];
	if (typeof taskStatus !== "string" || taskStatus.length === 0) {
		return null;
	}
	const reviewRound = (parsed as Record<string, unknown>)["reviewRound"];
	return { taskStatus, reviewRound: typeof reviewRound === "number" ? reviewRound : 0 };
}
