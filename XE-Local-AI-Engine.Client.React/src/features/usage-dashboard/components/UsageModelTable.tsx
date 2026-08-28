import { Badge, Card, Group, Stack, Table, Text, Title } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { TablePaginationFooter } from "@/core/ui/components/TablePagination/TablePaginationFooter";
import { useTablePagination } from "@/core/ui/components/TablePagination/useTablePagination";
import type { UsageModelRow } from "@/features/usage-dashboard/models/UsageDashboardModel";
import { formatCostUsd, formatCount, formatTokensCompact } from "@/features/usage-dashboard/models/UsageDashboardModel";
import { providerColor, providerLabel } from "@/features/usage-dashboard/models/UsageProviderPresentation";

// Persisted page-size preference (its own key so this table remembers a size independent of other tables).
const USAGE_MODEL_PAGE_SIZE_STORAGE_KEY = "usage-model-table";

// Per-model usage table: one row per model (aggregated across providers/days), sorted heaviest-first. The provider(s)
// the model ran under render as colour-coded badges. Client-side pagination over the already-loaded list.
export function UsageModelTable({
	rows,
	externalConnectionNames,
}: {
	readonly rows: readonly UsageModelRow[];
	// External connection id → display name, resolved by the page. Absent ids render as the bare "External".
	readonly externalConnectionNames?: ReadonlyMap<string, string>;
}) {
	const { t } = useTranslation();
	const pagination = useTablePagination(rows, { storageKey: USAGE_MODEL_PAGE_SIZE_STORAGE_KEY });

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="usage-model-table">
			<Stack gap="md">
				<Title order={3}>{t("pages.usage.models.title", "Usage by model")}</Title>
				<Table.ScrollContainer minWidth={720}>
					<Table striped={true} highlightOnHover={true} verticalSpacing="sm">
						<Table.Thead>
							<Table.Tr>
								<Table.Th>{t("pages.usage.models.columns.model", "Model")}</Table.Th>
								<Table.Th>{t("pages.usage.models.columns.providers", "Provider(s)")}</Table.Th>
								<Table.Th>{t("pages.usage.models.columns.runs", "Runs")}</Table.Th>
								<Table.Th>{t("pages.usage.models.columns.prompt", "Prompt")}</Table.Th>
								<Table.Th>{t("pages.usage.models.columns.completion", "Completion")}</Table.Th>
								<Table.Th>{t("pages.usage.models.columns.total", "Total")}</Table.Th>
								<Table.Th>{t("pages.usage.models.columns.estimatedCost", "Est. cost")}</Table.Th>
							</Table.Tr>
						</Table.Thead>
						<Table.Tbody>
							{pagination.pageItems.length === 0 ? (
								<Table.Tr>
									<Table.Td colSpan={7}>
										<EmptyState message={t("pages.usage.models.empty", "No model usage recorded for this range.")} />
									</Table.Td>
								</Table.Tr>
							) : (
								pagination.pageItems.map((row) => (
									<Table.Tr key={row.modelName} data-testid={`usage-model-row-${row.modelName}`}>
										<Table.Td>
											<Text fw={500} style={{ wordBreak: "break-all" }}>
												{row.modelName}
											</Text>
										</Table.Td>
										<Table.Td>
											<Group gap={4} wrap="wrap">
												{row.providers.map((provider) => (
													<Badge key={provider} color={providerColor(provider)} variant="light" size="sm">
														{providerLabel(provider, t, externalConnectionNames)}
													</Badge>
												))}
											</Group>
										</Table.Td>
										<Table.Td>{formatCount(row.runCount)}</Table.Td>
										<Table.Td>
											<Text aria-label={formatCount(row.promptTokens)}>{formatTokensCompact(row.promptTokens)}</Text>
										</Table.Td>
										<Table.Td>
											<Text aria-label={formatCount(row.completionTokens)}>{formatTokensCompact(row.completionTokens)}</Text>
										</Table.Td>
										<Table.Td>
											<Text fw={600} aria-label={formatCount(row.totalTokens)}>
												{formatTokensCompact(row.totalTokens)}
											</Text>
										</Table.Td>
										<Table.Td>
											<Text data-testid={`usage-model-cost-${row.modelName}`}>{formatCostUsd(row.estimatedCostUsd)}</Text>
										</Table.Td>
									</Table.Tr>
								))
							)}
						</Table.Tbody>
					</Table>
				</Table.ScrollContainer>

				{rows.length > 0 ? (
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
						data-testid="usage-model-pagination"
					/>
				) : null}
			</Stack>
		</Card>
	);
}
