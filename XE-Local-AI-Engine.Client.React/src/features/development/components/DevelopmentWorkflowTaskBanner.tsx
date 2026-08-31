import { Alert, Anchor, Stack, Text } from "@mantine/core";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

export interface DevelopmentWorkflowTaskBannerProps {
	/** The task's `workflowRunId`. Null for a task an operator created directly, which is most of them. */
	readonly workflowRunId: string | null | undefined;
	/** The run's work item, which the controller resolves through R6. Null until it lands, or where the route is off. */
	readonly workItemId: string | null;
}

/**
 * Says that a workflow drove this task, and where the decision that lets its patch land actually lives (Y3).
 *
 * This matters because Dev Mode's own apply gate is still on the page. A workflow-driven task's approval is a
 * HumanGate node upstream of the integration node, and the apply itself is the workflow's to perform — so an operator
 * looking at the Dev Mode panel needs to know that the button in front of them is not the authority here. The banner
 * says it; the panel is rendered read-only alongside.
 *
 * The link is built from the run rather than guessed: the Dev Mode task carries only a run id, and the workflow detail
 * route is keyed by WORK ITEM. R6 already answers both — `GET runs/{runId}` returns the run's `workItemId` — so the
 * controller reads that one existing endpoint rather than asking P3 for a new field. No link is offered until it
 * resolves, and none at all where the capability is off: a route that redirects home is worse than prose.
 */
export function DevelopmentWorkflowTaskBanner({ workflowRunId, workItemId }: DevelopmentWorkflowTaskBannerProps) {
	const { t } = useTranslation();
	const navigate = useNavigate();

	if (!workflowRunId) {
		return null;
	}

	return (
		<Alert color="blue" variant="light" data-testid="development-workflow-banner">
			<Stack gap={4} align="flex-start">
				<Text size="sm">
					{t(
						"pages.development.workflow.body",
						"A development workflow created this task and is driving it. The approval that lets its patch land is a gate node in that workflow, so this page shows the evidence and the workflow makes the decision.",
					)}
				</Text>
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
