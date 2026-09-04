import { Badge, Button, Group, Stack, Table, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconCloudDownload, IconCpu, IconDownload, IconServer2 } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { TablePaginationFooter } from "@/core/ui/components/TablePagination/TablePaginationFooter";
import { useTablePagination } from "@/core/ui/components/TablePagination/useTablePagination";
import {
	fitLevelColor,
	formatContextTokens,
	formatMemoryMb,
	formatModelFitMetric,
	formatModelFitReleaseDate,
} from "@/features/model-fit/components/ModelFitFormatters";
import type { ModelFitRecommendation } from "@/features/model-fit/models/ModelFitModels";

interface RecommendationTableProps {
	recommendations: readonly ModelFitRecommendation[];
	// When provided, an action cell renders a Download (GGUF) button per row. The parent drives the actual download
	// (and progress / cancel) through the download store; the table only signals intent and reflects the in-flight name.
	onDownload?: (recommendation: ModelFitRecommendation) => void;
	// Model name currently being downloaded (the parent's in-flight download), used to disable that row's button.
	downloadingModelName?: string | null;
}

// A row runs in CPU mode when the advisor's run mode is CPU (VRAM unknown / no GPU accel). Surfaced as a distinct
// badge so the operator sees the fit was estimated against the RAM budget, not VRAM.
function isCpuMode(runMode: string | null): boolean {
	return runMode?.toLowerCase() === "cpu";
}

// Badge color for a curated-catalog quality tier: S (gold) is the top pick, A (blue) is a strong pick, B (gray) is a
// solid pick. A row without a catalog match carries no tier and renders no badge.
function tierColor(tier: ModelFitRecommendation["tier"]): string {
	switch (tier) {
		case "S":
			return "yellow";
		case "A":
			return "blue";
		case "B":
			return "gray";
		default:
			return "gray";
	}
}

// The tier badge sits next to the model name when the advisor matched this row to a curated-catalog entry with a
// quality tier. Absent for a row with no catalog match (tier === null).
function TierBadge({ tier, rank }: { tier: ModelFitRecommendation["tier"]; rank: number }) {
	const { t } = useTranslation();
	if (tier === null) {
		return null;
	}
	return (
		<Badge color={tierColor(tier)} variant="filled" size="sm" data-testid={`model-fit-tier-badge-${rank}`}>
			{t(`pages.modelFit.recommendations.tier.${tier.toLowerCase()}`, tier)}
		</Badge>
	);
}

// The MoE-offload badge is an honesty signal: when the advisor's fit estimate offloads some Mixture-of-Experts
// layers to CPU/RAM instead of running the whole model on GPU, the row is slower than a plain GPU fit but preserves
// more quality than dropping to a smaller quant. The tooltip breaks down the GPU/RAM split when the advisor reported
// both figures; otherwise it shows the badge alone.
function MoeOffloadBadge({ recommendation }: { recommendation: ModelFitRecommendation }) {
	const { t } = useTranslation();
	if (!recommendation.expertsOffloaded) {
		return null;
	}
	const badge = (
		<Badge
			color="grape"
			variant="light"
			size="sm"
			leftSection={<IconServer2 size={12} />}
			data-testid={`model-fit-moe-offload-badge-${recommendation.rank}`}
		>
			{t("pages.modelFit.recommendations.moeOffload.badge", "Experts on CPU — slower, higher quality")}
		</Badge>
	);
	if (recommendation.gpuGb === null || recommendation.cpuGb === null) {
		return badge;
	}
	return (
		<Tooltip
			label={t("pages.modelFit.recommendations.moeOffload.breakdown", "GPU {{gpuGb}} GB + RAM {{cpuGb}} GB", {
				gpuGb: recommendation.gpuGb,
				cpuGb: recommendation.cpuGb,
			})}
		>
			{badge}
		</Tooltip>
	);
}

// Advisory-only quantized-KV hint under the Req. VRAM figure: when the advisor computed a Q8_0-KV estimate for a
// catalog row AND that estimate fits the budget, show the smaller footprint as a dimmed secondary line. The row's
// primary figures are ALWAYS the fp16-KV estimate (the default launch uses fp16 KV), so this never claims the model
// fits — the tooltip spells out the assumptions (advisory, needs flash attention, savings are estimates). An advisory
// that still would not fit is withheld: "would not fit either way" is noise, not guidance.
function KvQuantHint({ recommendation }: { recommendation: ModelFitRecommendation }) {
	const { t } = useTranslation();
	if (recommendation.kvQuant === null || recommendation.kvQuantFits !== true || recommendation.kvQuantEstimatedGb === null) {
		return null;
	}
	return (
		<Tooltip
			label={t(
				"pages.modelFit.recommendations.kvQuant.tooltip",
				"Advisory estimate only — the fit shown uses the default fp16 KV cache. A quantized KV cache requires flash attention and matching launch flags; the savings are an estimate, not guaranteed compatibility.",
			)}
			multiline={true}
			maw={280}
		>
			<Text size="xs" c="dimmed" data-testid={`model-fit-kv-quant-hint-${recommendation.rank}`}>
				{t("pages.modelFit.recommendations.kvQuant.hint", "≈ {{gb}} GB with {{quant}} KV cache", {
					gb: recommendation.kvQuantEstimatedGb.toFixed(1),
					quant: recommendation.kvQuant,
				})}
			</Text>
		</Tooltip>
	);
}

// The KV cost of one token of context, plus the model's attention shape, as a second dimmed line under the Req. VRAM
// figure. Deliberately independent of KvQuantHint: that one is an advisory about a DIFFERENT launch configuration, this
// one is what the row's own context target costs. Rendered only when the header could size the KV term AND the quant
// label came with it — an unlabelled byte count is ambiguous by a factor of two, so half a fact is not shown.
function KvPerTokenHint({ recommendation }: { recommendation: ModelFitRecommendation }) {
	const { t } = useTranslation();
	const bytesPerToken = recommendation.kvBytesPerToken ?? null;
	const quant = recommendation.kvBytesPerTokenQuant ?? null;
	if (bytesPerToken === null || quant === null) {
		return null;
	}
	const arch = recommendation.attentionArch ?? null;
	const archLabel = arch === null ? null : t(`pages.modelFit.recommendations.attentionArch.${arch}`, arch.toUpperCase());
	const perToken = t("pages.modelFit.recommendations.kvPerToken.hint", "{{kb}} KB/token ({{quant}} KV)", {
		kb: (bytesPerToken / 1024).toFixed(1),
		quant: quant.toLowerCase(),
	});
	return (
		<Tooltip
			label={t(
				"pages.modelFit.recommendations.kvPerToken.tooltip",
				"KV-cache cost of one token at {{context}} context, computed with a {{quant}} KV cache (the chat launch default) rather than the fp16 cache the required-memory figures above use.",
				{
					context: formatContextTokens(recommendation.contextTokens),
					quant: quant.toLowerCase(),
				},
			)}
			multiline={true}
			maw={280}
		>
			<Text size="xs" c="dimmed" data-testid={`model-fit-kv-per-token-${recommendation.rank}`}>
				{archLabel === null ? perToken : `${archLabel} · ${perToken}`}
			</Text>
		</Tooltip>
	);
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
									<Group gap={4} wrap="wrap" align="center">
										<Text size="sm" fw={500}>
											{recommendation.catalogDisplayName ?? recommendation.modelName}
										</Text>
										<TierBadge tier={recommendation.tier} rank={recommendation.rank} />
									</Group>
									{recommendation.catalogDisplayName ? (
										<Text size="xs" c="dimmed">
											{recommendation.modelName}
										</Text>
									) : recommendation.providerModelName ? (
										<Text size="xs" c="dimmed">
											{recommendation.providerModelName}
										</Text>
									) : null}
									{recommendation.catalogNotes ? (
										<Text size="xs" c="dimmed" data-testid={`model-fit-catalog-notes-${recommendation.rank}`}>
											{recommendation.catalogNotes}
										</Text>
									) : null}
									{!recommendation.isTrustedPublisher ? (
										<Tooltip
											label={t(
												"pages.modelFit.recommendations.untrustedPublisherHint",
												"This publisher is not a known GGUF packager — review the repo before downloading.",
											)}
											multiline={true}
											maw={260}
										>
											<Badge
												color="yellow"
												variant="light"
												size="sm"
												mt={4}
												leftSection={<IconAlertTriangle size={12} />}
												data-testid={`model-fit-untrusted-badge-${recommendation.rank}`}
											>
												{t("pages.modelFit.recommendations.untrustedPublisher", "Unverified publisher")}
											</Badge>
										</Tooltip>
									) : null}
								</Table.Td>
								<Table.Td>{formatModelFitMetric(recommendation.score, "", 1)}</Table.Td>
								<Table.Td>
									<Stack gap={4}>
										<FitCell recommendation={recommendation} />
										<MoeOffloadBadge recommendation={recommendation} />
									</Stack>
								</Table.Td>
								<Table.Td>{formatModelFitMetric(recommendation.estimatedTokensPerSecond, "", 1)}</Table.Td>
								<Table.Td>{formatContextTokens(recommendation.contextTokens)}</Table.Td>
								<Table.Td>{recommendation.quantization ?? "—"}</Table.Td>
								<Table.Td>{formatMemoryMb(recommendation.requiredRamMb)}</Table.Td>
								<Table.Td>
									{formatMemoryMb(recommendation.requiredVramMb)}
									<KvQuantHint recommendation={recommendation} />
									<KvPerTokenHint recommendation={recommendation} />
								</Table.Td>
								{/* Date-only, locale-formatted; the raw ISO value stays available as the cell's tooltip. */}
								<Table.Td title={recommendation.releaseDate ?? undefined}>
									{formatModelFitReleaseDate(recommendation.releaseDate)}
								</Table.Td>
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
