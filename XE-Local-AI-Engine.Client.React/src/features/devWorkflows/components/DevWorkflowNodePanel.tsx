import { Alert, Badge, Button, Code, Group, Loader, ScrollArea, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconExternalLink } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import {
	type DevWorkflowDecisionSubmission,
	DevWorkflowHumanGatePanel,
} from "@/features/devWorkflows/components/DevWorkflowHumanGatePanel";
import { DevWorkflowNodeStatusBadge } from "@/features/devWorkflows/components/DevWorkflowStatusBadge";
import {
	type DevWorkflowNodeRunDetailResponse,
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
	/** `node.interrupted` events counted for this node — the restart evidence `sessionResumes` does NOT carry. */
	readonly interruptedCount?: number;
	readonly onDecide: (submission: DevWorkflowDecisionSubmission) => void;
	readonly onShowArtifacts: () => void;
	/** Clears `?node=`, which is what brings the artifacts/events tabs back into this zone. */
	readonly onClose: () => void;
}

/**
 * The right-zone pane for the selected node-run. It dispatches on node type, and in Slice A0 every type-specific
 * section is a LINK-OUT rather than a re-hosted surface: the work-session view and the Dev Mode evidence chain already
 * exist at their own routes, and re-hosting either would fork the one place each is rendered.
 */
export function DevWorkflowNodePanel({
	nodeRun,
	isPending,
	loadError,
	isDeciding,
	decideError,
	artifactNameById,
	interruptedCount = 0,
	onDecide,
	onShowArtifacts,
	onClose,
}: DevWorkflowNodePanelProps) {
	const { t } = useTranslation();

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
							{t("pages.devWorkflows.nodes.attempt", "attempt {{attempt}} of {{maxAttempts}}", {
								attempt: nodeRun.attempt ?? 1,
								maxAttempts: nodeRun.maxAttempts ?? 1,
							})}
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

				<DevWorkflowHumanGatePanel
					nodeRun={nodeRun}
					isSubmitting={isDeciding}
					error={decideError}
					artifactNameById={artifactNameById}
					onDecide={onDecide}
					onShowArtifacts={onShowArtifacts}
				/>

				{nodeType === "Agent" ? <AgentSection nodeRun={nodeRun} /> : null}
				{nodeType === "Tool" ? <ToolSection nodeRun={nodeRun} onShowArtifacts={onShowArtifacts} /> : null}
				{nodeType === "DevTask" ? <DevTaskSection nodeRun={nodeRun} /> : null}

				<ObjectiveSection nodeRun={nodeRun} />
			</Stack>
		</ScrollArea>
	);
}

function AgentSection({ nodeRun }: { nodeRun: DevWorkflowNodeRunDetailResponse }) {
	const { t } = useTranslation();
	const navigate = useNavigate();
	return (
		<SectionCard title={t("pages.devWorkflows.node.agent", "Agent")} gap="xs" data-testid="dev-workflow-node-agent">
			<Text size="sm">
				{nodeRun.agentDisplayName ?? t("pages.devWorkflows.node.agentUnbound", "No agent is bound to this node.")}
			</Text>
			{nodeRun.modelLabel ? (
				<Text size="xs" c="dimmed">
					{nodeRun.modelLabel}
				</Text>
			) : null}
			{nodeRun.workSessionId ? (
				nodeRun.workSessionAvailable === false ? (
					// The node-run row outlives its work session on purpose (the reference is loose). Saying WHICH thing is
					// missing matters: the node's own events and artifacts are workflow-owned and still here.
					<Alert color="gray" variant="light" data-testid="dev-workflow-node-session-purged">
						{t(
							"pages.devWorkflows.node.sessionPurged",
							"The agent's transcript is no longer available. This node's events and artifacts are unaffected.",
						)}
					</Alert>
				) : nodeCapabilities.workSessions ? (
					// A link-out, not a re-hosted panel: the session view already exists at its own route with the plan,
					// findings and checkpoints this pane has no room for, and the session is a first-class
					// AgentWorkSessionKind.Workflow row that route renders unchanged.
					<Button
						size="xs"
						variant="subtle"
						leftSection={<IconExternalLink size={14} />}
						onClick={() => navigate({ to: "/work-sessions/$sessionId", params: { sessionId: nodeRun.workSessionId ?? "" } })}
						data-testid="dev-workflow-node-session-link"
					>
						{t("pages.devWorkflows.node.openSession", "Open the agent's work session")}
					</Button>
				) : null
			) : null}
		</SectionCard>
	);
}

function ToolSection({ nodeRun, onShowArtifacts }: { nodeRun: DevWorkflowNodeRunDetailResponse; onShowArtifacts: () => void }) {
	const { t } = useTranslation();
	return (
		<SectionCard title={t("pages.devWorkflows.node.tool", "Validation")} gap="xs" data-testid="dev-workflow-node-tool">
			{nodeRun.primaryArtifactId ? (
				<Button size="xs" variant="light" onClick={onShowArtifacts} data-testid="dev-workflow-node-tool-report">
					{t("pages.devWorkflows.node.openReport", "Open the validation report")}
				</Button>
			) : (
				<EmptyState size="sm" message={t("pages.devWorkflows.node.noReport", "No validation report yet.")} />
			)}
		</SectionCard>
	);
}

function DevTaskSection({ nodeRun }: { nodeRun: DevWorkflowNodeRunDetailResponse }) {
	const { t } = useTranslation();
	const navigate = useNavigate();
	if (!nodeRun.developmentTaskId) {
		return null;
	}
	return (
		<SectionCard title={t("pages.devWorkflows.node.devTask", "Development task")} gap="xs" data-testid="dev-workflow-node-devtask">
			{/* The task id is shown because the deep link cannot carry it yet: /development takes no search params, and
			    adding them is a change to a live, merged feature that belongs with the Tool/DevTask phase, not here. */}
			<Text size="xs" c="dimmed" data-testid="dev-workflow-node-devtask-id">
				{nodeRun.developmentTaskId}
			</Text>
			{nodeCapabilities.development ? (
				<Button
					size="xs"
					variant="subtle"
					leftSection={<IconExternalLink size={14} />}
					onClick={() => navigate({ to: "/development" })}
					data-testid="dev-workflow-node-development-link"
				>
					{t("pages.devWorkflows.node.openDevelopment", "Open Development Mode")}
				</Button>
			) : null}
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
