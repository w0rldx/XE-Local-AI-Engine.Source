import { Badge, Group, Loader, Paper, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { ResponsiveTable } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
import { TablePaginationFooter } from "@/core/ui/components/TablePagination/TablePaginationFooter";
import { useTablePagination } from "@/core/ui/components/TablePagination/useTablePagination";
import type { AgentExecutionLog } from "@/features/agents/models/AgentExecutionLogModels";
import { useAgentExecutionLogs } from "@/features/agents/queries/useAgentExecutionLogs";

interface AgentExecutionLogPanelProps {
	agentDefinitionId: string;
	agentName: string;
	enabled: boolean;
}

// Render an optional token counter (null when the streaming path did not populate it).
function formatTokens(value: number | null): string {
	return value === null ? "—" : String(value);
}

// Per-agent execution-log diagnostics (adaptive-memory observability). METADATA ONLY — the backend records no
// message or exception text, so every column is safe to render verbatim (errorClass is the exception TYPE name).
// Owns its own read (independent loading/error path) and paginates client-side over a bounded recent window, the
// same posture as the scheduler run-history table. Capability-gated under agentManagement — renders nothing when off.
export function AgentExecutionLogPanel({ agentDefinitionId, agentName, enabled }: AgentExecutionLogPanelProps) {
	const { t } = useTranslation();

	const logsQuery = useAgentExecutionLogs(enabled ? agentDefinitionId : null);
	const logs: AgentExecutionLog[] = logsQuery.data ?? [];
	const pagination = useTablePagination(logs, { storageKey: "agent-execution-logs" });

	if (!enabled) {
		return null;
	}

	return (
		<Paper withBorder={true} radius="md" p="md" data-testid={`agent-execution-log-panel-${agentDefinitionId}`}>
			<Stack gap="sm">
				<Stack gap={2}>
					<Text fw={600}>{t("pages.agents.executionLog.title", "Run diagnostics")}</Text>
					<Text size="xs" c="dimmed">
						{t(
							"pages.agents.executionLog.subtitle",
							"Recent runs for {{name}} (metadata only — no message or error text is stored).",
							{ name: agentName },
						)}
					</Text>
				</Stack>

				{logsQuery.isLoading ? (
					<Group gap="sm" data-testid="agent-execution-log-loading">
						<Loader size="sm" />
						<Text c="dimmed" size="sm">
							{t("pages.agents.executionLog.loading", "Loading run diagnostics…")}
						</Text>
					</Group>
				) : null}

				{logsQuery.error ? (
					<InlineErrorAlert
						message={apiErrorMessage(
							logsQuery.error,
							t("pages.agents.executionLog.errors.load", "Could not load run diagnostics."),
						)}
						data-testid="agent-execution-log-error"
					/>
				) : null}

				{!logsQuery.isLoading && !logsQuery.error && logs.length === 0 ? (
					<EmptyState
						size="sm"
						message={t("pages.agents.executionLog.empty", "No runs recorded yet.")}
						data-testid="agent-execution-log-empty"
					/>
				) : null}

				{!logsQuery.isLoading && !logsQuery.error && logs.length > 0 ? (
					<>
						<ResponsiveTable
							rows={pagination.pageItems}
							rowKey={(log) => log.id}
							rowTestId={(log) => `agent-execution-log-row-${log.id}`}
							minWidth={720}
							data-testid="agent-execution-log-table"
							columns={[
								{
									key: "outcome",
									header: t("pages.agents.executionLog.columns.outcome", "Outcome"),
									primary: true,
									cell: (log) => (
										<Badge color={log.success ? "teal" : "red"} variant="light">
											{log.success
												? t("pages.agents.executionLog.outcome.success", "Success")
												: t("pages.agents.executionLog.outcome.failed", "Failed")}
										</Badge>
									),
								},
								{
									key: "when",
									header: t("pages.agents.executionLog.columns.when", "When"),
									cell: (log) => formatTimestamp(log.createdAtUtc > 0 ? log.createdAtUtc : null),
								},
								{
									key: "model",
									header: t("pages.agents.executionLog.columns.model", "Model"),
									cell: (log) => (
										<Text size="sm" lineClamp={1}>
											{log.modelName || "—"}
										</Text>
									),
								},
								{
									key: "latency",
									header: t("pages.agents.executionLog.columns.latency", "Latency"),
									cell: (log) => t("pages.agents.executionLog.latencyValue", "{{ms}} ms", { ms: log.latencyMs }),
								},
								{
									key: "promptTokens",
									header: t("pages.agents.executionLog.columns.promptTokens", "Prompt"),
									cell: (log) => formatTokens(log.promptTokens),
								},
								{
									key: "completionTokens",
									header: t("pages.agents.executionLog.columns.completionTokens", "Completion"),
									cell: (log) => formatTokens(log.completionTokens),
								},
								{
									key: "error",
									header: t("pages.agents.executionLog.columns.error", "Error class"),
									cell: (log) => (
										<Text size="sm" lineClamp={1}>
											{log.errorClass ?? "—"}
										</Text>
									),
								},
							]}
						/>
						<TablePaginationFooter
							page={pagination.page}
							pageCount={pagination.pageCount}
							pageSize={pagination.pageSize}
							totalItems={pagination.totalItems}
							firstItemIndex={pagination.firstItemIndex}
							lastItemIndex={pagination.lastItemIndex}
							pageSizeOptions={pagination.pageSizeOptions}
							onPageChange={pagination.setPage}
							onPageSizeChange={pagination.setPageSize}
							data-testid="agent-execution-log-pagination"
						/>
					</>
				) : null}
			</Stack>
		</Paper>
	);
}
