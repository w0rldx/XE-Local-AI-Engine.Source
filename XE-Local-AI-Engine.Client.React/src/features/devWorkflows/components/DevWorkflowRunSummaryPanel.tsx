import { Button, Divider, Group, NavLink, ScrollArea, Select, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { DevWorkflowRunStatusBadge } from "@/features/devWorkflows/components/DevWorkflowStatusBadge";
import {
	type DevWorkflowDefinitionSummaryResponse,
	type DevWorkflowNodeRunSummaryResponse,
	type DevWorkflowRunCostResponse,
	type DevWorkflowRunSummaryResponse,
	toDevWorkflowNodeStatus,
	toDevWorkflowRunStatus,
} from "@/features/devWorkflows/models/DevWorkflowModels";

export interface DevWorkflowRunSummaryPanelProps {
	readonly request?: string;
	readonly runs: readonly DevWorkflowRunSummaryResponse[];
	readonly selectedRunId?: string;
	readonly nodes: readonly DevWorkflowNodeRunSummaryResponse[];
	readonly pendingDecisionCount: number;
	/** The run's spend, summed server-side over its node runs' final attempts. Absent until a run is selected. */
	readonly cost?: DevWorkflowRunCostResponse;
	/** Empty while a run is live: X14 allows one live run per work item and the start is refused with a 409 anyway. */
	readonly startableDefinitions: readonly DevWorkflowDefinitionSummaryResponse[];
	/** Lifted, because the centre pane previews the picked template while the work item has nothing to show yet. */
	readonly selectedDefinitionId: string | null;
	readonly onSelectDefinition: (definitionId: string | null) => void;
	readonly isStarting: boolean;
	readonly startError?: string;
	readonly onSelectRun: (runId: string) => void;
	readonly onStartRun: (definitionId: string) => void;
}

export function DevWorkflowRunSummaryPanel({
	request,
	runs,
	selectedRunId,
	nodes,
	pendingDecisionCount,
	cost,
	startableDefinitions,
	selectedDefinitionId,
	onSelectDefinition,
	isStarting,
	startError,
	onSelectRun,
	onStartRun,
}: DevWorkflowRunSummaryPanelProps) {
	const { t } = useTranslation();
	const definitionId = selectedDefinitionId;

	const statuses = nodes.map((node) => toDevWorkflowNodeStatus(node.status));
	const running = statuses.filter((status) => status === "Running").length;
	const queued = statuses.filter((status) => status === "Queued").length;
	const done = statuses.filter((status) => status === "Succeeded" || status === "Skipped").length;

	return (
		<ScrollArea h="100%" data-testid="dev-workflow-run-summary-panel">
			<Stack gap="md" pr="xs">
				{request ? (
					<SectionCard title={t("pages.devWorkflows.detail.request", "Request")} gap="xs">
						<Text size="sm" style={{ whiteSpace: "pre-wrap" }} data-testid="dev-workflow-request">
							{request}
						</Text>
					</SectionCard>
				) : null}

				<SectionCard title={t("pages.devWorkflows.detail.runs", "Runs")} gap="xs">
					{runs.length === 0 ? (
						<EmptyState
							size="sm"
							message={t("pages.devWorkflows.detail.noRuns", "This work item has not run yet.")}
							data-testid="dev-workflow-no-runs"
						/>
					) : (
						<Stack gap={2}>
							{runs.map((run) => (
								<NavLink
									key={run.id}
									active={run.id === selectedRunId}
									onClick={() => onSelectRun(run.id ?? "")}
									data-testid={`dev-workflow-run-${run.id}`}
									label={
										<Group gap="xs" wrap="nowrap">
											<DevWorkflowRunStatusBadge
												status={toDevWorkflowRunStatus(run.status)}
												testId={`dev-workflow-run-status-${run.id}`}
											/>
										</Group>
									}
									description={
										run.startedAtUtc
											? new Date(run.startedAtUtc).toLocaleString()
											: // A Pending run genuinely has no start time; printing epoch zero would date it to 1970.
												t("pages.devWorkflows.detail.notStarted", "not started yet")
									}
								/>
							))}
						</Stack>
					)}

					{startableDefinitions.length > 0 ? (
						<>
							<Divider />
							<Select
								size="xs"
								placeholder={t("pages.devWorkflows.create.definitionPlaceholder", "Pick a template")}
								data={startableDefinitions.map((definition) => ({
									value: definition.id ?? "",
									label: definition.name ?? "",
								}))}
								value={definitionId}
								onChange={onSelectDefinition}
								data-testid="dev-workflow-start-definition"
							/>
							<Button
								size="xs"
								disabled={!definitionId}
								loading={isStarting}
								onClick={() => {
									if (definitionId) {
										onStartRun(definitionId);
									}
								}}
								data-testid="dev-workflow-start-run"
							>
								{t("pages.devWorkflows.detail.startRun", "Start a run")}
							</Button>
							{startError ? (
								<Text size="xs" c="red" data-testid="dev-workflow-start-error">
									{startError}
								</Text>
							) : null}
						</>
					) : null}
				</SectionCard>

				{selectedRunId ? (
					<SectionCard title={t("pages.devWorkflows.detail.progress", "Progress")} gap="xs">
						{/* Queued is its own figure, never folded into "in progress" (O9): the node has one agent slot, so a
						    combined number would claim parallel agents this machine cannot run. */}
						<Text size="sm" data-testid="dev-workflow-progress-counts">
							{t("pages.devWorkflows.detail.counts", "{{running}} running · {{queued}} queued · {{done}}/{{total}} done", {
								running,
								queued,
								done,
								total: nodes.length,
							})}
						</Text>
						{pendingDecisionCount > 0 ? (
							<Text size="sm" c="orange" fw={500} data-testid="dev-workflow-progress-decisions">
								{t("pages.devWorkflows.detail.waitingOnYou", "{{count}} waiting on you", { count: pendingDecisionCount })}
							</Text>
						) : null}
						{/* The run's spend so far. A LOWER bound: it sums each node's LAST attempt, so a run that retried spent
						    more than this line says — the retry events carry the rest. */}
						{cost && (cost.inputTokens != null || cost.outputTokens != null || cost.toolCalls != null) ? (
							<Text size="xs" c="dimmed" data-testid="dev-workflow-progress-cost">
								{t("pages.devWorkflows.detail.cost", "{{input}} / {{output}} tok · {{tools}} tool calls, final attempts only", {
									input: cost.inputTokens?.toLocaleString() ?? "–",
									output: cost.outputTokens?.toLocaleString() ?? "–",
									tools: cost.toolCalls?.toLocaleString() ?? "–",
								})}
							</Text>
						) : null}
					</SectionCard>
				) : null}
			</Stack>
		</ScrollArea>
	);
}
