import { Anchor, Badge, Group, Stack, Text } from "@mantine/core";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import type { DevWorkflowNodeAttempt } from "@/features/devWorkflows/models/DevWorkflowAttempts";

export interface DevWorkflowNodeAttemptsProps {
	readonly attempts: readonly DevWorkflowNodeAttempt[];
}

/**
 * Attempt N → outcome → session link, from the event log (X2, P4 §2.6). The node-run row increments `Attempt` in
 * place, so this is the only surface on which an earlier attempt exists at all.
 *
 * A single attempt renders nothing: the panel header already says "attempt 1 of 3", and a one-row list under it would
 * be the same fact twice. The list appears exactly when there is history the header cannot hold.
 */
export function DevWorkflowNodeAttempts({ attempts }: DevWorkflowNodeAttemptsProps) {
	const { t } = useTranslation();
	const navigate = useNavigate();
	if (attempts.length < 2) {
		return null;
	}

	return (
		<SectionCard title={t("pages.devWorkflows.attempts.title", "Attempts")} gap={4} data-testid="dev-workflow-node-attempts">
			{attempts.map((attempt) => (
				<Group key={attempt.attempt} gap="xs" wrap="wrap" data-testid={`dev-workflow-node-attempt-${attempt.attempt}`}>
					<Badge size="xs" variant="light" color="gray">
						{t("pages.devWorkflows.attempts.number", "attempt {{attempt}}", { attempt: attempt.attempt })}
					</Badge>
					<Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
						{/* The event's own outcome token, verbatim: this client narrows no vocabulary it does not own, and a
						    token a newer server invents must still read as itself rather than disappear. */}
						<Text size="xs" c={attempt.outcome ? undefined : "dimmed"}>
							{attempt.outcome ??
								t("pages.devWorkflows.attempts.noEvidence", "no record of this attempt in the events loaded so far")}
						</Text>
						{attempt.interruptedCount > 0 ? (
							<Text size="xs" c="dimmed">
								{t("pages.devWorkflows.attempts.interrupted", "interrupted and re-dispatched {{count}}×", {
									count: attempt.interruptedCount,
								})}
							</Text>
						) : null}
					</Stack>
					{attempt.workSessionId && nodeCapabilities.workSessions ? (
						<Anchor
							component="button"
							type="button"
							size="xs"
							onClick={() =>
								navigate({ to: "/work-sessions/$sessionId", params: { sessionId: attempt.workSessionId ?? "" } })
							}
							data-testid={`dev-workflow-node-attempt-session-${attempt.attempt}`}
						>
							{t("pages.devWorkflows.attempts.session", "transcript")}
						</Anchor>
					) : null}
				</Group>
			))}
		</SectionCard>
	);
}
