import { Alert, Badge, Card, Group, Skeleton, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { GraphWorkflowRunStatusBadge } from "@/features/graphWorkflows/components/GraphWorkflowStatusBadge";
import {
	type GraphWorkflowRunSummaryResponse,
	narrowGraphWorkflowFailureClass,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

export interface GraphWorkflowRunListProps {
	readonly runs: readonly GraphWorkflowRunSummaryResponse[];
	readonly isLoading?: boolean;
	readonly error?: unknown;
	readonly selectedRunId?: string;
	readonly onSelectRun: (runId: string) => void;
}

/** The runs of one definition, newest first — the run an operator came to look at is the one they just started. */
export function GraphWorkflowRunList({ runs, isLoading, error, selectedRunId, onSelectRun }: GraphWorkflowRunListProps) {
	const { t } = useTranslation();

	if (isLoading === true) {
		return (
			<Stack gap="xs" data-testid="graph-workflow-run-list-loading">
				<Skeleton height={64} radius="md" />
				<Skeleton height={64} radius="md" />
			</Stack>
		);
	}

	if (error !== undefined && error !== null) {
		return (
			<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="graph-workflow-run-list-error">
				{apiErrorMessage(error, t("pages.graphWorkflows.runs.loadFailed", "Could not load the runs of this workflow."))}
			</Alert>
		);
	}

	if (runs.length === 0) {
		return (
			<EmptyState
				message={t("pages.graphWorkflows.runs.empty", "This workflow has not been run yet.")}
				data-testid="graph-workflow-run-list-empty"
			/>
		);
	}

	// `createdAtUtc` is set when the run row is written, before it starts, so it orders a queued run correctly too.
	const ordered = runs.toSorted((left, right) => (right.createdAtUtc ?? 0) - (left.createdAtUtc ?? 0));

	return (
		<Stack gap="xs" data-testid="graph-workflow-run-list">
			{ordered.map((run) => {
				const runId = run.id ?? "";
				const failureClass = narrowGraphWorkflowFailureClass(run.failureClass);
				return (
					<Card
						key={runId}
						withBorder={true}
						padding="xs"
						onClick={() => onSelectRun(runId)}
						bg={runId === selectedRunId ? "var(--mantine-color-default-hover)" : undefined}
						style={{ cursor: "pointer" }}
						data-testid={`graph-workflow-run-card-${runId}`}
					>
						<Stack gap={4}>
							<Group gap="xs" wrap="wrap">
								<GraphWorkflowRunStatusBadge status={run.status} data-testid={`graph-workflow-run-status-${runId}`} />
								<Badge size="xs" variant="light" color="gray" data-testid={`graph-workflow-run-version-${runId}`}>
									{t("pages.graphWorkflows.runs.version", "Version {{version}}", { version: run.definitionVersion ?? 0 })}
								</Badge>
								{failureClass !== "None" ? (
									<Badge size="xs" variant="light" color="red" data-testid={`graph-workflow-run-failure-${runId}`}>
										{t(`pages.graphWorkflows.failureClass.${failureClass}`, failureClass)}
									</Badge>
								) : null}
							</Group>
							<Text size="xs" c="dimmed" data-testid={`graph-workflow-run-times-${runId}`}>
								{run.completedAtUtc != null
									? t("pages.graphWorkflows.runs.completedAt", "Finished {{time}}", {
											time: new Date(run.completedAtUtc).toLocaleString(),
										})
									: run.startedAtUtc != null
										? t("pages.graphWorkflows.runs.startedAt", "Started {{time}}", {
												time: new Date(run.startedAtUtc).toLocaleString(),
											})
										: t("pages.graphWorkflows.runs.createdAt", "Created {{time}}", {
												time: new Date(run.createdAtUtc ?? 0).toLocaleString(),
											})}
							</Text>
						</Stack>
					</Card>
				);
			})}
		</Stack>
	);
}
