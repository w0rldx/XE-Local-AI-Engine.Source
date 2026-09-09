import { Badge, Group, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { formatBytesAsGb } from "@/core/formatting/BytesFormatting";
import { formatDurationCompact } from "@/core/formatting/TimeFormatting";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import type { DevWorkflowNodeRunDetailResponse } from "@/features/devWorkflows/models/DevWorkflowModels";

/** One label/value line. Absent values do not get a row at all: a wall of dashes is not information. */
function CostRow({ label, value, testId }: { readonly label: string; readonly value?: string; readonly testId: string }) {
	if (!value) {
		return null;
	}
	return (
		<Group gap="xs" wrap="nowrap" justify="space-between">
			<Text size="xs" c="dimmed">
				{label}
			</Text>
			<Text size="xs" data-testid={testId}>
				{value}
			</Text>
		</Group>
	);
}

/**
 * What this node run's LAST attempt spent, and where it routed.
 *
 * Two things this pane must not let a reader assume. The numbers are the last attempt ONLY — a re-attempt clears the
 * row, and the earlier attempts live on the run's retry events — so a retried node cost more than this says. And
 * `satisfied` means the out-edge's condition fired, never that the successor ran: a join can still skip on a dead
 * sibling edge, so the caveat is printed rather than left to be inferred.
 */
export function DevWorkflowNodeCostSection({ nodeRun }: { nodeRun: DevWorkflowNodeRunDetailResponse }) {
	const { t } = useTranslation();
	const route = nodeRun.route;
	const toolNames = nodeRun.toolNames ?? [];
	const ranFor =
		nodeRun.startedAtUtc != null && nodeRun.completedAtUtc != null ? nodeRun.completedAtUtc - nodeRun.startedAtUtc : null;
	// The envelope measures a WHOLE agent turn, tool loop included, so the remainder is time outside the turns —
	// queueing after the node started, the settle itself — and is deliberately not labelled as tool time.
	const turnMs = nodeRun.agentTurnMs ?? null;
	// How much of those turns was the local runtime launching and loading rather than generating. Null means no turn
	// went through the warmer at all (cloud-served, or a pre-column row) — unmeasured. Non-null is measured warmer
	// time; the warmer times a cache reuse too and the value truncates to whole ms, so a resident model reads near
	// zero and a zero only says "under 1 ms". The row is drawn for zero, because a measurement is not an absence.
	const readinessMs = nodeRun.modelReadinessMs ?? null;
	// A reading of the BOX at one moment, not a cost of this attempt: what the capacity gate saw free, and what it
	// reserved, at the most recent successful load of the serving model. A warm run inherits that earlier load's
	// figures — readiness above says which. Zero admitted is a real answer, a CPU placement, not an absence.
	const vramFreeAtLoad = nodeRun.vramFreeAtLoadBytes ?? null;
	const vramAdmitted = nodeRun.vramAdmittedBytes ?? null;
	const outsideTurnMs = ranFor != null && turnMs != null ? Math.max(0, ranFor - turnMs) : null;
	const queuedFor =
		nodeRun.queuedAtUtc != null && nodeRun.startedAtUtc != null ? nodeRun.startedAtUtc - nodeRun.queuedAtUtc : null;

	const measured =
		nodeRun.inputTokens != null ||
		nodeRun.outputTokens != null ||
		nodeRun.reasoningTokens != null ||
		nodeRun.estimatedInputTokens != null ||
		nodeRun.providerCalls != null ||
		nodeRun.toolCalls != null ||
		nodeRun.toolSchemaTokens != null ||
		nodeRun.workSessionSteps != null ||
		turnMs != null ||
		readinessMs != null ||
		vramFreeAtLoad != null ||
		vramAdmitted != null ||
		nodeRun.servedModelName != null ||
		toolNames.length > 0;
	if (!measured && !route && queuedFor === null && ranFor === null) {
		return null;
	}

	const count = (value?: number | null) => (value == null ? undefined : value.toLocaleString());
	const duration = (value: number | null) => (value == null ? undefined : formatDurationCompact(value));
	// Zero bytes must still render — a CPU-placed load reserved none — so the absent case is the null one only.
	const bytes = (value: number | null) => (value == null ? undefined : formatBytesAsGb(value));

	return (
		<SectionCard title={t("pages.devWorkflows.node.cost.title", "Cost")} gap={4} data-testid="dev-workflow-node-cost">
			{!measured ? (
				<Text size="xs" c="dimmed" data-testid="dev-workflow-node-cost-none">
					{t("pages.devWorkflows.node.cost.none", "Nothing was recorded for this attempt.")}
				</Text>
			) : null}
			<CostRow
				label={t("pages.devWorkflows.node.cost.tokensIn", "Input tokens")}
				value={count(nodeRun.inputTokens)}
				testId="dev-workflow-node-cost-input"
			/>
			<CostRow
				label={t("pages.devWorkflows.node.cost.tokensOut", "Output tokens")}
				value={count(nodeRun.outputTokens)}
				testId="dev-workflow-node-cost-output"
			/>
			<CostRow
				label={t("pages.devWorkflows.node.cost.reasoning", "Reasoning tokens")}
				value={count(nodeRun.reasoningTokens)}
				testId="dev-workflow-node-cost-reasoning"
			/>
			{/* An estimate from a character profile, so it is worth showing only where the real count is missing. */}
			<CostRow
				label={t("pages.devWorkflows.node.cost.estimated", "Estimated input tokens")}
				value={nodeRun.inputTokens == null ? count(nodeRun.estimatedInputTokens) : undefined}
				testId="dev-workflow-node-cost-estimated"
			/>
			<CostRow
				label={t("pages.devWorkflows.node.cost.providerCalls", "Provider calls")}
				value={count(nodeRun.providerCalls)}
				testId="dev-workflow-node-cost-provider-calls"
			/>
			<CostRow
				label={t("pages.devWorkflows.node.cost.toolCalls", "Tool calls")}
				value={count(nodeRun.toolCalls)}
				testId="dev-workflow-node-cost-tool-calls"
			/>
			<CostRow
				label={t("pages.devWorkflows.node.cost.schemaTokens", "Tool schema tokens")}
				value={count(nodeRun.toolSchemaTokens)}
				testId="dev-workflow-node-cost-schema-tokens"
			/>
			<CostRow
				label={t("pages.devWorkflows.node.cost.steps", "Work-session steps")}
				value={count(nodeRun.workSessionSteps)}
				testId="dev-workflow-node-cost-steps"
			/>
			<CostRow
				label={t("pages.devWorkflows.node.cost.servedModel", "Served by")}
				value={nodeRun.servedModelName ?? undefined}
				testId="dev-workflow-node-cost-served-model"
			/>
			<CostRow
				label={t("pages.devWorkflows.node.cost.queuedFor", "Queued for")}
				value={duration(queuedFor)}
				testId="dev-workflow-node-cost-queued"
			/>
			<CostRow
				label={t("pages.devWorkflows.node.cost.ranFor", "Ran for")}
				value={duration(ranFor)}
				testId="dev-workflow-node-cost-ran"
			/>
			<CostRow
				label={t("pages.devWorkflows.node.cost.turnTime", "Agent turns")}
				value={duration(turnMs)}
				testId="dev-workflow-node-cost-turn-time"
			/>
			<CostRow
				label={t("pages.devWorkflows.node.cost.modelReadiness", "Model readiness")}
				value={duration(readinessMs)}
				testId="dev-workflow-node-cost-model-readiness"
			/>
			<CostRow
				label={t("pages.devWorkflows.node.cost.vramFreeAtLoad", "Free VRAM at load")}
				value={bytes(vramFreeAtLoad)}
				testId="dev-workflow-node-cost-vram-free"
			/>
			<CostRow
				label={t("pages.devWorkflows.node.cost.vramAdmitted", "VRAM admitted")}
				value={bytes(vramAdmitted)}
				testId="dev-workflow-node-cost-vram-admitted"
			/>
			<CostRow
				label={t("pages.devWorkflows.node.cost.outsideTurnTime", "Outside the turns")}
				value={duration(outsideTurnMs)}
				testId="dev-workflow-node-cost-outside-turn-time"
			/>

			{toolNames.length > 0 ? (
				<Stack gap={4} mt={4}>
					<Text size="xs" c="dimmed">
						{t("pages.devWorkflows.node.cost.toolNames", "Tools called")}
					</Text>
					<Group gap={4} wrap="wrap" data-testid="dev-workflow-node-cost-tool-names">
						{toolNames.map((name) =>
							// The collector closes a trimmed list with this element. It is a marker, not a tool, and drawing it
							// as one would invent a tool nobody called.
							name === "…" ? (
								<Text key="truncated" size="xs" c="dimmed" data-testid="dev-workflow-node-cost-tool-names-truncated">
									{t("pages.devWorkflows.node.cost.toolNamesTruncated", "…and more")}
								</Text>
							) : (
								<Badge key={name} size="xs" variant="light" color="gray">
									{name}
								</Badge>
							),
						)}
					</Group>
				</Stack>
			) : null}

			{route ? (
				<Stack gap={4} mt={4} data-testid="dev-workflow-node-cost-route">
					<Group gap="xs" wrap="wrap">
						<Text size="xs" c="dimmed">
							{t("pages.devWorkflows.node.cost.route", "Route taken")}
						</Text>
						{route.truncated ? (
							<Badge size="xs" variant="light" color="orange" data-testid="dev-workflow-node-cost-route-truncated">
								{t("pages.devWorkflows.node.cost.routeTruncated", "shortened")}
							</Badge>
						) : null}
					</Group>
					<Text size="xs" data-testid="dev-workflow-node-cost-route-satisfied">
						{t("pages.devWorkflows.node.cost.routeSatisfied", "satisfied → {{nodes}}", {
							nodes: (route.satisfied ?? []).join(", ") || "—",
						})}
					</Text>
					<Text size="xs" data-testid="dev-workflow-node-cost-route-dead">
						{t("pages.devWorkflows.node.cost.routeDead", "not taken → {{nodes}}", {
							nodes: (route.dead ?? []).join(", ") || "—",
						})}
					</Text>
					{/* Only a waived skip fills this, so the row is drawn only when there is one — an always-present
					    "excused → —" would read as a claim about every other node run that there was nothing to excuse. */}
					{(route.waived ?? []).length > 0 ? (
						<Text size="xs" data-testid="dev-workflow-node-cost-route-waived">
							{t("pages.devWorkflows.node.cost.routeWaived", "excused → {{nodes}}", {
								nodes: (route.waived ?? []).join(", "),
							})}
						</Text>
					) : null}
					{route.gateAnswer ? (
						<Text size="xs" data-testid="dev-workflow-node-cost-route-gate-answer">
							{t("pages.devWorkflows.node.cost.gateAnswer", "answered {{answer}}", {
								answer: t(`pages.devWorkflows.decision.${route.gateAnswer}`, route.gateAnswer),
							})}
						</Text>
					) : null}
					<Text size="xs" c="dimmed">
						{t(
							"pages.devWorkflows.node.cost.routeGateNote",
							"A satisfied edge means its condition fired, not that the successor ran — a join can still skip on a dead sibling edge. An excused edge left this node's own skip without killing an All join, but it admits nobody on its own.",
						)}
					</Text>
				</Stack>
			) : null}
		</SectionCard>
	);
}
