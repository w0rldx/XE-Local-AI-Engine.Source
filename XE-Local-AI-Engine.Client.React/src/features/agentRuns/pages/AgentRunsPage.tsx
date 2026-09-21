import { Badge, Button, Code, Group, Loader, Stack, Table, Text } from "@mantine/core";
import { IconFileText, IconHistory, IconMessage, IconTrash } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { AgentHomePatchApplyDialog } from "@/core/ui/components/AgentHomePatchApplyDialog/AgentHomePatchApplyDialog";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { TablePaginationFooter } from "@/core/ui/components/TablePagination/TablePaginationFooter";
import { useServerTablePagination } from "@/core/ui/components/TablePagination/useTablePagination";
import { usePendingChatConversationStore } from "@/core/ui/stores/PendingChatConversationStore";
import { AgentRunDeleteDialog } from "@/features/agentRuns/components/AgentRunDeleteDialog";
import { AgentRunViewerDialog } from "@/features/agentRuns/components/AgentRunViewerDialog";
import {
	type AgentRunView,
	agentRunOutcomeColor,
	agentRunPageSize,
	agentRunPageSizeOptions,
	formatAgentRunSize,
} from "@/features/agentRuns/models/AgentRunModels";
import { useAgentRuns } from "@/features/agentRuns/queries/useAgentRuns";

/**
 * The node's AgentHome run history: what the agent did on this computer, and which of those runs still has a patch
 * an operator can review and land.
 *
 * The row actions are the EXISTING review-and-apply dialog, opened for that run id — the same one the chat tool card
 * opens, so there is one apply flow and one hash binding, not two — a deep link to the conversation the run happened
 * in, a viewer over what the run wrote, and a confirmed delete.
 *
 * One dialog at a time by construction: each kind has its own `runId | null` slot and opening one closes the others,
 * so no dialog ever has to be raised over another.
 */
export function AgentRunsPage() {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const [page, setPage] = useState(1);
	const [pageSize, setPageSize] = useState(agentRunPageSize);
	const [reviewRunId, setReviewRunId] = useState<string | null>(null);
	const [viewRunId, setViewRunId] = useState<string | null>(null);
	const [deleteRunId, setDeleteRunId] = useState<string | null>(null);
	// The conversation travels through the core hand-off store rather than a search param: `/chat` has no search
	// schema and reads its thread from the chat preferences, which this store is what lets another feature reach
	// without importing chat's own store.
	const setPendingConversationId = usePendingChatConversationStore((state) => state.actions.setPendingConversationId);
	const handleOpenConversation = useCallback(
		(conversationId: string) => {
			setPendingConversationId(conversationId);
			navigate({ to: nodeRoutePaths.chat });
		},
		[navigate, setPendingConversationId],
	);

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
						<AgentRunTable
							runs={runs}
							onReview={setReviewRunId}
							onOpenConversation={handleOpenConversation}
							onView={setViewRunId}
							onDelete={setDeleteRunId}
						/>
						<TablePaginationFooter {...pagination} data-testid="agent-runs-pagination" />
					</Stack>
				) : null}
			</SectionCard>
			{reviewRunId === null ? null : <AgentHomePatchApplyDialog runId={reviewRunId} onClose={() => setReviewRunId(null)} />}
			{viewRunId === null ? null : <AgentRunViewerDialog runId={viewRunId} onClose={() => setViewRunId(null)} />}
			{deleteRunId === null ? null : <AgentRunDeleteDialog runId={deleteRunId} onClose={() => setDeleteRunId(null)} />}
		</PageShell>
	);
}

function AgentRunTable({
	runs,
	onReview,
	onOpenConversation,
	onView,
	onDelete,
}: {
	readonly runs: readonly AgentRunView[];
	readonly onReview: (runId: string) => void;
	readonly onOpenConversation: (conversationId: string) => void;
	readonly onView: (runId: string) => void;
	readonly onDelete: (runId: string) => void;
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
								<RunActionsCell
									run={run}
									onReview={onReview}
									onOpenConversation={onOpenConversation}
									onView={onView}
									onDelete={onDelete}
								/>
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}

/**
 * What an operator can still do with this run: review its patch, open the conversation it ran in, read what it
 * wrote, and delete it.
 *
 * An action is absent rather than disabled when the run has nothing to offer it — a run older than the `started`
 * event that carries the conversation id, or one whose log could not be parsed, reports `conversationId: null`, and a
 * disabled button explaining that to every such row would cost a tooltip a keyboard cannot reach for no gain. View
 * and delete are always offered: every run has a directory, even an empty one, and the viewer says so honestly.
 */
function RunActionsCell({
	run,
	onReview,
	onOpenConversation,
	onView,
	onDelete,
}: {
	readonly run: AgentRunView;
	readonly onReview: (runId: string) => void;
	readonly onOpenConversation: (conversationId: string) => void;
	readonly onView: (runId: string) => void;
	readonly onDelete: (runId: string) => void;
}) {
	const { t } = useTranslation();
	const conversationId = run.conversationId;

	return (
		<Group gap="xs" wrap="nowrap">
			{run.patchExported ? (
				<Button size="xs" variant="light" onClick={() => onReview(run.runId)} data-testid={`agent-run-review-${run.runId}`}>
					{t("pages.agentRuns.review", "Review changes")}
				</Button>
			) : null}
			<Button
				size="xs"
				variant="subtle"
				leftSection={<IconFileText size={14} />}
				onClick={() => onView(run.runId)}
				data-testid={`agent-run-view-${run.runId}`}
			>
				{t("pages.agentRuns.viewer.open", "Log and patch")}
			</Button>
			{conversationId === null ? null : (
				<Button
					size="xs"
					variant="subtle"
					leftSection={<IconMessage size={14} />}
					onClick={() => onOpenConversation(conversationId)}
					data-testid={`agent-run-conversation-${run.runId}`}
				>
					{t("pages.agentRuns.openConversation", "Open conversation")}
				</Button>
			)}
			<Button
				size="xs"
				variant="subtle"
				color="red"
				leftSection={<IconTrash size={14} />}
				onClick={() => onDelete(run.runId)}
				data-testid={`agent-run-delete-${run.runId}`}
			>
				{t("pages.agentRuns.delete.action", "Delete")}
			</Button>
		</Group>
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
