import { Alert, Button, Collapse, Group, Stack, Text, Textarea } from "@mantine/core";
import { IconChevronDown, IconChevronRight } from "@tabler/icons-react";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { graphWorkflowConflictTypes, readGraphWorkflowConflict } from "@/features/graphWorkflows/api/GraphWorkflowConflict";
import {
	asGraphWorkflowDecisionKind,
	type GraphWorkflowDecisionKind,
	type GraphWorkflowNodeRunResponse,
	graphWorkflowDecisionKinds,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { useDecideGraphWorkflowNodeRun } from "@/features/graphWorkflows/queries/useGraphWorkflows";

export interface GraphWorkflowDecisionPanelProps {
	readonly runId: string;
	/** The node run as the server last described it. Nothing renders unless it carries a `pendingDecisionKind`. */
	readonly nodeRun: GraphWorkflowNodeRunResponse;
	/** From the Pause node's config. Empty falls back to the two v1 kinds rather than offering no control at all. */
	readonly allowedDecisions: readonly GraphWorkflowDecisionKind[];
	readonly requireComment: boolean;
	readonly prompt?: string;
}

/** `MaxDecisionComment` server-side: past this the decide endpoint answers 400. */
const COMMENT_MAX = 500;

const decisionColors: Partial<Record<GraphWorkflowDecisionKind, string>> = {
	Approve: "green",
	Reject: "red",
};

/** `undefined` for "nothing to send"; `null` for "the operator typed something that is not a JSON object". */
function parsePayload(text: string): unknown {
	const trimmed = text.trim();
	if (trimmed.length === 0) {
		return undefined;
	}
	try {
		const parsed: unknown = JSON.parse(trimmed);
		// The wire refuses a bare scalar or an array here (400, `payload not an object`), so this blocks the submit
		// rather than posting a body the server is going to reject.
		return parsed !== null && typeof parsed === "object" && !Array.isArray(parsed) ? parsed : null;
	} catch {
		return null;
	}
}

/**
 * The one decision surface for a parked Pause node. It fails closed: no `pendingDecisionKind` means the runtime is not
 * asking and no control renders at all.
 *
 * The `operationId` is minted ONCE per pending decision and reused across every retry of it, which is the client half
 * of the runtime's `(RunId, OperationId)` idempotency: a re-sent body replays as the recorded decision, while a fresh
 * id at the same gate is a SECOND human act and comes back 409 with the decision that stands. The key changes only
 * when the runtime asks again — a new attempt, or a different pending kind.
 */
export function GraphWorkflowDecisionPanel({
	runId,
	nodeRun,
	allowedDecisions,
	requireComment,
	prompt,
}: GraphWorkflowDecisionPanelProps) {
	const { t } = useTranslation();
	const decide = useDecideGraphWorkflowNodeRun();
	const [comment, setComment] = useState("");
	const [payloadText, setPayloadText] = useState("");
	const [payloadError, setPayloadError] = useState(false);
	const [advancedOpen, setAdvancedOpen] = useState(false);

	const pendingDecisionKind = nodeRun.pendingDecisionKind ?? undefined;
	const nodeKey = nodeRun.nodeKey ?? "";
	const decisionKey = `${nodeKey}:${nodeRun.attempt ?? 0}:${pendingDecisionKind ?? ""}`;
	const [operation, setOperation] = useState(() => ({ key: decisionKey, id: crypto.randomUUID() }));
	// The mutation's error outlives the decision it belongs to. Without this, a 409 on one attempt keeps the buttons
	// disabled and "already answered" on screen when the runtime asks again on the next attempt of the same node.
	const resetDecide = decide.reset;
	const answeredKey = useRef(decisionKey);
	useEffect(() => {
		if (answeredKey.current !== decisionKey) {
			answeredKey.current = decisionKey;
			resetDecide();
		}
	}, [decisionKey, resetDecide]);
	if (pendingDecisionKind && operation.key !== decisionKey) {
		setOperation({ key: decisionKey, id: crypto.randomUUID() });
		setComment("");
		setPayloadText("");
		setPayloadError(false);
	}

	const conflict = readGraphWorkflowConflict(decide.error);
	const standingDecision = asGraphWorkflowDecisionKind(conflict?.standingDecision);
	const alreadyDecided = conflict?.conflictType === graphWorkflowConflictTypes.gateAlreadyDecided;
	const offered = allowedDecisions.length > 0 ? allowedDecisions : graphWorkflowDecisionKinds;
	const trimmedComment = comment.trim();
	const commentMissing = requireComment && trimmedComment.length === 0;
	const commentTooLong = comment.length > COMMENT_MAX;

	if (!pendingDecisionKind) {
		return null;
	}

	const submit = (decision: GraphWorkflowDecisionKind): void => {
		const payload = parsePayload(payloadText);
		if (payload === null) {
			setPayloadError(true);
			setAdvancedOpen(true);
			return;
		}
		setPayloadError(false);
		decide.mutate({
			path: { runId, nodeKey },
			body: {
				operationId: operation.id,
				decision,
				comment: trimmedComment.length > 0 ? trimmedComment : undefined,
				payload,
			},
		});
	};

	return (
		<SectionCard
			title={t("pages.graphWorkflows.decision.title", "Awaiting your decision")}
			gap="sm"
			data-testid="graph-workflow-decision-panel"
		>
			{prompt ? (
				<Text size="sm" style={{ whiteSpace: "pre-wrap" }} data-testid="graph-workflow-decision-prompt">
					{prompt}
				</Text>
			) : null}

			<Textarea
				label={t("pages.graphWorkflows.decision.commentLabel", "Comment")}
				description={
					requireComment
						? t("pages.graphWorkflows.decision.commentRequired", "This node requires a comment with the decision.")
						: t("pages.graphWorkflows.decision.commentOptional", "Optional. It is recorded with the decision.")
				}
				error={commentTooLong ? t("pages.graphWorkflows.decision.commentTooLong", "Keep the comment to {{max}} characters or fewer.", { max: COMMENT_MAX }) : undefined}
				value={comment}
				autosize={true}
				minRows={3}
				onChange={(event) => setComment(event.currentTarget.value)}
				data-testid="graph-workflow-decision-comment"
			/>
			<Text size="xs" c="dimmed" data-testid="graph-workflow-decision-comment-count">
				{t("pages.graphWorkflows.decision.commentCount", "{{used}} of {{max}} characters", {
					used: comment.length,
					max: COMMENT_MAX,
				})}
			</Text>

			{/* Q5: the payload is on the wire regardless, and an operator whose downstream node reads
			    `upstream.review.payload` has no other way to set it — but it is not what a decision usually needs. */}
			<Button
				size="xs"
				variant="subtle"
				leftSection={advancedOpen ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
				aria-expanded={advancedOpen}
				onClick={() => setAdvancedOpen((open) => !open)}
				data-testid="graph-workflow-decision-advanced-toggle"
			>
				{t("pages.graphWorkflows.decision.advanced", "Advanced")}
			</Button>
			<Collapse expanded={advancedOpen}>
				<Stack gap={4}>
					<Text size="xs" fw={500}>
						{t("pages.graphWorkflows.decision.payloadLabel", "Payload (JSON object)")}
					</Text>
					<CodeEditor
						value={payloadText}
						language="json"
						height={160}
						onChange={setPayloadText}
						aria-label={t("pages.graphWorkflows.decision.payloadLabel", "Payload (JSON object)")}
						data-testid="graph-workflow-decision-payload"
					/>
					{payloadError ? (
						<Text size="xs" c="red" data-testid="graph-workflow-decision-payload-error">
							{t("pages.graphWorkflows.decision.payloadNotObject", "Enter a JSON object, or leave it empty.")}
						</Text>
					) : null}
				</Stack>
			</Collapse>

			{alreadyDecided ? (
				<Alert color="yellow" variant="light" data-testid="graph-workflow-decision-already-decided">
					{t("pages.graphWorkflows.decision.alreadyDecided", "This was already answered with “{{decision}}”.", {
						decision: standingDecision
							? t(`pages.graphWorkflows.decision.${standingDecision}`, standingDecision)
							: (conflict?.standingDecision ?? ""),
					})}
				</Alert>
			) : conflict?.conflictType === graphWorkflowConflictTypes.runConflict ? (
				<Alert color="yellow" variant="light" data-testid="graph-workflow-decision-stale">
					{t("pages.graphWorkflows.decision.runMovedOn", "This run has moved on — it is no longer waiting for this decision.")}
				</Alert>
			) : decide.error ? (
				<Alert color="red" variant="light" data-testid="graph-workflow-decision-error">
					{apiErrorMessage(decide.error, t("pages.graphWorkflows.decision.failed", "That decision could not be recorded."))}
				</Alert>
			) : null}

			{/* Wraps on a narrow viewport on purpose: a non-wrapping action row makes a decision visible but
			    unanswerable at 390px. */}
			<Group gap="xs" wrap="wrap">
				{offered.map((decision) => (
					<Button
						key={decision}
						size="xs"
						color={decisionColors[decision]}
						variant={decision === "Approve" ? "filled" : "light"}
						loading={decide.isPending}
						// Once the server has said a decision stands, every further click earns the same 409.
						disabled={decide.isPending || alreadyDecided || commentMissing || commentTooLong}
						onClick={() => submit(decision)}
						data-testid={`graph-workflow-decision-${decision}`}
					>
						{t(`pages.graphWorkflows.decision.${decision}`, decision)}
					</Button>
				))}
			</Group>
		</SectionCard>
	);
}
