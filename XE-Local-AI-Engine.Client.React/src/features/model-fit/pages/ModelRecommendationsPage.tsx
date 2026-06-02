import { Alert, Anchor, Badge, Button, Card, Container, Group, Loader, Select, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconCpu, IconExternalLink, IconInfoCircle, IconRefresh } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { RecommendationTable } from "@/features/model-fit/components/RecommendationTable";
import { formatModelFitTimestamp } from "@/features/model-fit/components/ModelFitFormatters";
import { useModelFitSchedulerEvents } from "@/features/model-fit/hooks/useModelFitSchedulerEvents";
import {
	defaultModelFitProviderName,
	type ModelFitUseCase,
	modelFitUseCases,
	modelRecommendationCheckTemplateId,
} from "@/features/model-fit/models/ModelFitModels";
import { useLatestRecommendations, useRefreshRecommendations } from "@/features/model-fit/queries/useModelFit";
import { useModelFitManagementStore } from "@/features/model-fit/stores/ModelFitManagementStore";
import { useScheduledJobs } from "@/features/scheduler/queries/useScheduler";

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

export function ModelRecommendationsPage() {
	const { t } = useTranslation();
	const navigate = useNavigate();

	// Live invalidation: when a model-recommendation-check scheduler run terminates, the latest query refetches.
	useModelFitSchedulerEvents();

	const useCase = useModelFitManagementStore((state) => state.useCase);
	const setUseCase = useModelFitManagementStore((state) => state.actions.setUseCase);

	const filters = useMemo(() => ({ useCase, providerName: defaultModelFitProviderName }), [useCase]);
	const latestQuery = useLatestRecommendations(filters);
	// Reuse the scheduler job-list query to discover an existing model-recommendation-check job. The refresh
	// endpoint fires an EXISTING job; it never creates one — so refresh is gated on such a job being present.
	const jobsQuery = useScheduledJobs();
	const refreshMutation = useRefreshRecommendations();

	const latest = latestQuery.data;

	// The first ENABLED model-recommendation-check job (or the first of any state if none enabled). Refresh is only
	// offered when at least one such job exists; otherwise the operator must create one in the Scheduler UI (job
	// definition CRUD lives there, not here).
	const refreshJob = useMemo(() => {
		const jobs = jobsQuery.data ?? [];
		const matching = jobs.filter((job) => job.templateId === modelRecommendationCheckTemplateId);
		return matching.find((job) => job.enabled) ?? matching[0];
	}, [jobsQuery.data]);

	const canRefresh = refreshJob !== undefined && !refreshMutation.isPending;

	const handleRefresh = (): void => {
		if (refreshJob === undefined) {
			return;
		}
		refreshMutation.mutate(refreshJob.id);
	};

	const handleUseCaseChange = (value: string | null): void => {
		if (value !== null) {
			setUseCase(value as ModelFitUseCase);
		}
	};

	const useCaseData = modelFitUseCases.map((value) => ({
		value,
		label: t(`pages.modelFit.recommendations.useCases.${value}`, value),
	}));

	const hasCache = latest?.hasCache ?? false;

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Group justify="space-between" align="flex-start">
					<Stack gap={4}>
						<Text size="sm" tt="uppercase" fw={700} c="dimmed">
							{t("pages.modelFit.eyebrow", "Worker Node")}
						</Text>
						<Group gap="xs" align="center">
							<IconCpu size={24} />
							<Title order={2}>{t("pages.modelFit.recommendations.title", "Model recommendations")}</Title>
						</Group>
						<Text c="dimmed">
							{t(
								"pages.modelFit.recommendations.subtitle",
								"Hardware-aware local model guidance for this node. Results are cached — use Refresh now to run a new recommendation check through the scheduler.",
							)}
						</Text>
					</Stack>
					<Group gap="sm">
						<Button
							variant="default"
							leftSection={<IconExternalLink size={16} />}
							onClick={() => navigate({ to: nodeRoutePaths.scheduler })}
							data-testid="model-fit-scheduler-link"
						>
							{t("pages.modelFit.recommendations.schedulerLink", "Scheduler")}
						</Button>
						<Button
							variant="default"
							leftSection={<IconExternalLink size={16} />}
							onClick={() => navigate({ to: nodeRoutePaths.models })}
							data-testid="model-fit-models-link"
						>
							{t("pages.modelFit.recommendations.modelsLink", "Model management")}
						</Button>
						<Button
							leftSection={<IconRefresh size={16} />}
							loading={refreshMutation.isPending}
							disabled={!canRefresh}
							onClick={handleRefresh}
							data-testid="model-fit-refresh-button"
						>
							{t("pages.modelFit.recommendations.refreshButton", "Refresh now")}
						</Button>
					</Group>
				</Group>

				{refreshJob === undefined && !jobsQuery.isLoading ? (
					<Alert color="blue" icon={<IconInfoCircle size={16} />} data-testid="model-fit-no-job-guidance">
						<Group justify="space-between" align="center">
							<Text size="sm">
								{t(
									"pages.modelFit.recommendations.noJobGuidance",
									"No model-recommendation-check schedule exists yet. Create one in the Scheduler to enable refreshing.",
								)}
							</Text>
							<Anchor
								component="button"
								type="button"
								onClick={() => navigate({ to: nodeRoutePaths.scheduler })}
								data-testid="model-fit-no-job-scheduler-link"
							>
								{t("pages.modelFit.recommendations.openScheduler", "Open Scheduler")}
							</Anchor>
						</Group>
					</Alert>
				) : null}

				{refreshMutation.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="model-fit-refresh-error">
						{errorMessage(
							refreshMutation.error,
							t("pages.modelFit.recommendations.errors.refresh", "Could not start a refresh."),
						)}
					</Alert>
				) : null}

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						<Group justify="space-between" align="flex-end">
							<Select
								label={t("pages.modelFit.recommendations.useCaseLabel", "Use case")}
								data={useCaseData}
								value={useCase}
								onChange={handleUseCaseChange}
								allowDeselect={false}
								data-testid="model-fit-use-case-select"
							/>
							{latest?.lastRefreshedAtUtc ? (
								<Text size="sm" c="dimmed" data-testid="model-fit-last-refreshed">
									{t("pages.modelFit.recommendations.lastRefreshed", "Last refreshed: {{time}}", {
										time: formatModelFitTimestamp(latest.lastRefreshedAtUtc),
									})}
								</Text>
							) : null}
						</Group>

						{latestQuery.isLoading ? (
							<Group gap="sm">
								<Loader size="sm" />
								<Text c="dimmed">{t("pages.modelFit.recommendations.loading", "Loading recommendations…")}</Text>
							</Group>
						) : null}

						{latestQuery.error ? (
							<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="model-fit-recommendations-error">
								{errorMessage(
									latestQuery.error,
									t("pages.modelFit.recommendations.errors.load", "Could not load recommendations."),
								)}
							</Alert>
						) : null}

						{!latestQuery.isLoading && !latestQuery.error && hasCache && latest ? (
							<Stack gap="md" data-testid="model-fit-snapshot">
								<Group gap="xl">
									<Stack gap={0}>
										<Text size="xs" c="dimmed">
											{t("pages.modelFit.recommendations.snapshot.status", "Status")}
										</Text>
										<Badge variant="light">{latest.status ?? "—"}</Badge>
									</Stack>
									<Stack gap={0}>
										<Text size="xs" c="dimmed">
											{t("pages.modelFit.recommendations.snapshot.useCase", "Use case")}
										</Text>
										<Text size="sm">
											{latest.useCase
												? t(`pages.modelFit.recommendations.useCases.${latest.useCase}`, latest.useCase)
												: "—"}
										</Text>
									</Stack>
									<Stack gap={0}>
										<Text size="xs" c="dimmed">
											{t("pages.modelFit.recommendations.snapshot.provider", "Provider")}
										</Text>
										<Text size="sm">{latest.providerName ?? "—"}</Text>
									</Stack>
									<Stack gap={0}>
										<Text size="xs" c="dimmed">
											{t("pages.modelFit.recommendations.snapshot.sourceImage", "Source image")}
										</Text>
										<Text size="sm" ff="monospace">
											{latest.sourceImageId ?? "—"}
										</Text>
									</Stack>
								</Group>

								{latest.recommendations.length > 0 ? (
									<RecommendationTable recommendations={latest.recommendations} />
								) : (
									<Text c="dimmed" data-testid="model-fit-recommendations-empty-list">
										{t(
											"pages.modelFit.recommendations.emptyList",
											"The latest snapshot returned no recommendations.",
										)}
									</Text>
								)}
							</Stack>
						) : null}

						{!latestQuery.isLoading && !latestQuery.error && !hasCache ? (
							<Alert color="gray" icon={<IconInfoCircle size={16} />} data-testid="model-fit-no-cache">
								{t(
									"pages.modelFit.recommendations.noCache",
									"No cached recommendation snapshot for this use case yet. Run a recommendation check from the scheduler, or use Refresh now if a schedule exists.",
								)}
							</Alert>
						) : null}
					</Stack>
				</Card>
			</Stack>
		</Container>
	);
}
