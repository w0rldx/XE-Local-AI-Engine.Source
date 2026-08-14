import { Button, Group, Select, Text } from "@mantine/core";
import { IconCpu, IconExternalLink, IconRefresh } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { CatalogInfoCard } from "@/features/model-fit/components/CatalogInfoCard";
import { HardwareProfileCard } from "@/features/model-fit/components/HardwareProfileCard";
import { InferenceProfilePanel } from "@/features/model-fit/components/InferenceProfilePanel";
import { formatModelFitTimestamp } from "@/features/model-fit/components/ModelFitFormatters";
import { NoScheduleAlert } from "@/features/model-fit/components/NoScheduleAlert";
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
		<PageShell>
			<PageHeader
				icon={<IconCpu size={24} />}
				title={t("pages.modelFit.recommendations.title", "Local model advisor")}
				subtitle={t(
					"pages.modelFit.recommendations.subtitle",
					"Hardware-aware local model guidance for this node. Detect hardware and pick the use case to see ranked, fit-checked model recommendations.",
				)}
				actions={
					<>
						<Button
							variant="default"
							leftSection={<IconExternalLink size={16} />}
							onClick={openScheduler}
							data-testid="model-fit-scheduler-link"
						>
							{t("pages.modelFit.recommendations.schedulerLink", "Scheduler")}
						</Button>
						<Button
							variant="default"
							leftSection={<IconExternalLink size={16} />}
							onClick={openModels}
							data-testid="model-fit-models-link"
						>
							{t("pages.modelFit.recommendations.modelsLink", "Model management")}
						</Button>
						<Button
							leftSection={<IconRefresh size={16} />}
							loading={model.isRefreshing}
							disabled={!model.canRefresh}
							onClick={model.onRefresh}
							data-testid="model-fit-refresh-button"
						>
							{t("pages.modelFit.recommendations.refreshButton", "Refresh now")}
						</Button>
					</>
				}
			/>

			<HardwareProfileCard
				profile={model.hardware.profile}
				isLoading={model.hardware.isLoading}
				isFetching={model.hardware.isFetching}
				error={model.hardware.error}
				onRefresh={model.hardware.onRefresh}
			/>

			{!model.hasSchedule && !model.isLoadingJobs ? <NoScheduleAlert onOpenScheduler={openScheduler} /> : null}

			<SectionCard data-tour="recommendation-install">
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
			</SectionCard>

			{model.catalog.data ? (
				<CatalogInfoCard catalog={model.catalog.data} onRefresh={model.catalog.onRefresh} isRefreshing={model.catalog.isRefreshing} />
			) : null}

			{/* Inference Optimizer: tuned llama.cpp launch profiles for this node. A distinct, unobtrusive
			    section below the recommendations — outcomes only (status + tok/s + VRAM), never raw launch flags. */}
			<InferenceProfilePanel />
		</PageShell>
	);
}
