import { Alert, Badge, Group, Loader, Paper, Stack, Table, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { TablePaginationFooter } from "@/core/ui/components/TablePagination/TablePaginationFooter";
import { useTablePagination } from "@/core/ui/components/TablePagination/useTablePagination";
import type { AgentExecutionLog } from "@/features/agents/models/AgentExecutionLogModels";
import { useAgentExecutionLogs } from "@/features/agents/queries/useAgentExecutionLogs";

interface AgentExecutionLogPanelProps {
	agentDefinitionId: string;
	agentName: string;
	// FE-static capability gate (folded under agentManagement). When false the panel renders nothing.
	enabled: boolean;
}

// Render an epoch-millisecond timestamp as a locale string, or an em-dash for a missing/invalid value.
function formatTimestamp(epochMs: number): string {
	if (epochMs <= 0) {
		return "—";
	}
	const date = new Date(epochMs);
	return Number.isNaN(date.getTime()) ? "—" : date.toLocaleString();
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
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="agent-execution-log-error">
						{apiErrorMessage(logsQuery.error, t("pages.agents.executionLog.errors.load", "Could not load run diagnostics."))}
					</Alert>
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
						<Table.ScrollContainer minWidth={720}>
							<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="agent-execution-log-table">
								<Table.Thead>
									<Table.Tr>
										<Table.Th>{t("pages.agents.executionLog.columns.outcome", "Outcome")}</Table.Th>
										<Table.Th>{t("pages.agents.executionLog.columns.when", "When")}</Table.Th>
										<Table.Th>{t("pages.agents.executionLog.columns.model", "Model")}</Table.Th>
										<Table.Th>{t("pages.agents.executionLog.columns.latency", "Latency")}</Table.Th>
										<Table.Th>{t("pages.agents.executionLog.columns.promptTokens", "Prompt")}</Table.Th>
										<Table.Th>{t("pages.agents.executionLog.columns.completionTokens", "Completion")}</Table.Th>
										<Table.Th>{t("pages.agents.executionLog.columns.error", "Error class")}</Table.Th>
									</Table.Tr>
								</Table.Thead>
								<Table.Tbody>
									{pagination.pageItems.map((log) => (
										<Table.Tr key={log.id} data-testid={`agent-execution-log-row-${log.id}`}>
											<Table.Td>
												<Badge color={log.success ? "teal" : "red"} variant="light">
													{log.success
														? t("pages.agents.executionLog.outcome.success", "Success")
														: t("pages.agents.executionLog.outcome.failed", "Failed")}
												</Badge>
											</Table.Td>
											<Table.Td>{formatTimestamp(log.createdAtUtc)}</Table.Td>
											<Table.Td>
												<Text size="sm" lineClamp={1}>
													{log.modelName || "—"}
												</Text>
											</Table.Td>
											<Table.Td>
												{t("pages.agents.executionLog.latencyValue", "{{ms}} ms", { ms: log.latencyMs })}
											</Table.Td>
											<Table.Td>{formatTokens(log.promptTokens)}</Table.Td>
											<Table.Td>{formatTokens(log.completionTokens)}</Table.Td>
											<Table.Td>
												<Text size="sm" lineClamp={1}>
													{log.errorClass ?? "—"}
												</Text>
											</Table.Td>
										</Table.Tr>
									))}
								</Table.Tbody>
							</Table>
						</Table.ScrollContainer>
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
