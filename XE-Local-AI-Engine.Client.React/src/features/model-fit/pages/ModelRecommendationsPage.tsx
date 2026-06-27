import { Alert, Anchor, Badge, Button, Card, Container, Group, Loader, Select, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconCpu, IconExternalLink, IconInfoCircle, IconRefresh } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { toast } from "@/core/ui/notifications/Toast";
import { HardwareProfileCard } from "@/features/model-fit/components/HardwareProfileCard";
import { InferenceProfilePanel } from "@/features/model-fit/components/InferenceProfilePanel";
import { formatModelFitTimestamp } from "@/features/model-fit/components/ModelFitFormatters";
import { RecommendationTable } from "@/features/model-fit/components/RecommendationTable";
import { useModelFitSchedulerEvents } from "@/features/model-fit/hooks/useModelFitSchedulerEvents";
import {
	defaultGgufQuant,
	type ModelFitRecommendation,
	type ModelFitUseCase,
	modelFitUseCases,
	modelRecommendationCheckTemplateId,
	recommendationRefreshLimit,
} from "@/features/model-fit/models/ModelFitModels";
import {
	useHardwareProfile,
	useLatestRecommendations,
	useRefreshRecommendations,
} from "@/features/model-fit/queries/useModelFit";
import { useModelFitManagementStore } from "@/features/model-fit/stores/ModelFitManagementStore";
// Cross-feature hand-off (operator-approved): a download started from a recommendation row is owned by the Model
// Management feature so it surfaces — with progress + cancel — on that page's download panel via the shared store.
import { useStartGgufDownload } from "@/features/models/queries/useGgufDownload";
import { useGgufBrowseStore } from "@/features/models/stores/GgufBrowseStore";
import { useScheduledJobs } from "@/features/scheduler/queries/useScheduler";

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

export function ModelRecommendationsPage() {
	const { t } = useTranslation();
	const navigate = useNavigate();

	const useCase = useModelFitManagementStore((state) => state.useCase);
	const setUseCase = useModelFitManagementStore((state) => state.actions.setUseCase);

	// Forced hardware re-probe flag — flipped on by the refresh action so the next profile query re-detects the box.
	const [hardwareRefresh, setHardwareRefresh] = useState(false);

	const filters = useMemo(() => ({ useCase }), [useCase]);
	const latestQuery = useLatestRecommendations(filters);
	const jobsQuery = useScheduledJobs();
	const refreshMutation = useRefreshRecommendations();

	const hardwareQuery = useHardwareProfile(hardwareRefresh);
	const startDownload = useStartGgufDownload();
	// The advisor hands the download off to the Model Management feature: marking the started model in the shared store
	// makes it appear (with progress + cancel) on that page's download-progress panel.
	const markInFlight = useGgufBrowseStore((state) => state.actions.markInFlight);

	const latest = latestQuery.data;

	const refreshJob = useMemo(() => {
		const jobs = jobsQuery.data ?? [];
		const matching = jobs.filter((job) => job.templateId === modelRecommendationCheckTemplateId);
		return matching.find((job) => job.enabled) ?? matching[0];
	}, [jobsQuery.data]);

	useModelFitSchedulerEvents(refreshJob?.id);

	const canRefresh = refreshJob !== undefined && !refreshMutation.isPending;

	const handleRefresh = (): void => {
		if (refreshJob === undefined) {
			return;
		}
		refreshMutation.mutate(
			{ scheduledJobId: refreshJob.id, useCase, limit: recommendationRefreshLimit },
			{
				// The refresh enqueues an async scheduler run, so there is no immediate result to show. Confirm the request
				// landed with an info toast (keyed by a stable id so rapid clicks update one toast instead of stacking) — the
				// terminal Succeeded/Failed/Cancelled toast arrives later from the scheduler hub (ModelFitRefreshNotifications).
				onSuccess: () =>
					toast.info(
						t(
							"pages.modelFit.recommendations.toasts.started",
							"Checking for the latest model recommendations. We'll let you know when they're ready.",
						),
						{ title: t("pages.modelFit.recommendations.toasts.startedTitle", "Refresh started"), id: "model-fit-refresh-start" },
					),
				onError: (error) =>
					toast.error(errorMessage(error, t("pages.modelFit.recommendations.errors.refresh", "Could not start a refresh."))),
			},
		);
	};

	const handleUseCaseChange = (value: string | null): void => {
		if (value !== null) {
			setUseCase(value as ModelFitUseCase);
		}
	};

	const handleHardwareRefresh = (): void => {
		// Latch the re-probe flag so the hardware query (keyed on `refresh`) re-detects, then refetch.
		setHardwareRefresh(true);
		hardwareQuery.refetch().catch(() => undefined);
	};

	const handleRecommendationDownload = (recommendation: ModelFitRecommendation): void => {
		// The recommendation row carries the pullable model name (the repo/model the advisor chose) + its quant. The
		// download is OWNED by the Model Management feature: on success we mark the resolved model name in the shared
		// in-flight store so it appears — with progress + cancel — on that page's download-progress panel, then point
		// the operator there (there is no in-flight UI on the advisor itself).
		if (!recommendation.pullModelName) {
			return;
		}
		startDownload.mutate(
			{ repoId: recommendation.pullModelName, fileName: undefined, quant: recommendation.quantization ?? defaultGgufQuant },
			{
				onSuccess: (response) => {
					const modelName = response?.modelName ?? recommendation.pullModelName ?? "";
					markInFlight(modelName);
					if (response?.alreadyInFlight) {
						toast.info(t("pages.models.gguf.download.alreadyInFlight", "That download is already in progress."));
					} else {
						// Direct the operator to Model Management to watch progress / cancel (this page has no in-flight UI).
						toast.success(
							t(
								"pages.modelFit.recommendations.downloadHandoff",
								"Download started. Track or cancel it on the Model management page.",
							),
						);
					}
				},
				onError: (error) =>
					toast.error(errorMessage(error, t("pages.models.gguf.download.error", "Could not start the download."))),
			},
		);
	};

	const useCaseData = modelFitUseCases.map((value) => ({
		value,
		label: t(`pages.modelFit.recommendations.useCases.${value}`, value),
	}));

	const hasCache = latest?.hasCache ?? false;
	const downloadingModelName = startDownload.isPending ? (startDownload.variables?.repoId ?? null) : null;

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
							<Title order={2}>{t("pages.modelFit.recommendations.title", "Local model advisor")}</Title>
						</Group>
						<Text c="dimmed">
							{t(
								"pages.modelFit.recommendations.subtitle",
								"Hardware-aware local model guidance for this node. Detect hardware and pick the use case to see ranked, fit-checked model recommendations.",
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

				<HardwareProfileCard
					profile={hardwareQuery.data}
					isLoading={hardwareQuery.isLoading}
					isFetching={hardwareQuery.isFetching}
					error={hardwareQuery.error}
					onRefresh={handleHardwareRefresh}
				/>

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

				<Card withBorder={true} radius="md" p="lg" data-tour="recommendation-install">
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
											{latest.useCase ? t(`pages.modelFit.recommendations.useCases.${latest.useCase}`, latest.useCase) : "—"}
										</Text>
									</Stack>
								</Group>

								{latest.recommendations.length > 0 ? (
									<RecommendationTable
										recommendations={latest.recommendations}
										onDownload={handleRecommendationDownload}
										downloadingModelName={downloadingModelName}
									/>
								) : (
									<Text c="dimmed" data-testid="model-fit-recommendations-empty-list">
										{t("pages.modelFit.recommendations.emptyList", "The latest snapshot returned no recommendations.")}
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

				{/* Inference Optimizer (Lane C3): tuned llama.cpp launch profiles for this node. A distinct, unobtrusive
				    section below the recommendations — outcomes only (status + tok/s + VRAM), never raw launch flags. */}
				<InferenceProfilePanel />
			</Stack>
		</Container>
	);
}
