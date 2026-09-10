import { Badge, Group, Loader, Select, SimpleGrid, Stack, Text } from "@mantine/core";
import { type ReactNode, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import {
	fitLevelColor,
	formatContextTokens,
	formatMemoryMb,
	formatModelFitMetric,
} from "@/features/model-fit/components/ModelFitFormatters";
import {
	defaultModelFitUseCase,
	type ModelFitRecommendation,
	type ModelFitUseCase,
	modelFitUseCases,
} from "@/features/model-fit/models/ModelFitModels";
import { useLatestRecommendations } from "@/features/model-fit/queries/useModelFit";

interface ModelFitPanelProps {
	modelName: string;
}

type Translate = (key: string, fallback: string) => string;

// Localized label for a use-case slug, reusing the canonical model-fit use-case labels so this panel stays in sync
// with the recommendations page. Falls back to a title-cased slug for any future use case without a translation.
function labelForUseCase(t: Translate, useCase: string): string {
	return t(`pages.modelFit.recommendations.useCases.${useCase}`, useCase.charAt(0).toUpperCase() + useCase.slice(1));
}

// Finds the cached recommendation that corresponds to an installed model. llmfit identifies a model by any of its
// name projections, so match the installed model name (case-insensitive, trimmed) against the pull tag first, then
// the provider name, then the display name. Returns null when the model is absent from this use-case snapshot.
function findRecommendationForModel(
	recommendations: readonly ModelFitRecommendation[],
	modelName: string,
): ModelFitRecommendation | null {
	const target = modelName.trim().toLowerCase();
	if (!target) {
		return null;
	}

	return (
		recommendations.find((recommendation) =>
			[recommendation.pullModelName, recommendation.providerModelName, recommendation.modelName]
				.filter((value): value is string => Boolean(value))
				.some((value) => value.trim().toLowerCase() === target),
		) ?? null
	);
}

// One labelled metric cell in the fit grid.
function FitMetric({ label, children }: { label: string; children: ReactNode }) {
	return (
		<Stack gap={2}>
			<Text size="xs" tt="uppercase" fw={700} c="dimmed">
				{label}
			</Text>
			<Text size="sm">{children}</Text>
		</Stack>
	);
}

// llmfit-enriched fit information for a single installed model, surfaced inside the model details dialog. Reads are
// cache-only (they never run llmfit) and use-case scoped, so the panel lets the operator switch use case and joins
// the installed model into the latest cached snapshot by name. An absent snapshot or an unmatched model is reported
// explicitly rather than left blank.
export function ModelFitPanel({ modelName }: ModelFitPanelProps) {
	const { t } = useTranslation();
	const [useCase, setUseCase] = useState<ModelFitUseCase>(defaultModelFitUseCase);
	const recommendationsQuery = useLatestRecommendations({ useCase });
	const latest = recommendationsQuery.data;

	const recommendation = useMemo(
		() => (latest?.hasCache ? findRecommendationForModel(latest.recommendations, modelName) : null),
		[latest, modelName],
	);

	const useCaseOptions = modelFitUseCases.map((value) => ({ value, label: labelForUseCase(t, value) }));

	return (
		<Stack gap="md">
			<Group justify="space-between" align="flex-end">
				<Select
					label={t("pages.modelFit.recommendations.useCaseLabel", "Use case")}
					data={useCaseOptions}
					value={useCase}
					allowDeselect={false}
					onChange={(value) => value && setUseCase(value as ModelFitUseCase)}
					size="xs"
					w={200}
					data-testid="model-fit-use-case-select"
				/>
				{latest?.lastRefreshedAtUtc ? (
					<Text size="xs" c="dimmed">
						{t("pages.modelFit.panel.cached", "Cached {{timestamp}}", {
							timestamp: formatTimestamp(latest.lastRefreshedAtUtc),
						})}
					</Text>
				) : null}
			</Group>

			{recommendationsQuery.isFetching ? (
				<Group gap="sm">
					<Loader size="sm" />
					<Text c="dimmed">{t("pages.modelFit.panel.loading", "Loading model-fit data…")}</Text>
				</Group>
			) : null}

			{recommendationsQuery.error ? (
				<InlineErrorAlert
					message={apiErrorMessage(
						recommendationsQuery.error,
						t("pages.modelFit.recommendations.errors.load", "Could not load recommendations."),
					)}
				/>
			) : null}

			{!recommendationsQuery.isFetching && !recommendationsQuery.error && latest && !latest.hasCache ? (
				<Text c="dimmed" data-testid="model-fit-no-cache">
					{t(
						"pages.modelFit.panel.noCache",
						"No cached llmfit recommendations for the {{useCase}} use case. Refresh from the Model recommendations page to populate them.",
						{ useCase: labelForUseCase(t, useCase) },
					)}
				</Text>
			) : null}

			{!recommendationsQuery.isFetching && latest?.hasCache && !recommendation ? (
				<Text c="dimmed" data-testid="model-fit-no-match">
					{t("pages.modelFit.panel.noMatch", "{{model}} is not in the latest {{useCase}} recommendations.", {
						model: modelName,
						useCase: labelForUseCase(t, useCase),
					})}
				</Text>
			) : null}

			{recommendation ? (
				<Stack gap="sm" data-testid="model-fit-result">
					<Group gap="sm">
						<Badge color="blue" variant="light">
							{t("pages.modelFit.panel.rank", "Rank #{{rank}}", { rank: recommendation.rank })}
						</Badge>
						{recommendation.fitLevel ? (
							<Badge color={fitLevelColor(recommendation.fitLevel)} variant="light">
								{recommendation.fitLevel}
							</Badge>
						) : null}
						{recommendation.runMode ? <Badge variant="outline">{recommendation.runMode}</Badge> : null}
					</Group>
					<SimpleGrid cols={{ base: 2, sm: 3 }} spacing="md">
						<FitMetric label={t("pages.modelFit.recommendations.columns.score", "Score")}>
							{formatModelFitMetric(recommendation.score, "", 1)}
						</FitMetric>
						<FitMetric label={t("pages.modelFit.recommendations.columns.tps", "Est. TPS")}>
							{formatModelFitMetric(recommendation.estimatedTokensPerSecond, "", 1)}
						</FitMetric>
						<FitMetric label={t("pages.modelFit.recommendations.columns.context", "Context")}>
							{formatContextTokens(recommendation.contextTokens)}
						</FitMetric>
						<FitMetric label={t("pages.modelFit.panel.quantization", "Quantization")}>
							{recommendation.quantization ?? "—"}
						</FitMetric>
						<FitMetric label={t("pages.modelFit.recommendations.columns.ram", "Req. RAM")}>
							{formatMemoryMb(recommendation.requiredRamMb)}
						</FitMetric>
						<FitMetric label={t("pages.modelFit.recommendations.columns.vram", "Req. VRAM")}>
							{formatMemoryMb(recommendation.requiredVramMb)}
						</FitMetric>
					</SimpleGrid>
				</Stack>
			) : null}
		</Stack>
	);
}
