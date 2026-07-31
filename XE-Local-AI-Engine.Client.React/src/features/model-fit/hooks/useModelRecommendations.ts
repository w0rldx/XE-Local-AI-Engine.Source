import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { toast } from "@/core/ui/notifications/Toast";
import { useModelFitSchedulerEvents } from "@/features/model-fit/hooks/useModelFitSchedulerEvents";
import {
	defaultGgufQuant,
	type HardwareProfile,
	type ModelFitCatalogInfo,
	type ModelFitLatestRecommendations,
	type ModelFitRecommendation,
	type ModelFitUseCase,
	modelRecommendationCheckTemplateId,
	recommendationRefreshLimit,
} from "@/features/model-fit/models/ModelFitModels";
import {
	useHardwareProfile,
	useLatestRecommendations,
	useModelFitCatalog,
	useRefreshModelFitCatalog,
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

// The hardware-profile card's data + re-probe action, grouped so the card can be wired from one prop.
interface HardwareState {
	readonly profile: HardwareProfile | undefined;
	readonly isLoading: boolean;
	readonly isFetching: boolean;
	readonly error: unknown;
	readonly onRefresh: () => void;
}

// The curated-catalog footer's data + refresh action.
interface CatalogState {
	readonly data: ModelFitCatalogInfo | undefined;
	readonly onRefresh: () => void;
	readonly isRefreshing: boolean;
}

// Everything the recommendations page composition needs: the use-case filter, the recommendation snapshot query state,
// the hardware card state, the catalog footer state, the refresh-now control state, and the per-row download hand-off.
export interface UseModelRecommendationsResult {
	readonly useCase: ModelFitUseCase;
	readonly onUseCaseChange: (value: string | null) => void;
	readonly latest: ModelFitLatestRecommendations | undefined;
	readonly hasCache: boolean;
	readonly isLoadingRecommendations: boolean;
	readonly recommendationsError: unknown;
	readonly hardware: HardwareState;
	readonly catalog: CatalogState;
	// Refresh-now (fires the existing model-recommendation-check job). `hasSchedule` is false when no such job exists.
	readonly hasSchedule: boolean;
	readonly isLoadingJobs: boolean;
	readonly canRefresh: boolean;
	readonly isRefreshing: boolean;
	readonly onRefresh: () => void;
	// Per-row GGUF download hand-off to the Model Management feature.
	readonly downloadingModelName: string | null;
	readonly onDownload: (recommendation: ModelFitRecommendation) => void;
}

// Owns all server state, derived flags, and side-effecting handlers for the local model advisor page, so the page
// itself is pure composition. Reads are cache-only (see useModelFit); a refresh enqueues an async scheduler run and a
// recommendation-row download is handed off to the Model Management feature via the shared GGUF store.
export function useModelRecommendations(): UseModelRecommendationsResult {
	const { t } = useTranslation();

	const useCase = useModelFitManagementStore((state) => state.useCase);
	const setUseCase = useModelFitManagementStore((state) => state.actions.setUseCase);

	// Forced hardware re-probe flag — flipped on by the refresh action so the next profile query re-detects the box.
	const [hardwareRefresh, setHardwareRefresh] = useState(false);

	const filters = useMemo(() => ({ useCase }), [useCase]);
	const latestQuery = useLatestRecommendations(filters);
	const jobsQuery = useScheduledJobs();
	const refreshMutation = useRefreshRecommendations();
	const catalogQuery = useModelFitCatalog();
	const refreshCatalogMutation = useRefreshModelFitCatalog();

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

	const handleRefreshCatalog = (): void => {
		refreshCatalogMutation.mutate(undefined, {
			// The node may have no configured live-refresh source (ModelCatalogOptions.RefreshUrl unset), in which case the
			// endpoint still returns 200 with the unchanged bundled snapshot. Only claim the catalog was refreshed when the
			// response says a refresh source actually exists; otherwise say plainly that there was nothing to fetch.
			onSuccess: (data) => {
				if (data?.refreshSourceConfigured) {
					toast.success(t("pages.modelFit.recommendations.catalog.toasts.refreshed", "The model catalog was refreshed."));
				} else {
					toast.info(
						t(
							"pages.modelFit.recommendations.catalog.toasts.noRefreshSource",
							"No catalog refresh source is configured, so the bundled catalog is in use. There is nothing to fetch.",
						),
					);
				}
			},
			onError: (error) =>
				toast.error(
					errorMessage(error, t("pages.modelFit.recommendations.catalog.toasts.refreshError", "Could not refresh the model catalog.")),
				),
		});
	};

	return {
		useCase,
		onUseCaseChange: handleUseCaseChange,
		latest,
		hasCache: latest?.hasCache ?? false,
		isLoadingRecommendations: latestQuery.isLoading,
		recommendationsError: latestQuery.error,
		hardware: {
			profile: hardwareQuery.data,
			isLoading: hardwareQuery.isLoading,
			isFetching: hardwareQuery.isFetching,
			error: hardwareQuery.error,
			onRefresh: handleHardwareRefresh,
		},
		catalog: {
			data: catalogQuery.data,
			onRefresh: handleRefreshCatalog,
			isRefreshing: refreshCatalogMutation.isPending,
		},
		hasSchedule: refreshJob !== undefined,
		isLoadingJobs: jobsQuery.isLoading,
		canRefresh,
		isRefreshing: refreshMutation.isPending,
		onRefresh: handleRefresh,
		downloadingModelName: startDownload.isPending ? (startDownload.variables?.repoId ?? null) : null,
		onDownload: handleRecommendationDownload,
	};
}
