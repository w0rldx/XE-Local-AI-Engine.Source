import { Anchor, Badge, Group, Stack, Text } from "@mantine/core";
import { useNavigate } from "@tanstack/react-router";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import {
	type DevWorkflowAttemptCost,
	type DevWorkflowNodeAttempt,
	devWorkflowAttemptCostFields,
} from "@/features/devWorkflows/models/DevWorkflowAttempts";
import {
	type DevWorkflowNodeRunDetailResponse,
	formatDevWorkflowDuration,
} from "@/features/devWorkflows/models/DevWorkflowModels";

export interface DevWorkflowNodeAttemptsProps {
	readonly attempts: readonly DevWorkflowNodeAttempt[];
	/**
	 * The node-run row, which is where the FINAL attempt's cost lives: the row carries the last attempt's numbers only,
	 * and every earlier attempt's numbers ride on the retry event that closed it. Neither half is the total on its own.
	 */
	readonly nodeRun?: DevWorkflowNodeRunDetailResponse;
}

/**
 * An attempt whose numbers cannot be added up. Two shapes qualify: one that recorded nothing at all, which is an
 * attempt the loaded event pages never reached, and one billed for provider calls whose usage was never reported —
 * a context-window overflow ends the attempt after N rounds and the provider returns no token counts for any of them.
 * Provider calls of 0 or none with null tokens is NOT that: nothing was spent, so nothing is missing.
 */
function isUnrecorded(cost: DevWorkflowAttemptCost): boolean {
	const recordedNothing = !devWorkflowAttemptCostFields.some((name) => cost[name] != null);
	return recordedNothing || ((cost.providerCalls ?? 0) > 0 && cost.inputTokens == null);
}

/** One attempt's additive numbers on a single line. An absent member is left out; a wall of dashes is not information. */
function costSummary(t: TFunction, cost: DevWorkflowAttemptCost): string {
	const parts: string[] = [];
	const push = (key: string, fallback: string, value: string | undefined) => {
		if (value !== undefined) {
			parts.push(`${t(key, fallback)} ${value}`);
		}
	};
	const count = (value?: number | null) => (value == null ? undefined : value.toLocaleString());
	push("pages.devWorkflows.node.cost.tokensIn", "Input tokens", count(cost.inputTokens));
	push("pages.devWorkflows.node.cost.tokensOut", "Output tokens", count(cost.outputTokens));
	push("pages.devWorkflows.node.cost.reasoning", "Reasoning tokens", count(cost.reasoningTokens));
	push("pages.devWorkflows.node.cost.providerCalls", "Provider calls", count(cost.providerCalls));
	push("pages.devWorkflows.node.cost.toolCalls", "Tool calls", count(cost.toolCalls));
	push(
		"pages.devWorkflows.node.cost.turnTime",
		"Agent turns",
		cost.agentTurnMs == null ? undefined : formatDevWorkflowDuration(cost.agentTurnMs),
	);
	return parts.join(" · ");
}

/**
 * Attempt N → outcome → session link, from the event log (X2, P4 §2.6). The node-run row increments `Attempt` in
 * place, so this is the only surface on which an earlier attempt exists at all.
 *
 * A single attempt renders nothing: the panel header already says "attempt 1 of 3", and a one-row list under it would
 * be the same fact twice. The list appears exactly when there is history the header cannot hold.
 */
export function DevWorkflowNodeAttempts({ attempts, nodeRun }: DevWorkflowNodeAttemptsProps) {
	const { t } = useTranslation();
	const navigate = useNavigate();
	if (attempts.length < 2) {
		return null;
	}

	// The attempt the node-run row is ON takes its numbers from that row; every earlier one takes them from the retry
	// event that closed it. The two never overlap: an attempt the row is still on has not been retried, so no event
	// carries its cost yet.
	const rows = attempts.map((attempt) => {
		const cost: DevWorkflowAttemptCost = nodeRun && attempt.attempt === nodeRun.attempt ? nodeRun : attempt;
		// Three of the nine members have no place on the one-line summary, so a row carrying only those has a record
		// but nothing to print. The line is gated on the printable text, never on the record.
		return { attempt, cost, summary: costSummary(t, cost) };
	});
	// Every member the summary can print, or the total silently drops what the rows above it show.
	const sum = (name: keyof DevWorkflowAttemptCost) => {
		const values = rows.map((row) => row.cost[name]).filter((value): value is number => typeof value === "number");
		return values.length > 0 ? values.reduce((total, value) => total + value, 0) : undefined;
	};
	const totalSummary = costSummary(t, {
		inputTokens: sum("inputTokens"),
		outputTokens: sum("outputTokens"),
		reasoningTokens: sum("reasoningTokens"),
		providerCalls: sum("providerCalls"),
		toolCalls: sum("toolCalls"),
		agentTurnMs: sum("agentTurnMs"),
	});
	// An EARLIER attempt the log cannot account for makes the sum a floor rather than the total. The last row is never
	// that: it is the attempt still running, which has simply not spent anything yet, and calling that partial would
	// put the caveat on every node currently working.
	const partial = rows.slice(0, -1).some((row) => isUnrecorded(row.cost));

	return (
		<SectionCard title={t("pages.devWorkflows.attempts.title", "Attempts")} gap={4} data-testid="dev-workflow-node-attempts">
			{rows.map(({ attempt, summary }) => (
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
						{summary ? (
							<Text size="xs" c="dimmed" data-testid={`dev-workflow-node-attempt-cost-${attempt.attempt}`}>
								{summary}
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
			{totalSummary ? (
				<Text size="xs" mt={4} data-testid="dev-workflow-node-attempts-total">
					{partial
						? t(
								"pages.devWorkflows.attempts.totalPartial",
								"Total across the attempts on record: {{summary}} — at least one earlier attempt left no record, so the real total is higher.",
								{ summary: totalSummary },
							)
						: t("pages.devWorkflows.attempts.total", "Total across attempts: {{summary}}", { summary: totalSummary })}
				</Text>
			) : null}
		</SectionCard>
	);
}
