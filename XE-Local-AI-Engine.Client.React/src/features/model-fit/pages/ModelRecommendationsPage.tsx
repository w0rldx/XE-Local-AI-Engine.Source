import { Card, Container, Group, Select, Stack, Text } from "@mantine/core";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { CatalogInfoCard } from "@/features/model-fit/components/CatalogInfoCard";
import { HardwareProfileCard } from "@/features/model-fit/components/HardwareProfileCard";
import { InferenceProfilePanel } from "@/features/model-fit/components/InferenceProfilePanel";
import { formatModelFitTimestamp } from "@/features/model-fit/components/ModelFitFormatters";
import { NoScheduleAlert } from "@/features/model-fit/components/NoScheduleAlert";
import { RecommendationsHeader } from "@/features/model-fit/components/RecommendationsHeader";
import { RecommendationsResults } from "@/features/model-fit/components/RecommendationsResults";
import { useModelRecommendations } from "@/features/model-fit/hooks/useModelRecommendations";
import { modelFitUseCases } from "@/features/model-fit/models/ModelFitModels";

// The local model advisor page: a hardware-aware, cache-only view of ranked GGUF recommendations for this node. All
// server state, derived flags, and side-effecting handlers live in useModelRecommendations; this component is pure
// composition + layout, delegating each region to a focused child component.
export function ModelRecommendationsPage() {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const model = useModelRecommendations();

	const openScheduler = (): void => {
		navigate({ to: nodeRoutePaths.scheduler });
	};
	const openModels = (): void => {
		navigate({ to: nodeRoutePaths.models });
	};

	const useCaseData = modelFitUseCases.map((value) => ({
		value,
		label: t(`pages.modelFit.recommendations.useCases.${value}`, value),
	}));

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<RecommendationsHeader
					canRefresh={model.canRefresh}
					isRefreshing={model.isRefreshing}
					onRefresh={model.onRefresh}
					onOpenScheduler={openScheduler}
					onOpenModels={openModels}
				/>

				<HardwareProfileCard
					profile={model.hardware.profile}
					isLoading={model.hardware.isLoading}
					isFetching={model.hardware.isFetching}
					error={model.hardware.error}
					onRefresh={model.hardware.onRefresh}
				/>

				{!model.hasSchedule && !model.isLoadingJobs ? <NoScheduleAlert onOpenScheduler={openScheduler} /> : null}

				<Card withBorder={true} radius="md" p="lg" data-tour="recommendation-install">
					<Stack gap="md">
						<Group justify="space-between" align="flex-end">
							<Select
								label={t("pages.modelFit.recommendations.useCaseLabel", "Use case")}
								data={useCaseData}
								value={model.useCase}
								onChange={model.onUseCaseChange}
								allowDeselect={false}
								data-testid="model-fit-use-case-select"
							/>
							{model.latest?.lastRefreshedAtUtc ? (
								<Text size="sm" c="dimmed" data-testid="model-fit-last-refreshed">
									{t("pages.modelFit.recommendations.lastRefreshed", "Last refreshed: {{time}}", {
										time: formatModelFitTimestamp(model.latest.lastRefreshedAtUtc),
									})}
								</Text>
							) : null}
						</Group>

						<RecommendationsResults
							isLoading={model.isLoadingRecommendations}
							error={model.recommendationsError}
							hasCache={model.hasCache}
							latest={model.latest}
							onDownload={model.onDownload}
							downloadingModelName={model.downloadingModelName}
						/>
					</Stack>
				</Card>

				{model.catalog.data ? (
					<CatalogInfoCard catalog={model.catalog.data} onRefresh={model.catalog.onRefresh} isRefreshing={model.catalog.isRefreshing} />
				) : null}

				{/* Inference Optimizer (Lane C3): tuned llama.cpp launch profiles for this node. A distinct, unobtrusive
				    section below the recommendations — outcomes only (status + tok/s + VRAM), never raw launch flags. */}
				<InferenceProfilePanel />
			</Stack>
		</Container>
	);
}
