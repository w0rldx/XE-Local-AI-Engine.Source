import { Alert } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { devWorkflowReviewerSameModelDetail } from "@/features/devWorkflows/models/DevWorkflowAttempts";
import type { DevWorkflowRunEventResponse } from "@/features/devWorkflows/models/DevWorkflowModels";

/**
 * The engine's `node.reviewer.same-model` warning: this node was handed a patch and reviews it on the model that produced
 * it. A warning, never a block, so the node runs on and this banner is the only place the panel says so.
 *
 * Only the CURRENT attempt's warning counts: after a re-pin and a Retry, attempt 1's warning would otherwise outlive
 * the model it named. An event without an attempt is not shown, since it cannot say it is about this one.
 */
export function DevWorkflowNodeReviewerModelNotice({
	nodeEvents,
	attempt,
}: {
	readonly nodeEvents: readonly DevWorkflowRunEventResponse[];
	readonly attempt: number;
}) {
	const { t } = useTranslation();
	const detail = nodeEvents
		.filter((event) => event.eventType === "node.reviewer.same-model")
		.map((event) => devWorkflowReviewerSameModelDetail(event.detailJson))
		.filter((candidate) => candidate.attempt === attempt)
		.at(-1);
	if (!detail) {
		return null;
	}

	return (
		<Alert color="yellow" variant="light" data-testid="dev-workflow-node-reviewer-same-model">
			{t(
				"pages.devWorkflows.node.reviewerSameModel",
				"This node reviews {{patches}} on {{model}}, the same model that produced it, so its review is not independent.",
				{ patches: detail.patches.join(", "), model: detail.modelId ?? "?" },
			)}
		</Alert>
	);
}
