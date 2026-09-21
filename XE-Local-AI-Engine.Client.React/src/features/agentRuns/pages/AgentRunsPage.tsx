import { Badge, Button, Code, Group, Loader, Stack, Table, Text } from "@mantine/core";
import { IconHistory } from "@tabler/icons-react";
import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { TablePaginationFooter } from "@/core/ui/components/TablePagination/TablePaginationFooter";
import { useServerTablePagination } from "@/core/ui/components/TablePagination/useTablePagination";
import {
	type AgentRunView,
	agentRunOutcomeColor,
	agentRunPageSize,
	agentRunPageSizeOptions,
	formatAgentRunSize,
} from "@/features/agentRuns/models/AgentRunModels";
import { useAgentRuns } from "@/features/agentRuns/queries/useAgentRuns";
import { AgentHomePatchApplyDialog } from "@/features/chat/components/AgentHomePatchApplyDialog";

/**
 * The node's AgentHome run history: what the agent did on this computer, and which of those runs still has a patch
 * an operator can review and land.
 *
 * Read-only by design. The row action is the EXISTING review-and-apply dialog, opened for that run id — the same
 * one the chat tool card opens, so there is one apply flow and one hash binding, not two. Deleting a run, deep
 * links to the conversation and reading a run's logs are later slices.
 */
export function AgentRunsPage() {
	const { t } = useTranslation();
	const [page, setPage] = useState(1);
	const [pageSize, setPageSize] = useState(agentRunPageSize);
	const [reviewRunId, setReviewRunId] = useState<string | null>(null);

	// A new page size renumbers the pages, so the old page number means nothing against it.
	const handlePageSizeChange = useCallback((next: number) => {
		setPageSize(next);
		setPage(1);
	}, []);

	const runsQuery = useAgentRuns({ limit: pageSize, offset: (page - 1) * pageSize });
	const runs = runsQuery.data?.items ?? [];

	const pagination = useServerTablePagination({
		page,
		pageSize,
		totalItems: runsQuery.data?.totalCount ?? 0,
		pageSizeOptions: agentRunPageSizeOptions,
		onPageChange: setPage,
		onPageSizeChange: handlePageSizeChange,
	});

	return (
		<PageShell>
			<PageHeader
				title={t("pages.agentRuns.title", "Agent runs")}
				icon={<IconHistory size={24} />}
				subtitle={t("pages.agentRuns.subtitle", "Every AgentHome run this node has kept, newest first.")}
				data-testid="agent-runs-header"
			/>
			<SectionCard>
				{runsQuery.isError ? (
					<InlineErrorAlert
						message={apiErrorMessage(runsQuery.error, t("pages.agentRuns.loadError", "The run history could not be loaded."))}
						data-testid="agent-runs-error"
					/>
				) : null}
				{runsQuery.isPending ? (
					<Group gap="sm" data-testid="agent-runs-loading">
						<Loader size="sm" />
						<Text size="sm" c="dimmed">
							{t("pages.agentRuns.loading", "Loading the run history…")}
						</Text>
					</Group>
				) : null}
				{!runsQuery.isPending && !runsQuery.isError && runs.length === 0 ? (
					<EmptyState
						message={t("pages.agentRuns.empty", "This node has not run an agent task yet.")}
						data-testid="agent-runs-empty"
					/>
				) : null}
				{runs.length > 0 ? (
					<Stack gap="md">
						<AgentRunTable runs={runs} onReview={setReviewRunId} />
						<TablePaginationFooter {...pagination} data-testid="agent-runs-pagination" />
					</Stack>
				) : null}
			</SectionCard>
			{reviewRunId === null ? null : <AgentHomePatchApplyDialog runId={reviewRunId} onClose={() => setReviewRunId(null)} />}
		</PageShell>
	);
}

function AgentRunTable({
	runs,
	onReview,
}: {
	readonly runs: readonly AgentRunView[];
	readonly onReview: (runId: string) => void;
}) {
	const { t } = useTranslation();

	return (
		<Table.ScrollContainer minWidth={900}>
			<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="agent-runs-table">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.agentRuns.columns.run", "Run")}</Table.Th>
						<Table.Th>{t("pages.agentRuns.columns.started", "Started")}</Table.Th>
						<Table.Th>{t("pages.agentRuns.columns.outcome", "Outcome")}</Table.Th>
						<Table.Th>{t("pages.agentRuns.columns.changes", "Changes")}</Table.Th>
						<Table.Th>{t("pages.agentRuns.columns.size", "Size")}</Table.Th>
						<Table.Th />
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{runs.map((run) => (
						<Table.Tr key={run.runId} data-testid={`agent-run-row-${run.runId}`}>
							<Table.Td>
								<Code>{run.runId}</Code>
							</Table.Td>
							<Table.Td>{formatTimestamp(run.startedAtUtc)}</Table.Td>
							<Table.Td>
								<Badge color={agentRunOutcomeColor(run.outcome)} variant="light">
									{t(`pages.agentRuns.outcome.${run.outcome}`, run.outcome)}
								</Badge>
							</Table.Td>
							<Table.Td>
								<ChangesCell run={run} />
							</Table.Td>
							<Table.Td>{formatAgentRunSize(run.sizeBytes)}</Table.Td>
							<Table.Td>
								{run.patchExported ? (
									<Button
										size="xs"
										variant="light"
										onClick={() => onReview(run.runId)}
										data-testid={`agent-run-review-${run.runId}`}
									>
										{t("pages.agentRuns.review", "Review changes")}
									</Button>
								) : null}
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}

/** What the run's patch amounts to: how many files, and whether it has already been landed or refused. */
function ChangesCell({ run }: { readonly run: AgentRunView }) {
	const { t } = useTranslation();

	if (!run.patchExported) {
		return (
			<Text size="sm" c="dimmed">
				{t("pages.agentRuns.noPatch", "No changes exported")}
			</Text>
		);
	}

	return (
		<Group gap="xs" wrap="nowrap">
			<Text size="sm">
				{run.changedFileCount === null
					? t("pages.agentRuns.filesUnknown", "Patch exported")
					: t("pages.agentRuns.files", "{{value}} file(s)", { value: run.changedFileCount })}
			</Text>
			{run.applyState === "none" ? null : (
				<Badge size="sm" variant="outline" color={run.applyState === "applied" ? "teal" : "orange"}>
					{run.applyState === "applied"
						? t("pages.agentRuns.applyState.applied", "Applied")
						: t("pages.agentRuns.applyState.rejected", "Apply refused")}
				</Badge>
			)}
		</Group>
	);
}
