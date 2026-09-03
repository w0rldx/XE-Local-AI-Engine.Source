import { Alert, Badge, Button, Code, Group, Loader, ScrollArea, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { DevWorkflowAgentNodePanel } from "@/features/devWorkflows/components/DevWorkflowAgentNodePanel";
import { DevWorkflowDevTaskNodePanel } from "@/features/devWorkflows/components/DevWorkflowDevTaskNodePanel";
import {
	type DevWorkflowDecisionSubmission,
	DevWorkflowHumanGatePanel,
} from "@/features/devWorkflows/components/DevWorkflowHumanGatePanel";
import { DevWorkflowNodeAttempts } from "@/features/devWorkflows/components/DevWorkflowNodeAttempts";
import { DevWorkflowNodeStatusBadge } from "@/features/devWorkflows/components/DevWorkflowStatusBadge";
import { DevWorkflowStructuralNodePanel } from "@/features/devWorkflows/components/DevWorkflowStructuralNodePanel";
import { DevWorkflowToolNodePanel } from "@/features/devWorkflows/components/DevWorkflowToolNodePanel";
import {
	devWorkflowAttemptEventTypes,
	devWorkflowNodeAttempts,
	devWorkflowNodeEvents,
	devWorkflowRoutedDetail,
} from "@/features/devWorkflows/models/DevWorkflowAttempts";
import {
	type DevWorkflowNodeRunDetailResponse,
	type DevWorkflowRunEventResponse,
	type DevWorkflowRunResponse,
	devWorkflowAttemptCounts,
	devWorkflowAttemptLabel,
	isSettledDevWorkflowNodeStatus,
	toDevWorkflowNodeStatus,
	toDevWorkflowNodeType,
} from "@/features/devWorkflows/models/DevWorkflowModels";

export interface DevWorkflowNodePanelProps {
	readonly nodeRun?: DevWorkflowNodeRunDetailResponse;
	readonly isPending: boolean;
	readonly loadError?: unknown;
	readonly isDeciding: boolean;
	readonly decideError?: unknown;
	/** Artifact id → name, for the gate's evidence list. Empty until the run's artifact feed lands. */
	readonly artifactNameById?: ReadonlyMap<string, string>;
	/**
	 * The run's loaded event pages, and the run's own node-run rows. Both are the panel's only source for facts the
	 * node-run DETAIL response does not carry: attempt history lives in the log (X2), and a structural node's
	 * dependencies and branches live in its siblings' rows and the pinned graph's edges.
	 */
	readonly events?: readonly DevWorkflowRunEventResponse[];
	readonly run?: DevWorkflowRunResponse;
	readonly onDecide: (submission: DevWorkflowDecisionSubmission) => void;
	readonly onShowArtifacts: () => void;
	/** Clears `?node=`, which is what brings the artifacts/events tabs back into this zone. */
	readonly onClose: () => void;
}

/**
 * The right-zone pane for the selected node-run. It dispatches on node type, and everything above that dispatch is the
 * same for all seven: the header, the cascade-rerun account, the gate controls and the attempt history.
 *
 * Where each kind's evidence comes from is the distinction that matters. A Tool node renders its own report and an
 * Agent node its own transcript, because both are workflow-owned and neither has another home. A DevTask node stays a
 * LINK-OUT: the Dev Mode evidence chain exists at its own route and re-hosting it would fork the one place the
 * hash-locked apply gate is rendered (O13).
 */
export function DevWorkflowNodePanel({
	nodeRun,
	isPending,
	loadError,
	isDeciding,
	decideError,
	artifactNameById,
	events = [],
	run,
	onDecide,
	onShowArtifacts,
	onClose,
}: DevWorkflowNodePanelProps) {
	const { t } = useTranslation();
	const nodeEvents = useMemo(() => devWorkflowNodeEvents(events, nodeRun?.id), [events, nodeRun?.id]);
	const attempts = useMemo(() => devWorkflowNodeAttempts(nodeEvents, nodeRun?.attempt ?? 1), [nodeEvents, nodeRun?.attempt]);
	const interruptedCount = attempts.reduce((total, attempt) => total + attempt.interruptedCount, 0);

	if (isPending) {
		return <Loader size="sm" data-testid="dev-workflow-node-panel-loading" />;
	}
	if (loadError || !nodeRun) {
		return (
			<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-node-panel-error">
				{apiErrorMessage(loadError, t("pages.devWorkflows.node.loadFailed", "This node could not be loaded."))}
			</Alert>
		);
	}

	const status = toDevWorkflowNodeStatus(nodeRun.status);
	const nodeType = toDevWorkflowNodeType(nodeRun.nodeType);
	const producedCount = nodeRun.producedArtifactIds?.length ?? 0;

	return (
		<ScrollArea h="100%" data-testid="dev-workflow-node-panel">
			<Stack gap="md" pr="xs">
				<Button size="xs" variant="subtle" onClick={onClose} data-testid="dev-workflow-node-panel-close">
					{t("pages.devWorkflows.node.back", "Back to artifacts and events")}
				</Button>
				<SectionCard gap="xs">
					<Group gap="xs" wrap="wrap">
						<Text fw={600} style={{ flex: 1, minWidth: 0 }} lineClamp={2} data-testid="dev-workflow-node-panel-label">
							{nodeRun.label}
						</Text>
						<DevWorkflowNodeStatusBadge status={status} testId="dev-workflow-node-panel-status" />
					</Group>
					<Group gap={4} wrap="wrap">
						<Badge size="xs" variant="light" color="gray">
							{t(`pages.devWorkflows.nodeType.${nodeType}`, nodeType)}
						</Badge>
						<Text size="xs" c="dimmed">
							{devWorkflowAttemptLabel(
								t,
								devWorkflowAttemptCounts(nodeRun.attempt, nodeRun.maxAttempts, nodeRun.operatorRetries),
							)}
						</Text>
						{/* Two different facts, deliberately side by side. A node that survived an engine restart is the whole
						    point of this module, and `sessionResumes` is NOT that number — it counts the session being
						    parked at its step budget, which happens to plenty of nodes that were never interrupted. */}
						{interruptedCount > 0 ? (
							<Text size="xs" c="dimmed" data-testid="dev-workflow-node-interrupted">
								{t("pages.devWorkflows.node.interrupted", "interrupted and re-dispatched {{count}}×", {
									count: interruptedCount,
								})}
							</Text>
						) : null}
						{(nodeRun.sessionResumes ?? 0) > 0 ? (
							<Text size="xs" c="dimmed" data-testid="dev-workflow-node-resumes">
								{t("pages.devWorkflows.node.resumes", "paused for step budget {{count}}×", {
									count: nodeRun.sessionResumes ?? 0,
								})}
							</Text>
						) : null}
					</Group>
					{producedCount > 0 ? (
						<Button size="xs" variant="subtle" onClick={onShowArtifacts} data-testid="dev-workflow-node-artifacts">
							{t("pages.devWorkflows.node.producedArtifacts", "produced {{count}} artifact(s)", { count: producedCount })}
						</Button>
					) : null}
				</SectionCard>

				{/* Failed and Blocked both need the reason. The gate panel repeats it for Blocked because that is where the
				    intervention controls are; a Failed node has no controls, so this is its only place to say why. */}
				{status === "Failed" && nodeRun.failureClass ? (
					<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-node-failure">
						<Stack gap={4}>
							<Text size="sm">
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

				<CascadeRerunNotice nodeRun={nodeRun} nodeEvents={nodeEvents} events={events} run={run} />

				<DevWorkflowHumanGatePanel
					nodeRun={nodeRun}
					isSubmitting={isDeciding}
					error={decideError}
					artifactNameById={artifactNameById}
					onDecide={onDecide}
					onShowArtifacts={onShowArtifacts}
				/>

				<DevWorkflowNodeAttempts attempts={attempts} />

				{nodeType === "Agent" ? <DevWorkflowAgentNodePanel nodeRun={nodeRun} /> : null}
				{nodeType === "Tool" ? <DevWorkflowToolNodePanel nodeRun={nodeRun} onShowArtifacts={onShowArtifacts} /> : null}
				{nodeType === "DevTask" ? <DevWorkflowDevTaskNodePanel nodeRun={nodeRun} /> : null}
				{/* A HumanGate is a gate too, and once it has been ANSWERED the branch it sent the run down is the fact an
				    operator opened it for — `feature-development-v1` ships only HumanGates, so without this the branch
				    list is unreachable from the one template that matters. It appears BELOW the decision controls and
				    only when the node has settled: while the gate is still asking, every successor is Pending and the
				    list would name no branch at all. Same panel, same edges, same untaken-branch semantics as `Gate`. */}
				{nodeType === "Gate" ||
				nodeType === "Parallel" ||
				nodeType === "Join" ||
				(nodeType === "HumanGate" && isSettledDevWorkflowNodeStatus(status)) ? (
					<DevWorkflowStructuralNodePanel nodeRun={nodeRun} nodeType={nodeType} run={run} />
				) : null}

				<AppliedRuleSetsSection nodeRun={nodeRun} />

				<ObjectiveSection nodeRun={nodeRun} />
			</Stack>
		</ScrollArea>
	);
}

/**
 * Why completed work went back to `Pending` (C45/X9). A fix loop resets a whole cascade, so a node that had succeeded
 * is put back to run again — and without this it simply un-completes with no account of itself, which reads as the
 * module losing work.
 *
 * The evidence is `node.retry.routed`, which B4 emits against the node that FAILED, naming the target the loop routed
 * back to. This node's own log says only that it was re-scheduled, so the two are joined on sequence: the routed event
 * immediately preceding this node's latest reset is the round that reset it. A routed event this node's own row
 * produced is not a cascade — it is this node failing and retrying itself, which the attempts list already says.
 */
function CascadeRerunNotice({
	nodeRun,
	nodeEvents,
	events,
	run,
}: {
	readonly nodeRun: DevWorkflowNodeRunDetailResponse;
	readonly nodeEvents: readonly DevWorkflowRunEventResponse[];
	readonly events: readonly DevWorkflowRunEventResponse[];
	readonly run?: DevWorkflowRunResponse;
}) {
	const { t } = useTranslation();
	const latestReset = nodeEvents
		.filter((event) => event.eventType === devWorkflowAttemptEventTypes.retryScheduled)
		.at(-1);
	if (!latestReset) {
		return null;
	}

	// Only a routed event that names THIS node as its target explains this node's reset. Proximity alone does not: a
	// same-node retry writes `node.retry.scheduled` with no routed event of its own, so the newest routed event
	// anywhere in the run sits at-or-before it and would be read as the cause — under C2's N parallel subtrees that is
	// the ordinary case. The cost is silence for a node reset as a DESCENDANT of the routed target rather than as the
	// target itself: no banner where one would have been useful, which is the side to be wrong on.
	const routed = events
		.filter(
			(event) =>
				event.eventType === devWorkflowAttemptEventTypes.retryRouted &&
				(event.sequence ?? 0) <= (latestReset.sequence ?? 0) &&
				devWorkflowRoutedDetail(event.detailJson).to === nodeRun.nodeKey,
		)
		.toSorted((left, right) => (left.sequence ?? 0) - (right.sequence ?? 0))
		.at(-1);
	const failedNodeKey = devWorkflowRoutedDetail(routed?.detailJson).from;
	if (!failedNodeKey || failedNodeKey === nodeRun.nodeKey) {
		return null;
	}

	const failedLabel = (run?.nodes ?? []).find((node) => node.nodeKey === failedNodeKey)?.label ?? failedNodeKey;
	return (
		<Alert color="blue" variant="light" data-testid="dev-workflow-node-cascade-rerun">
			{t(
				"pages.devWorkflows.node.cascadeRerun",
				"This node is running again because “{{node}}” failed and the run is re-doing the work that depended on it.",
				{ node: failedLabel },
			)}
		</Alert>
	);
}

/**
 * The policy that was baked into this node run's objective (D/Y2), read off the persisted resolution rather than
 * re-resolved: a rule set edited or deleted after materialization must not change what this run was told to do.
 *
 * That is exactly what the two hashes say. `contentSha256` is the body the run used; `currentContentSha256` is the
 * body the rule set has NOW — different means the stored rule has moved on since, `null` means it is gone. The short
 * hash is shown rather than the body itself: the body is up to 4096 characters of policy prose and belongs on the
 * rule-set page, while the question here is only "which rules, in which revision".
 */
function AppliedRuleSetsSection({ nodeRun }: { nodeRun: DevWorkflowNodeRunDetailResponse }) {
	const { t } = useTranslation();
	const ruleSets = nodeRun.appliedRuleSets ?? [];
	if (ruleSets.length === 0) {
		return null;
	}
	return (
		<SectionCard
			title={t("pages.devWorkflows.ruleSets.applied", "Applied rule sets")}
			gap="xs"
			data-testid="dev-workflow-node-rule-sets"
		>
			{ruleSets.map((ruleSet) => (
				<Group key={ruleSet.id} gap="xs" wrap="nowrap" data-testid={`dev-workflow-node-rule-set-${ruleSet.id}`}>
					<Text size="sm" style={{ flex: 1, minWidth: 0 }} lineClamp={1}>
						{ruleSet.name}
					</Text>
					<Code>{(ruleSet.contentSha256 ?? "").slice(0, 8)}</Code>
					{/* `null` is the server saying the rule set has been deleted since. An ABSENT field is not that — it is
					    a payload that never carried the comparison — so it earns no badge rather than a wrong one. */}
					{ruleSet.currentContentSha256 === null ? (
						<Badge size="xs" variant="light" color="gray" data-testid={`dev-workflow-node-rule-set-deleted-${ruleSet.id}`}>
							{t("pages.devWorkflows.ruleSets.deletedSince", "deleted")}
						</Badge>
					) : ruleSet.currentContentSha256 && ruleSet.currentContentSha256 !== ruleSet.contentSha256 ? (
						<Badge size="xs" variant="light" color="orange" data-testid={`dev-workflow-node-rule-set-edited-${ruleSet.id}`}>
							{t("pages.devWorkflows.ruleSets.editedSince", "edited since")}
						</Badge>
					) : null}
				</Group>
			))}
		</SectionCard>
	);
}

/** What this node was asked to do. `inputJson` is rendered as raw JSON text in v1 — nothing parses it yet. */
function ObjectiveSection({ nodeRun }: { nodeRun: DevWorkflowNodeRunDetailResponse }) {
	const { t } = useTranslation();
	if (!nodeRun.instructions && !nodeRun.inputJson && !nodeRun.outputJson) {
		return null;
	}
	return (
		<SectionCard title={t("pages.devWorkflows.node.objective", "Objective")} gap="xs" data-testid="dev-workflow-node-objective">
			{nodeRun.instructions ? (
				<Text size="sm" style={{ whiteSpace: "pre-wrap" }}>
					{nodeRun.instructions}
				</Text>
			) : null}
			{nodeRun.inputJson ? (
				<Code block={true} data-testid="dev-workflow-node-input">
					{nodeRun.inputJson}
				</Code>
			) : null}
			{nodeRun.outputJson ? (
				<Code block={true} data-testid="dev-workflow-node-output">
					{nodeRun.outputJson}
				</Code>
			) : null}
		</SectionCard>
	);
}
