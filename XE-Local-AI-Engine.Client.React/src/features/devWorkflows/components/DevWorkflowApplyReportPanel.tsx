import { Alert, Badge, Code, Group, Paper, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import {
	type DevWorkflowAppliedTask,
	type DevWorkflowApplyReportBody,
	isDevWorkflowApplyLanded,
} from "@/features/devWorkflows/models/DevWorkflowApplyReport";

export interface DevWorkflowApplyReportPanelProps {
	readonly report: DevWorkflowApplyReportBody;
}

/**
 * An apply node's `<nodeKey>-apply.json`: which task each patch belonged to, and what the hash-locked gate did with it.
 *
 * The one thing this panel must never do is let a partial apply read as a whole one. A fan-out's applies run in
 * SEQUENCE and stop at the first refusal — after one, the repository is not in the state the next patch was approved
 * against — so a report can legitimately say "two landed, one refused, one never offered", and every one of those four
 * words has to survive to the screen. The count in the header is therefore always "N of M", never a bare N, and the
 * refused and cancelled rows carry their own detail rather than being collapsed into a failure banner.
 *
 * Zero tasks is a PASS: a decomposition may honestly answer "no work needed". It says so in words rather than
 * rendering an empty list under a green badge.
 */
export function DevWorkflowApplyReportPanel({ report }: DevWorkflowApplyReportPanelProps) {
	const { t } = useTranslation();
	const tasks = report.tasks ?? [];
	const landed = tasks.filter((task) => isDevWorkflowApplyLanded(task.outcome)).length;

	return (
		<Stack gap="xs" data-testid="dev-workflow-apply-report">
			<Group gap="xs" wrap="wrap">
				<Badge color={report.passed ? "green" : "red"} data-testid="dev-workflow-apply-result">
					{report.passed
						? t("pages.devWorkflows.apply.passed", "Every patch landed")
						: t("pages.devWorkflows.apply.failed", "The apply did not complete")}
				</Badge>
				{/* Always "N of M". A bare count over a sequence that stops at the first refusal is the false green this
				    panel exists to prevent. */}
				<Text size="xs" c="dimmed" data-testid="dev-workflow-apply-count">
					{t("pages.devWorkflows.apply.count", "{{landed}} of {{total}} patches applied", {
						landed,
						total: tasks.length,
					})}
				</Text>
			</Group>

			{tasks.length === 0 ? (
				// Not an empty list under a green badge: a decomposition answering "no work needed" is a real answer, and
				// so is an apply node that found nothing to apply. Saying which is not this panel's to know — it says the
				// only thing the document supports.
				<Text size="sm" c="dimmed" data-testid="dev-workflow-apply-no-tasks">
					{t("pages.devWorkflows.apply.noTasks", "This node found no completed task with a patch to apply.")}
				</Text>
			) : (
				<Stack gap="xs">
					{tasks.map((task) => (
						<AppliedTaskCard key={task.taskId} task={task} />
					))}
				</Stack>
			)}

			{/* The sequence stops at the first refusal, so anything after it was never offered at all. Saying so is the
			    difference between "three patches failed" and "one failed and two were never tried". */}
			{tasks.some((task) => task.outcome === "cancelled") ? (
				<Alert color="orange" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-apply-stopped">
					{t(
						"pages.devWorkflows.apply.stopped",
						"The sequence stopped before every patch was offered. After a refusal the repository is no longer in the state the remaining patches were approved against, so they were not attempted.",
					)}
				</Alert>
			) : null}
		</Stack>
	);
}

function AppliedTaskCard({ task }: { readonly task: DevWorkflowAppliedTask }) {
	const { t } = useTranslation();
	const landed = isDevWorkflowApplyLanded(task.outcome);
	return (
		<Paper withBorder={true} p="xs" data-testid={`dev-workflow-apply-task-${task.taskId}`}>
			<Group justify="space-between" wrap="nowrap" align="flex-start">
				{/* The title is nullable on the wire — a materialized child inherits its brief, and a task without one
				    falls back to the node key that produced the patch, which is the name the graph uses for it anyway. */}
				<Text size="sm" style={{ flex: 1, minWidth: 0 }} lineClamp={2}>
					{task.title ?? task.nodeKey ?? task.taskId}
				</Text>
				<Badge size="xs" variant="light" color={landed ? "green" : task.outcome === "cancelled" ? "gray" : "red"}>
					{/* The server's own token through a label map, with the raw token as the fallback so a vocabulary this
					    client has not learned yet still reads as itself. */}
					{t(`pages.devWorkflows.applyOutcome.${task.outcome}`, task.outcome)}
				</Badge>
			</Group>
			{task.detail ? (
				// Sanitized server-side and rendered verbatim: on a refusal it is the only account of why.
				<Text size="xs" c="dimmed" mt={4} style={{ whiteSpace: "pre-wrap" }} data-testid={`dev-workflow-apply-detail-${task.taskId}`}>
					{task.detail}
				</Text>
			) : null}
			<Code mt={4}>{task.nodeKey}</Code>
		</Paper>
	);
}
