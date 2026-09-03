import {
	ActionIcon,
	Badge,
	Button,
	Collapse,
	Group,
	Paper,
	Stack,
	Text,
	Tooltip,
} from "@mantine/core";
import {
	IconCheck,
	IconChevronDown,
	IconChevronUp,
	IconFlask,
	IconPencil,
	IconX,
} from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import {
	memoryScopeColors,
	memoryScopeFallbacks,
	sourceFallbacks,
} from "@/features/agents/components/PlaybookActionDisplay";
import type {
	EvalResult,
	PlaybookAction,
} from "@/features/agents/models/PlaybookActionModels";

function toConfidencePercent(confidence: number): string {
	return `${Math.round(confidence * 100)}%`;
}

// Why the Approve/Promote control is gated, derived from the row's evalResult. Drives both the
// disabled state and the tooltip copy: no eval has run, the eval is stale (ran against an older version), the
// candidate regressed a prior-good case, or the gate is satisfied (passed).
type PromoteGateReason = "needsEval" | "stale" | "regressed" | "passed";

function promoteGateReason(action: PlaybookAction): PromoteGateReason {
	const result = action.evalResult;
	if (result === null) {
		return "needsEval";
	}
	if (result.actionVersionAtEval !== action.version) {
		return "stale";
	}
	return result.passed ? "passed" : "regressed";
}

// English fallback copy for the gated-Approve tooltip (the i18n key carries the localized text). Keyed by gate
// reason; "passed" maps to empty since the tooltip is suppressed when the gate is satisfied.
const gateReasonFallbacks: Record<PromoteGateReason, string> = {
	needsEval: "Run the eval before approving this suggestion.",
	stale: "This suggestion changed since the last eval. Re-run the eval before approving.",
	regressed: "The eval regressed a prior-good case. Resolve the regression before approving.",
	passed: "",
};

function gateReasonFallback(reason: PromoteGateReason): string {
	return gateReasonFallbacks[reason];
}

interface EvalResultSummaryProps {
	actionId: string;
	evalResult: EvalResult | null;
}

// Render the eval-gate outcome for a Suggested action: a pass/fail badge with the
// regressed/golden case counts and an expandable list of the regressed cases (goldenCaseId + how it was scored).
// Renders nothing until an eval has run (evalResult null) — the gated Approve tooltip already explains "run eval".
function EvalResultSummary({ actionId, evalResult }: EvalResultSummaryProps) {
	const { t } = useTranslation();
	const [open, setOpen] = useState(false);

	if (evalResult === null) {
		return null;
	}

	const regressedCases = evalResult.cases.filter((evalCase) => evalCase.regressed);

	return (
		<Stack gap={2} data-testid={`playbook-suggested-eval-${actionId}`}>
			<Group gap="xs" align="center" wrap="wrap">
				<Badge
					size="xs"
					variant="light"
					color={evalResult.passed ? "teal" : "red"}
					data-testid={`playbook-suggested-eval-status-${actionId}`}
				>
					{evalResult.passed
						? t("pages.agents.playbook.eval.passed", "Eval passed")
						: t("pages.agents.playbook.eval.failed", "Eval failed")}
				</Badge>
				<Text size="xs" c="dimmed" data-testid={`playbook-suggested-eval-counts-${actionId}`}>
					{t("pages.agents.playbook.eval.counts", "{{regressed}} regressed / {{golden}} golden cases", {
						regressed: evalResult.regressedCaseCount,
						golden: evalResult.goldenCaseCount,
					})}
				</Text>
			</Group>
			{evalResult.goldenCaseTotal > evalResult.goldenCaseCount ? (
				<Text size="xs" c="dimmed" data-testid={`playbook-suggested-eval-truncated-${actionId}`}>
					{t("pages.agents.playbook.eval.truncated", "Evaluated {{evaluated}} of {{total}} golden cases.", {
						evaluated: evalResult.goldenCaseCount,
						total: evalResult.goldenCaseTotal,
					})}
				</Text>
			) : null}
			{regressedCases.length > 0 ? (
				<Stack gap={2}>
					<Button
						size="compact-xs"
						variant="subtle"
						color="gray"
						leftSection={open ? <IconChevronUp size={12} /> : <IconChevronDown size={12} />}
						onClick={() => setOpen((current) => !current)}
						data-testid={`playbook-suggested-eval-toggle-${actionId}`}
					>
						{t("pages.agents.playbook.eval.regressedToggle", "Show {{count}} regressed cases", {
							count: regressedCases.length,
						})}
					</Button>
					<Collapse expanded={open}>
						<Stack gap={2} data-testid={`playbook-suggested-eval-regressed-${actionId}`}>
							{regressedCases.map((evalCase) => (
								<Text key={evalCase.goldenCaseId} size="xs" c="dimmed" style={{ wordBreak: "break-all" }}>
									{t("pages.agents.playbook.eval.regressedCase", "{{id}} (scored by {{scoredBy}})", {
										id: evalCase.goldenCaseId,
										scoredBy: t(`pages.agents.playbook.eval.scoredBy.${evalCase.scoredBy}`, evalCase.scoredBy),
									})}
								</Text>
							))}
						</Stack>
					</Collapse>
				</Stack>
			) : null}
		</Stack>
	);
}

export interface SuggestedActionRowProps {
	action: PlaybookAction;
	disabled: boolean;
	// True while THIS row's eval is in flight (drives the Run-eval button's loading spinner).
	isEvaluating: boolean;
	onApprove: () => void;
	onEdit: () => void;
	onReject: () => void;
	onRunEval: () => void;
}

// One analysis-proposed (Suggested) action awaiting human review. Surfaces the provenance
// ("Analysis"), the analysis confidence as a percent, the proposed behavior, and an evidence affordance: a
// "Based on N feedback items" summary that expands to the cited ids and points the operator to the feedback
// insights panel mounted on the same page. Carries Approve (→ promote), Edit (→ existing edit form/PUT), and
// Reject (→ archive) controls.
export function SuggestedActionRow({ action, disabled, isEvaluating, onApprove, onEdit, onReject, onRunEval }: SuggestedActionRowProps) {
	const { t } = useTranslation();
	const [evidenceOpen, setEvidenceOpen] = useState(false);

	const feedbackIds = action.sourceFeedbackIds ?? [];
	const evidenceCount = feedbackIds.length;

	// The eval gate. Approve is disabled until the latest eval passed against the action's current
	// version; the tooltip explains why (no eval yet / regressed / stale).
	const gateReason = promoteGateReason(action);
	const canPromote = gateReason === "passed";
	const promoteTooltip = canPromote ? null : t(`pages.agents.playbook.eval.gate.${gateReason}`, gateReasonFallback(gateReason));

	// A Suggested candidate may be Analysis-proposed OR Extracted from a run; surface its real provenance + scope.
	// A Failure-scope candidate (negative guidance) gets the red-border treatment like its enabled counterpart.
	const isFailureScope = action.memoryScope === "Failure";

	return (
		<Paper
			withBorder={true}
			p="xs"
			key={action.id}
			style={isFailureScope ? { borderColor: "var(--mantine-color-red-4)" } : undefined}
			data-testid={`playbook-suggested-${action.id}`}
		>
			<Stack gap={6}>
				<Group justify="space-between" align="flex-start" wrap="nowrap">
					<Stack gap={4} style={{ flex: 1, minWidth: 0 }}>
						<Group gap="xs" align="center" wrap="wrap">
							<Badge size="xs" variant="light" color="grape" data-testid={`playbook-suggested-source-${action.id}`}>
								{t(`pages.agents.playbook.source.${action.source}`, sourceFallbacks[action.source])}
							</Badge>
							{action.memoryScope ? (
								<Badge
									size="xs"
									variant="light"
									color={memoryScopeColors[action.memoryScope]}
									data-testid={`playbook-suggested-scope-${action.id}`}
								>
									{t(`pages.agents.playbook.scope.${action.memoryScope}`, memoryScopeFallbacks[action.memoryScope])}
								</Badge>
							) : null}
							{action.confidence !== null ? (
								<Badge size="xs" variant="outline" color="blue" data-testid={`playbook-suggested-confidence-${action.id}`}>
									{t("pages.agents.playbook.confidenceLabel", "Confidence {{value}}", {
										value: toConfidencePercent(action.confidence),
									})}
								</Badge>
							) : null}
						</Group>
						<Text size="sm">{action.behavior}</Text>
						{action.triggerCondition ? (
							<Text size="xs" c="dimmed">
								{t("pages.agents.playbook.triggerLabel", "When: {{trigger}}", {
									trigger: action.triggerCondition,
								})}
							</Text>
						) : null}
						<Stack gap={2}>
							{evidenceCount > 0 ? (
								<Button
									size="compact-xs"
									variant="subtle"
									color="gray"
									leftSection={evidenceOpen ? <IconChevronUp size={12} /> : <IconChevronDown size={12} />}
									onClick={() => setEvidenceOpen((open) => !open)}
									data-testid={`playbook-suggested-evidence-toggle-${action.id}`}
								>
									{t("pages.agents.playbook.evidenceSummary", "Based on {{count}} feedback items", {
										count: evidenceCount,
									})}
								</Button>
							) : (
								<Text size="xs" c="dimmed" data-testid={`playbook-suggested-evidence-empty-${action.id}`}>
									{t("pages.agents.playbook.evidenceEmpty", "No cited feedback items.")}
								</Text>
							)}
							{evidenceCount > 0 ? (
								<Collapse expanded={evidenceOpen}>
									<Stack gap={2} data-testid={`playbook-suggested-evidence-${action.id}`}>
										<Text size="xs" c="dimmed">
											{t("pages.agents.playbook.evidenceHint", "Review these items in the Feedback insights panel below.")}
										</Text>
										{feedbackIds.map((feedbackId) => (
											<Text key={feedbackId} size="xs" c="dimmed" style={{ wordBreak: "break-all" }}>
												{feedbackId}
											</Text>
										))}
									</Stack>
								</Collapse>
							) : null}
							<EvalResultSummary actionId={action.id} evalResult={action.evalResult} />
						</Stack>
					</Stack>
					<Group gap={4} wrap="nowrap">
						<ActionIcon
							aria-label={t("pages.agents.playbook.editAria", "Edit action")}
							variant="subtle"
							size="sm"
							disabled={disabled}
							onClick={onEdit}
							data-testid={`playbook-suggested-edit-${action.id}`}
						>
							<IconPencil size={14} />
						</ActionIcon>
					</Group>
				</Group>
				<Group gap="xs">
					<Button
						size="xs"
						variant="default"
						leftSection={<IconFlask size={14} />}
						loading={isEvaluating}
						disabled={disabled}
						onClick={onRunEval}
						data-testid={`playbook-suggested-run-eval-${action.id}`}
					>
						{t("pages.agents.playbook.eval.runButton", "Run eval")}
					</Button>
					{/* The Approve/Promote control is eval-gated: disabled until the latest eval passed for the action's
					    current version. A Tooltip explains why when the gate blocks. Wrap the button so the tooltip still
					    shows for a disabled control. */}
					<Tooltip
						label={promoteTooltip ?? ""}
						disabled={canPromote}
						withArrow={true}
						data-testid={`playbook-suggested-approve-tooltip-${action.id}`}
					>
						<Button
							size="xs"
							variant="light"
							color="teal"
							leftSection={<IconCheck size={14} />}
							disabled={disabled || !canPromote}
							onClick={onApprove}
							data-testid={`playbook-suggested-approve-${action.id}`}
						>
							{t("pages.agents.playbook.approveButton", "Approve")}
						</Button>
					</Tooltip>
					<Button
						size="xs"
						variant="subtle"
						color="red"
						leftSection={<IconX size={14} />}
						disabled={disabled}
						onClick={onReject}
						data-testid={`playbook-suggested-reject-${action.id}`}
					>
						{t("pages.agents.playbook.rejectButton", "Reject")}
					</Button>
				</Group>
			</Stack>
		</Paper>
	);
}
