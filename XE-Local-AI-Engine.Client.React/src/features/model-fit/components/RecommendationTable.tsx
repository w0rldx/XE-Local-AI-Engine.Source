import { Badge, Table, Text } from "@mantine/core";
import { IconCheck, IconDownload } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import {
	fitLevelColor,
	formatContextTokens,
	formatMemoryMb,
	formatModelFitMetric,
} from "@/features/model-fit/components/ModelFitFormatters";
import type { ModelFitRecommendation } from "@/features/model-fit/models/ModelFitModels";

interface RecommendationTableProps {
	recommendations: readonly ModelFitRecommendation[];
}

// Pure presentation: renders the ranked recommendation rows for a cached snapshot. The parent owns the data and
// the empty/diagnostics state — this table is only rendered when there is at least one recommendation. Each row
// shows rank, model name, score, fit-level + run-mode badges, estimated TPS / context / quantization, and an
// installed-vs-pullable indicator.
export function RecommendationTable({ recommendations }: RecommendationTableProps) {
	const { t } = useTranslation();

	return (
		<Table.ScrollContainer minWidth={920}>
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
						<Table.Th>{t("pages.modelFit.recommendations.columns.installed", "Installed")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{recommendations.map((recommendation) => (
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
							<Table.Td>
								{recommendation.runMode ? (
									<Badge variant="outline">{recommendation.runMode}</Badge>
								) : (
									"—"
								)}
							</Table.Td>
							<Table.Td>{formatModelFitMetric(recommendation.estimatedTokensPerSecond, "", 1)}</Table.Td>
							<Table.Td>{formatContextTokens(recommendation.contextTokens)}</Table.Td>
							<Table.Td>{recommendation.quantization ?? "—"}</Table.Td>
							<Table.Td>{formatMemoryMb(recommendation.requiredRamMb)}</Table.Td>
							<Table.Td>
								{recommendation.isInstalled ? (
									<Badge color="green" variant="light" leftSection={<IconCheck size={12} />}>
										{t("pages.modelFit.recommendations.installed", "Installed")}
									</Badge>
								) : (
									<Badge color="blue" variant="light" leftSection={<IconDownload size={12} />}>
										{t("pages.modelFit.recommendations.pullable", "Pullable")}
									</Badge>
								)}
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}
