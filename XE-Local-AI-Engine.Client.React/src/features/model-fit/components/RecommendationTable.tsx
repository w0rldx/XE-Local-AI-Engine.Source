import { Badge, Button, Group, Stack, Table, Text } from "@mantine/core";
import { IconCheck, IconCloudDownload, IconCpu, IconDownload } from "@tabler/icons-react";
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
	// When provided, an action cell renders a Download (GGUF) button per row. The parent drives the actual download
	// (and progress / cancel) through the Lane-B store seam; the table only signals intent and reflects the in-flight name.
	onDownload?: (recommendation: ModelFitRecommendation) => void;
	// Model name currently being downloaded (the parent's in-flight download), used to disable that row's button.
	downloadingModelName?: string | null;
}

// A row runs in CPU mode when the advisor's run mode is CPU (VRAM unknown / no GPU accel). Surfaced as a distinct
// badge so the operator sees the fit was estimated against the RAM budget, not VRAM.
function isCpuMode(runMode: string | null): boolean {
	return runMode?.toLowerCase() === "cpu";
}

// The Fit cell renders the advisor's run-mode fit level ("GPU"/"CPU") as a colored badge plus a CPU-mode badge for a
// CPU run. The "CPU" fit level duplicates the dedicated CPU-mode badge, so a CPU row shows only the CPU-mode badge;
// a non-CPU fit level renders its own colored badge. When neither applies, the cell shows a dash.
function FitCell({ recommendation }: { recommendation: ModelFitRecommendation }) {
	const { t } = useTranslation();
	const cpuMode = isCpuMode(recommendation.runMode);
	const showFitBadge = recommendation.fitLevel !== null && recommendation.fitLevel !== "CPU";

	return (
		<Group gap={4} wrap="nowrap">
			{showFitBadge ? (
				<Badge color={fitLevelColor(recommendation.fitLevel)} variant="light">
					{recommendation.fitLevel}
				</Badge>
			) : null}
			{cpuMode ? (
				<Badge
					color="orange"
					variant="light"
					leftSection={<IconCpu size={12} />}
					data-testid={`model-fit-cpu-mode-badge-${recommendation.rank}`}
				>
					{t("pages.modelFit.recommendations.cpuMode", "CPU mode")}
				</Badge>
			) : null}
			{!showFitBadge && !cpuMode ? "—" : null}
		</Group>
	);
}

// Pure presentation: renders the ranked recommendation rows for a cached snapshot, client-side paginated. Each row
// shows rank, model name, score, fit-level badge (+ a CPU-mode badge when the advisor ran the fit against RAM rather
// than VRAM), estimated TPS, context, the chosen GGUF quant, the memory-fit estimate (required RAM / VRAM), release
// date, an install status badge, and (when onDownload is wired) a per-row Download action that triggers the GGUF
// download for that model. The parent owns the data and the empty/diagnostics state.
export function RecommendationTable({ recommendations, onDownload, downloadingModelName }: RecommendationTableProps) {
	const { t } = useTranslation();
	const showActions = onDownload !== undefined;

	const pagination = useTablePagination(recommendations, { storageKey: "model-fit-recommendations" });

	return (
		<Stack gap="sm">
			<Table.ScrollContainer minWidth={1040}>
				<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="model-fit-recommendations-table">
					<Table.Thead>
						<Table.Tr>
							<Table.Th>{t("pages.modelFit.recommendations.columns.rank", "Rank")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.model", "Model")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.score", "Score")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.fit", "Fit")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.tps", "Est. TPS")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.context", "Context")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.quantization", "Quant")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.ram", "Req. RAM")}</Table.Th>
							<Table.Th>{t("pages.modelFit.recommendations.columns.vram", "Req. VRAM")}</Table.Th>
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
									<FitCell recommendation={recommendation} />
								</Table.Td>
								<Table.Td>{formatModelFitMetric(recommendation.estimatedTokensPerSecond, "", 1)}</Table.Td>
								<Table.Td>{formatContextTokens(recommendation.contextTokens)}</Table.Td>
								<Table.Td>{recommendation.quantization ?? "—"}</Table.Td>
								<Table.Td>{formatMemoryMb(recommendation.requiredRamMb)}</Table.Td>
								<Table.Td>{formatMemoryMb(recommendation.requiredVramMb)}</Table.Td>
								<Table.Td>{recommendation.releaseDate ?? "—"}</Table.Td>
								<Table.Td>
									{recommendation.isInstalled ? (
										<Badge color="green" variant="light" leftSection={<IconCheck size={12} />}>
											{t("pages.modelFit.recommendations.installed", "Installed")}
										</Badge>
									) : recommendation.pullModelName ? (
										<Badge color="blue" variant="light" leftSection={<IconDownload size={12} />}>
											{t("pages.modelFit.recommendations.available", "Available")}
										</Badge>
									) : (
										<Badge color="gray" variant="light">
											{t("pages.modelFit.recommendations.catalogOnly", "Catalog only")}
										</Badge>
									)}
								</Table.Td>
								{showActions ? (
									<Table.Td>
										{/* Download is offered for a not-installed row that carries a model name to download. Installed rows
										    and catalog-only rows (pullModelName === null) show nothing actionable. */}
										{!recommendation.isInstalled && recommendation.pullModelName ? (
											<Button
												size="xs"
												variant="light"
												leftSection={<IconCloudDownload size={14} />}
												loading={downloadingModelName === recommendation.pullModelName}
												disabled={downloadingModelName === recommendation.pullModelName}
												onClick={() => onDownload?.(recommendation)}
												data-testid={`model-fit-download-button-${recommendation.rank}`}
											>
												{downloadingModelName === recommendation.pullModelName
													? t("pages.modelFit.recommendations.downloading", "Downloading…")
													: t("pages.modelFit.recommendations.download", "Download")}
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
