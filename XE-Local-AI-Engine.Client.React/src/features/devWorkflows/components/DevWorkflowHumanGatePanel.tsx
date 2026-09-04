import { Alert, Anchor, Badge, Button, Group, Stack, Text, Textarea } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import type { TFunction } from "i18next";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { MarkdownView } from "@/core/ui/components/MarkdownView/MarkdownView";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { devWorkflowConflictTypes, readDevWorkflowConflict } from "@/features/devWorkflows/api/DevWorkflowConflict";
import {
	asDevWorkflowDecisionKind,
	type DevWorkflowDecisionKind,
	type DevWorkflowNodeRunDetailResponse,
	type DevWorkflowNodeType,
	devWorkflowNodeAwaitsHuman,
	toDevWorkflowDecisionKinds,
	toDevWorkflowNodeStatus,
	toDevWorkflowNodeType,
} from "@/features/devWorkflows/models/DevWorkflowModels";

export interface DevWorkflowDecisionSubmission {
	readonly decision: DevWorkflowDecisionKind;
	readonly comment?: string;
	readonly operationId: string;
}

export interface DevWorkflowHumanGatePanelProps {
	readonly nodeRun: DevWorkflowNodeRunDetailResponse;
	readonly isSubmitting: boolean;
	readonly error?: unknown;
	/** Artifact id → name, so the evidence reads as "Implementation plan" rather than as a GUID. */
	readonly artifactNameById?: ReadonlyMap<string, string>;
	readonly onDecide: (submission: DevWorkflowDecisionSubmission) => void;
	readonly onShowArtifacts: () => void;
}

/** A rejection or a change request with no reason is unactionable for the run and unauditable for the operator. */
const decisionsRequiringComment: ReadonlySet<DevWorkflowDecisionKind> = new Set<DevWorkflowDecisionKind>([
	"Reject",
	"RequestChanges",
]);

const decisionColors: Partial<Record<DevWorkflowDecisionKind, string>> = {
	Approve: "green",
	Reject: "red",
	Abandon: "red",
};

const COMMENT_MAX = 8000;

/**
 * Only promise the comment reaches the retried work where it actually does. An Agent node always folds it into the
 * next attempt's objective; a DevTask node hands it to the next coder round, and only while the task is being reworked;
 * a Tool node never reads it. A blanket promise on a Tool retry is a lie the operator finds out about afterwards.
 */
function commentHint(
	allowedDecisions: readonly DevWorkflowDecisionKind[],
	nodeType: DevWorkflowNodeType,
	t: TFunction,
): string {
	if (allowedDecisions.includes("Retry")) {
		if (nodeType === "Agent") {
			return t(
				"pages.devWorkflows.gate.commentHintRetry",
				"Required when you reject or ask for changes. A retry reason is passed to the next attempt.",
			);
		}
		if (nodeType === "DevTask") {
			return t(
				"pages.devWorkflows.gate.commentHintRetryDevTask",
				"Required when you reject or ask for changes. A retry reason is handed to the next coder round when the implementation is being reworked.",
			);
		}
	}
	return t("pages.devWorkflows.gate.commentHint", "Required when you reject or ask for changes.");
}

/**
 * The one decision surface, serving BOTH an open human gate (`WaitingForApproval`) and a stopped node needing an
 * intervention (`Blocked`, Y20) — operationally the same situation: the run has halted until a human acts, and the
 * runtime takes both answers through the same endpoint, the same table and the same audit shape.
 *
 * It fails closed. No `pendingDecisionKind` means the runtime is not asking, and no controls render at all; which
 * buttons DO render is `allowedDecisions`, computed server-side from the pinned graph, so the panel never offers a
 * decision that would come back a 409.
 */
export function DevWorkflowHumanGatePanel({
	nodeRun,
	isSubmitting,
	error,
	artifactNameById,
	onDecide,
	onShowArtifacts,
}: DevWorkflowHumanGatePanelProps) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const [comment, setComment] = useState("");

	const status = toDevWorkflowNodeStatus(nodeRun.status);
	const pendingDecisionKind = nodeRun.pendingDecisionKind;
	const isAsking = devWorkflowNodeAwaitsHuman(status) && Boolean(pendingDecisionKind);

	// One id per PENDING DECISION, minted once and reused across every retry of that attempt: it feeds the runtime's
	// query-first (RunId, OperationId) idempotency, so a replay returns the recorded decision instead of deciding
	// twice. Regenerating it on a failed submit would defeat the entire mechanism by making the retry look like a
	// second human act — which the server refuses with the standing decision. The key changes when the runtime asks
	// again (a new attempt, or a different pending kind), and only then is a new id minted.
	const decisionKey = `${nodeRun.id ?? ""}:${nodeRun.attempt ?? 0}:${pendingDecisionKind ?? ""}`;
	const [operation, setOperation] = useState(() => ({ key: decisionKey, id: crypto.randomUUID() }));
	if (isAsking && operation.key !== decisionKey) {
		setOperation({ key: decisionKey, id: crypto.randomUUID() });
		setComment("");
	}

	const conflict = readDevWorkflowConflict(error);
	const standingDecision = asDevWorkflowDecisionKind(conflict?.standingDecision);
	const allowedDecisions = toDevWorkflowDecisionKinds(nodeRun.allowedDecisions);
	const trimmedComment = comment.trim();

	const priorDecisions = (nodeRun.decisions ?? []).toSorted((left, right) => (left.sequence ?? 0) - (right.sequence ?? 0));

	if (!isAsking) {
		// Prior decisions still matter after the fact — several across attempts explain why a node is on attempt 3 —
		// but with nothing pending there is nothing to answer, so the controls are gone rather than disabled.
		return priorDecisions.length > 0 ? (
			<SectionCard title={t("pages.devWorkflows.gate.history", "Decisions")} gap="xs" data-testid="dev-workflow-gate-history">
				<DecisionHistory decisions={priorDecisions} />
			</SectionCard>
		) : null;
	}

	const submit = async (decision: DevWorkflowDecisionKind): Promise<void> => {
		// X10: a Reject with no accepting out-edge does not "reject a step" — it drains the run and cancels it. An
		// operator must not discover that after the fact, so the confirm says so in as many words.
		if (decision === "Reject" && nodeRun.hasRejectBranch !== true) {
			const confirmed = await confirm({
				title: t("pages.devWorkflows.gate.rejectConfirmTitle", "Reject and cancel this run?"),
				description: t(
					"pages.devWorkflows.gate.rejectConfirmBody",
					"This gate has no branch for a rejection, so rejecting cancels the whole run once its live nodes have wound down.",
				),
				confirmationText: t("pages.devWorkflows.decision.Reject", "Reject"),
				cancellationText: t("common.cancel", "Cancel"),
			});
			if (!confirmed) {
				return;
			}
		}
		if (decision === "Abandon") {
			const confirmed = await confirm({
				title: t("pages.devWorkflows.gate.abandonConfirmTitle", "Abandon this node?"),
				description: t(
					"pages.devWorkflows.gate.abandonConfirmBody",
					// The runtime lands an abandoned node run at Failed, not Cancelled — so the copy must not promise it
					// will read as cancelled afterwards.
					"The node is recorded as failed and no further attempt is made. If nothing else can run without it, the run ends too.",
				),
				confirmationText: t("pages.devWorkflows.decision.Abandon", "Abandon"),
				cancellationText: t("common.cancel", "Cancel"),
			});
			if (!confirmed) {
				return;
			}
		}
		onDecide({
			decision,
			comment: trimmedComment.length > 0 ? trimmedComment : undefined,
			operationId: operation.id,
		});
	};

	const isIntervention = status === "Blocked";

	return (
		<SectionCard
			title={
				isIntervention
					? t("pages.devWorkflows.gate.interventionTitle", "This node needs you")
					: t("pages.devWorkflows.gate.title", "Awaiting your decision")
			}
			gap="sm"
			actions={
				<Badge color={isIntervention ? "red" : "orange"} variant="light" data-testid="dev-workflow-gate-badge">
					{isIntervention
						? t("pages.devWorkflows.nodes.needsIntervention", "needs your intervention")
						: t("pages.devWorkflows.nodes.needsDecision", "needs your decision")}
				</Badge>
			}
			data-testid="dev-workflow-gate-panel"
		>
			{/* Y24: the graph node's `instructions` IS the gate prompt — there is no separate prompt field. */}
			{nodeRun.instructions ? <MarkdownView content={nodeRun.instructions} /> : null}

			{/* Evidence first, decision second: these are the artifacts the gate is ABOUT, recorded as the node's
			    artifact uses when it entered WaitingForApproval. An approval with the plan one click away is a
			    different act from an approval with nothing on screen but a prompt. */}
			{(nodeRun.consumedArtifactIds ?? []).length > 0 ? (
				<Stack gap={2} data-testid="dev-workflow-gate-evidence">
					<Text size="xs" fw={500}>
						{t("pages.devWorkflows.gate.evidence", "What you are deciding on")}
					</Text>
					{(nodeRun.consumedArtifactIds ?? []).map((artifactId) => (
						<Anchor
							key={artifactId}
							component="button"
							type="button"
							size="xs"
							ta="left"
							onClick={onShowArtifacts}
							data-testid={`dev-workflow-gate-evidence-${artifactId}`}
						>
							{/* The name comes from the run's artifact feed; until that lands the id is at least a handle. */}
							{artifactNameById?.get(artifactId) ?? artifactId}
						</Anchor>
					))}
				</Stack>
			) : null}

			{isIntervention && nodeRun.failureClass ? (
				<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-gate-failure">
					<Stack gap={4}>
						<Text size="sm">
							{/* Not narrowed client-side, so an unrecognised class from a newer server reads as a plain
							    sentence rather than as a raw PascalCase token. */}
							{t(
								`pages.devWorkflows.failureClass.${nodeRun.failureClass}`,
								t("pages.devWorkflows.failureClass.unknown", "The node failed"),
							)}
						</Text>
						{nodeRun.terminalReason ? (
							<Text size="xs" c="dimmed" style={{ whiteSpace: "pre-wrap" }}>
								{nodeRun.terminalReason}
							</Text>
						) : null}
					</Stack>
				</Alert>
			) : null}

			{priorDecisions.length > 0 ? <DecisionHistory decisions={priorDecisions} /> : null}

			<Textarea
				label={t("pages.devWorkflows.gate.commentLabel", "Comment")}
				description={commentHint(allowedDecisions, toDevWorkflowNodeType(nodeRun.nodeType), t)}
				value={comment}
				maxLength={COMMENT_MAX}
				autosize={true}
				minRows={3}
				onChange={(event) => setComment(event.currentTarget.value)}
				data-testid="dev-workflow-gate-comment"
			/>

			{standingDecision ? (
				<Alert color="yellow" variant="light" data-testid="dev-workflow-gate-already-decided">
					{t("pages.devWorkflows.gate.alreadyDecided", "This was already answered with “{{decision}}”.", {
						decision: t(`pages.devWorkflows.decision.${standingDecision}`, standingDecision),
					})}
				</Alert>
			) : conflict?.conflictType === devWorkflowConflictTypes.invalidTransition ? (
				<Alert color="yellow" variant="light" data-testid="dev-workflow-gate-stale">
					{t("pages.devWorkflows.gate.noLongerPending", "This node has moved on — it is no longer waiting for a decision.")}
				</Alert>
			) : error ? (
				<Alert color="red" variant="light" data-testid="dev-workflow-gate-error">
					{apiErrorMessage(error, t("pages.devWorkflows.gate.failed", "That decision could not be recorded."))}
				</Alert>
			) : null}

			{/* Wraps on a narrow viewport on purpose: a non-wrapping action row once made an approval visible but
			    unanswerable at 390px. */}
			<Group gap="xs" wrap="wrap">
				{allowedDecisions.map((decision) => (
					<Button
						key={decision}
						size="xs"
						color={decisionColors[decision]}
						variant={decision === "Approve" ? "filled" : "light"}
						loading={isSubmitting}
						disabled={isSubmitting || (decisionsRequiringComment.has(decision) && trimmedComment.length === 0)}
						onClick={() => {
							submit(decision).catch(() => undefined);
						}}
						data-testid={`dev-workflow-gate-${decision}`}
					>
						{t(`pages.devWorkflows.decision.${decision}`, decision)}
					</Button>
				))}
			</Group>
			{allowedDecisions.length === 0 ? (
				<Text size="xs" c="dimmed" data-testid="dev-workflow-gate-no-decisions">
					{t("pages.devWorkflows.gate.noDecisions", "This node offers no decision this client can take.")}
				</Text>
			) : null}
		</SectionCard>
	);
}

function DecisionHistory({
	decisions,
}: {
	decisions: readonly NonNullable<DevWorkflowNodeRunDetailResponse["decisions"]>[number][];
}) {
	const { t } = useTranslation();
	return (
		<Stack gap={4} data-testid="dev-workflow-gate-decisions">
			{decisions.map((decision) => {
				const kind = asDevWorkflowDecisionKind(decision.decision);
				return (
					<Group key={decision.id} gap="xs" wrap="wrap" data-testid={`dev-workflow-gate-decision-${decision.id}`}>
						<Badge size="xs" variant="light">
							{kind ? t(`pages.devWorkflows.decision.${kind}`, kind) : (decision.decision ?? "")}
						</Badge>
						<Text size="xs" c="dimmed">
							{t("pages.devWorkflows.gate.decidedMeta", "attempt {{attempt}} · {{subject}} · {{when}}", {
								attempt: decision.attempt ?? 1,
								subject: decision.decidedBySubject ?? t("pages.devWorkflows.gate.unknownSubject", "unknown"),
								when: new Date(decision.decidedAtUtc ?? 0).toLocaleString(),
							})}
						</Text>
						{decision.comment ? <Text size="xs">{decision.comment}</Text> : null}
					</Group>
				);
			})}
		</Stack>
	);
}
