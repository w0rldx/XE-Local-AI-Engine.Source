import { Badge, Button, Stack, Table, Text } from "@mantine/core";
import { IconCheck, IconCloudDownload, IconDownload } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { TablePaginationFooter } from "@/core/ui/components/TablePagination/TablePaginationFooter";
import { useTablePagination } from "@/core/ui/components/TablePagination/useTablePagination";
import {
	fitLevelColor,
	formatContextTokens,
	formatMemoryMb,
	formatModelFitMetric,
} from "@/features/model-fit/components/ModelFitFormatters";
import type { ModelFitRecommendation } from "@/features/model-fit/models/ModelFitModels";

interface RecommendationTableProps {
	recommendations: readonly ModelFitRecommendation[];
	// When provided, an action cell renders a Pull button for pullable rows. The parent drives the actual pull (and
	// progress) through the shared useModelPull hook; the table only signals intent and reflects the in-flight name.
	onPull?: (recommendation: ModelFitRecommendation) => void;
	// Model name currently being pulled (the parent's in-flight pull), used to disable that row's button. The
	// comparison is against pullModelName — the Ollama tag the Pull button actually pulls.
	pullingModelName?: string | null;
}

// Pure presentation: renders the ranked recommendation rows for a cached snapshot, client-side paginated (the run now
// fetches the whole use-case catalog, ~166 rows). The parent owns the data and the empty/diagnostics state — this
// table is only rendered when there is at least one recommendation. Each row shows rank, model name, score, fit-level
// + run-mode badges, estimated TPS / context / quantization, release date, a status badge (Installed / Pullable /
// Catalog only), and (when onPull is wired) a Pull action. A Pull button is shown ONLY for a row that is not installed
// AND carries a non-null pullModelName (most llmfit ids are not Ollama-pullable — those are flagged "Catalog only").
export function RecommendationTable({ recommendations, onPull, pullingModelName }: RecommendationTableProps) {
	const { t } = useTranslation();
	const showActions = onPull !== undefined;

	const pagination = useTablePagination(recommendations, { storageKey: "model-fit-recommendations" });

	return (
		<Stack gap="sm">
			<Table.ScrollContainer minWidth={980}>
				<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="model-fit-recommendations-table">
					<Table.Thead>
						<Table.Tr>
							<Table.Th>{t("pages.modelFit.recommendations.columns.rank", "Rank")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.model", "Model")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.score", "Score")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.fit", "Fit")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.runMode", "Run mode")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.tps", "Est. TPS")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.context", "Context")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.quantization", "Quant")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.memory", "Memory")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.released", "Released")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.installed", "Status")}</Table.Th>
							{showActions ? <Table.Th>{t("pages.modelFit.recommendations.columns.action", "Action")}</Table.Th> : null}
						</Table.Tr>
					</Table.Thead>
					<Table.Tbody>
						{pagination.pageItems.map((recommendation) => (
							<Table.Tr
								key={`${recommendation.rank}-${recommendation.modelName}`}
								data-testid={`model-fit-recommendation-row-${recommendation.rank}`}
							>
								<Table.Td>{recommendation.rank}</Table.Td>
								<Table.Td>
									<Text size="sm" fw={500}>
										{recommendation.modelName}
									</Text>
									{recommendation.providerModelName ? (
										<Text size="xs" c="dimmed">
											{recommendation.providerModelName}
										</Text>
									) : null}
								</Table.Td>
								<Table.Td>{formatModelFitMetric(recommendation.score, "", 1)}</Table.Td>
								<Table.Td>
									{recommendation.fitLevel ? (
										<Badge color={fitLevelColor(recommendation.fitLevel)} variant="light">
											{recommendation.fitLevel}
										</Badge>
									) : (
										"—"
									)}
								</Table.Td>
								<Table.Td>{recommendation.runMode ? <Badge variant="outline">{recommendation.runMode}</Badge> : "—"}</Table.Td>
								<Table.Td>{formatModelFitMetric(recommendation.estimatedTokensPerSecond, "", 1)}</Table.Td>
								<Table.Td>{formatContextTokens(recommendation.contextTokens)}</Table.Td>
								<Table.Td>{recommendation.quantization ?? "—"}</Table.Td>
								<Table.Td>{formatMemoryMb(recommendation.requiredRamMb)}</Table.Td>
								<Table.Td>{recommendation.releaseDate ?? "—"}</Table.Td>
								<Table.Td>
									{recommendation.isInstalled ? (
										<Badge color="green" variant="light" leftSection={<IconCheck size={12} />}>
											{t("pages.modelFit.recommendations.installed", "Installed")}
										</Badge>
									) : recommendation.pullModelName ? (
										<Badge color="blue" variant="light" leftSection={<IconDownload size={12} />}>
											{t("pages.modelFit.recommendations.pullable", "Pullable")}
										</Badge>
									) : (
										// No Ollama mapping and not installed: a catalog-only name the operator can't one-click pull.
										<Badge color="gray" variant="light">
											{t("pages.modelFit.recommendations.catalogOnly", "Catalog only")}
										</Badge>
									)}
								</Table.Td>
								{showActions ? (
									<Table.Td>
										{/* Pull is offered only for a not-installed row that carries a real Ollama tag. Installed rows and
										    catalog-only rows (pullModelName === null) show nothing actionable. */}
										{!recommendation.isInstalled && recommendation.pullModelName ? (
											<Button
												size="xs"
												variant="light"
												leftSection={<IconCloudDownload size={14} />}
												loading={pullingModelName === recommendation.pullModelName}
												disabled={pullingModelName === recommendation.pullModelName}
												onClick={() => onPull?.(recommendation)}
												data-testid={`model-fit-pull-button-${recommendation.rank}`}
											>
												{pullingModelName === recommendation.pullModelName
													? t("pages.modelFit.recommendations.pulling", "Pulling…")
													: t("pages.modelFit.recommendations.pull", "Pull")}
											</Button>
										) : (
											<Text size="xs" c="dimmed">
												—
											</Text>
										)}
									</Table.Td>
								) : null}
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
				data-testid="model-fit-recommendations-pagination"
			/>
		</Stack>
	);
}
