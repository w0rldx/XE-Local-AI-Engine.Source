import { Alert, Anchor, Stack, Text } from "@mantine/core";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

export interface DevelopmentWorkflowTaskBannerProps {
	/** The task's `workflowRunId`. Null for a task an operator created directly, which is most of them. */
	readonly workflowRunId: string | null | undefined;
	/** The run's work item, which the controller resolves through R6. Null until it lands, or where the route is off. */
	readonly workItemId: string | null;
	/** The run reached a terminal status, so it will answer no further gate and this page is the authority again. */
	readonly runEnded: boolean;
	/** The run's status could not be read, so who owns the decision is unknown and the page stays read-only. */
	readonly runUnreadable: boolean;
	/** Asks for the run's status again. The way out of {@link runUnreadable} without reloading the page. */
	readonly onRetryStatus: () => void;
}

/**
 * Says that a workflow drove this task, and where the decision that lets its patch land actually lives (Y3).
 *
 * This matters because Dev Mode's own apply gate is still on the page. While the run is LIVE its approval is a
 * HumanGate node upstream of the integration node and the apply is the workflow's to perform — so an operator looking
 * at the Dev Mode panel needs to know that the button in front of them is not the authority. The banner says it; the
 * panel is rendered read-only alongside.
 *
 * A run that has ENDED is the other half of the same fact, and the copy has to change with it: a terminal run answers
 * no further gate, so this page is the only authority left over a patch that is already validated. Saying "the
 * workflow decides" over a run that can no longer decide anything would leave an operator waiting on nothing.
 *
 * A run whose status could not be READ is neither, and is not allowed to read as either: ownership is enforced by the
 * server, so the page keeps its patch read-only and says the status is unknown rather than implying a decision is
 * pending in a workflow nobody can see. The retry is the way out — without it a single failed read strands the patch
 * behind a banner with no next step.
 *
 * The link is built from the run rather than guessed: the Dev Mode task carries only a run id, and the workflow detail
 * route is keyed by WORK ITEM. R6 already answers both — `GET runs/{runId}` returns the run's `workItemId` — so the
 * controller reads that one existing endpoint rather than asking P3 for a new field. No link is offered until it
 * resolves, and none at all where the capability is off: a route that redirects home is worse than prose.
 */
export function DevelopmentWorkflowTaskBanner({
	workflowRunId,
	workItemId,
	runEnded,
	runUnreadable,
	onRetryStatus,
}: DevelopmentWorkflowTaskBannerProps) {
	const { t } = useTranslation();
	const navigate = useNavigate();

	if (!workflowRunId) {
		return null;
	}

	// Ordered by precedence, not nested: an unreadable status overrides a stale "ended", and both override the default.
	let body = t(
		"pages.development.workflow.body",
		"A development workflow created this task and is driving it. The approval that lets its patch land is a gate node in that workflow, so this page shows the evidence and the workflow makes the decision.",
	);
	if (runEnded) {
		body = t(
			"pages.development.workflow.ended",
			"The development workflow that created this task has ended, so it can no longer approve anything. This page is the remaining authority over its patch.",
		);
	}

	if (runUnreadable) {
		body = t(
			"pages.development.workflow.unreadable",
			"The status of the development workflow that created this task could not be read, so it is not known whether that workflow still owns the decision. This page stays read-only until it can be.",
		);
	}

	return (
		<Alert color={runEnded || runUnreadable ? "yellow" : "blue"} variant="light" data-testid="development-workflow-banner">
			<Stack gap={4} align="flex-start">
				<Text size="sm">{body}</Text>
				{runUnreadable ? (
					<Anchor component="button" type="button" size="sm" onClick={onRetryStatus} data-testid="development-workflow-retry">
						{t("pages.development.workflow.retry", "Check the workflow again")}
					</Anchor>
				) : null}
				{workItemId ? (
					<Anchor
						component="button"
						type="button"
						size="sm"
						onClick={() =>
							navigate({
								to: "/development-workflows/$workItemId",
								params: { workItemId },
								search: { run: workflowRunId },
							})
						}
						data-testid="development-workflow-link"
					>
						{t("pages.development.workflow.open", "Open the workflow run")}
					</Anchor>
				) : null}
			</Stack>
		</Alert>
	);
}
