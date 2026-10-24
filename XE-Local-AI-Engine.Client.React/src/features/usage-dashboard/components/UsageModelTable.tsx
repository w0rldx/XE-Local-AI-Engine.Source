import { Badge, Card, Group, Stack, Text, Title } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { ResponsiveTable } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
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
				<Title order={2} size="h3">
					{t("pages.usage.models.title", "Usage by model")}
				</Title>
				<ResponsiveTable
					rows={pagination.pageItems}
					rowKey={(row) => row.modelName}
					rowTestId={(row) => `usage-model-row-${row.modelName}`}
					minWidth={720}
					empty={<EmptyState message={t("pages.usage.models.empty", "No model usage recorded for this range.")} />}
					columns={[
						{
							key: "model",
							header: t("pages.usage.models.columns.model", "Model"),
							primary: true,
							cell: (row) => (
								<Text fw={500} style={{ overflowWrap: "anywhere" }}>
									{row.modelName}
								</Text>
							),
						},
						{
							key: "providers",
							header: t("pages.usage.models.columns.providers", "Provider(s)"),
							cell: (row) => (
								<Group gap={4} wrap="wrap">
									{row.providers.map((provider) => (
										// The max-content floor keeps a long label such as "Local (llama.cpp)" whole instead of ellipsized.
										<Badge
											key={provider}
											color={providerColor(provider)}
											variant="light"
											size="sm"
											style={{ minWidth: "max-content" }}
										>
											{providerLabel(provider, t, externalConnectionNames)}
										</Badge>
									))}
								</Group>
							),
						},
						{ key: "runs", header: t("pages.usage.models.columns.runs", "Runs"), cell: (row) => formatCount(row.runCount) },
						// A row whose runs reported no tokens says so once, under Prompt, rather than as three zeros.
						{
							key: "prompt",
							header: t("pages.usage.models.columns.prompt", "Prompt"),
							cell: (row) =>
								row.tokensReported === 0 ? (
									<Text c="dimmed" data-testid={`usage-model-not-reported-${row.modelName}`}>
										{t("pages.usage.models.notReported", "Not reported")}
									</Text>
								) : (
									<Text aria-label={formatCount(row.promptTokens)}>{formatTokensCompact(row.promptTokens)}</Text>
								),
						},
						{
							key: "completion",
							header: t("pages.usage.models.columns.completion", "Completion"),
							cell: (row) =>
								row.tokensReported === 0 ? null : (
									<Text aria-label={formatCount(row.completionTokens)}>{formatTokensCompact(row.completionTokens)}</Text>
								),
						},
						{
							key: "total",
							header: t("pages.usage.models.columns.total", "Total"),
							cell: (row) =>
								row.tokensReported === 0 ? null : (
									<>
										<Text fw={600} aria-label={formatCount(row.totalTokens)}>
											{formatTokensCompact(row.totalTokens)}
										</Text>
										{row.tokensReported < row.runCount ? (
											<Text size="xs" c="dimmed" data-testid={`usage-model-partly-reported-${row.modelName}`}>
												{t("pages.usage.models.runsNotReported", "{{count}} runs not reported", {
													count: row.runCount - row.tokensReported,
												})}
											</Text>
										) : null}
									</>
								),
						},
						{
							key: "estimatedCost",
							header: t("pages.usage.models.columns.estimatedCost", "Est. cost"),
							cell: (row) => <Text data-testid={`usage-model-cost-${row.modelName}`}>{formatCostUsd(row.estimatedCostUsd)}</Text>,
						},
					]}
				/>

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
