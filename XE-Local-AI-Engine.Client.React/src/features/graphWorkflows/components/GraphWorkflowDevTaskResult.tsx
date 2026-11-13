// A DevTask node run's Development task, read out of its output document, and the way through to the Development page,
// which owns the attempts, the patch and the validation evidence. The lane writes `{ developmentTaskId, taskStatus,
// reviewRound }` for implement and `{ developmentTaskId, alreadyApplied }` for apply when the row settles.

import { Badge, Button, Group, Text } from "@mantine/core";
import { IconExternalLink } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { isPlainRecord } from "@/core/models/JsonRecord";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";

function textAt(value: unknown, ...path: readonly string[]): string | undefined {
	let current = value;
	for (const name of path) {
		current = isPlainRecord(current) ? current[name] : undefined;
	}
	return typeof current === "string" && current.length > 0 ? current : undefined;
}

export interface GraphWorkflowDevTaskResultProps {
	/** The node run's output envelope, `{ status, attempt, output }`. */
	readonly output: unknown;
	/** The node run's input document; its `run.input.developmentProjectId` names the project the task belongs to. */
	readonly input: unknown;
	/** The attempt the row is on. An envelope from an earlier attempt describes a task stage that is no longer current. */
	readonly attempt: number;
}

export function GraphWorkflowDevTaskResult({ output, input, attempt }: GraphWorkflowDevTaskResultProps) {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const taskId = textAt(output, "output", "developmentTaskId");
	if (taskId === undefined || !isPlainRecord(output) || output["attempt"] !== attempt) {
		return (
			<Text size="xs" c="dimmed" data-testid="graph-workflow-node-panel-devtask-pending">
				{t("pages.graphWorkflows.nodePanel.devTaskPending", "This node has not reported its Development task yet.")}
			</Text>
		);
	}
	const taskStatus = textAt(output, "output", "taskStatus");
	const projectId = textAt(input, "run", "input", "developmentProjectId");

	return (
		<SectionCard
			title={t("pages.graphWorkflows.nodePanel.devTask", "Development task")}
			gap="xs"
			data-testid="graph-workflow-node-panel-devtask"
		>
			{taskStatus ? (
				<Group gap="xs">
					<Badge size="sm" variant="light" data-testid="graph-workflow-node-panel-devtask-status">
						{t(`pages.graphWorkflows.devTaskStatus.${taskStatus}`, taskStatus)}
					</Badge>
				</Group>
			) : null}
			<Text size="xs" c="dimmed" data-testid="graph-workflow-node-panel-devtask-id">
				{taskId}
			</Text>
			{nodeCapabilities.development ? (
				<Button
					size="xs"
					variant="subtle"
					leftSection={<IconExternalLink size={14} />}
					// Without the project there is nothing to seed: the plain link is the honest one.
					onClick={() =>
						navigate(projectId ? { to: "/development", search: { project: projectId, task: taskId } } : { to: "/development" })
					}
					data-testid="graph-workflow-node-panel-devtask-link"
				>
					{t("pages.graphWorkflows.nodePanel.openDevelopment", "Open on the Development page")}
				</Button>
			) : null}
		</SectionCard>
	);
}
