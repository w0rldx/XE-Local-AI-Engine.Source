import { Alert, Anchor, Badge, Button, Card, Container, Group, Loader, Select, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconCpu, IconExternalLink, IconInfoCircle, IconRefresh } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { toast } from "@/core/ui/notifications/Toast";
import { DownloadProgressPanel } from "@/features/model-fit/components/DownloadProgressPanel";
import { GgufBrowsePanel } from "@/features/model-fit/components/GgufBrowsePanel";
import { GgufDownloadDialog } from "@/features/model-fit/components/GgufDownloadDialog";
import { HardwareProfileCard } from "@/features/model-fit/components/HardwareProfileCard";
import { HfTokenPanel } from "@/features/model-fit/components/HfTokenPanel";
import { LlamaCppVersionPanel } from "@/features/model-fit/components/LlamaCppVersionPanel";
import { formatModelFitTimestamp } from "@/features/model-fit/components/ModelFitFormatters";
import { RecommendationTable } from "@/features/model-fit/components/RecommendationTable";
import { RunningModelsPanel } from "@/features/model-fit/components/RunningModelsPanel";
import { useModelFitSchedulerEvents } from "@/features/model-fit/hooks/useModelFitSchedulerEvents";
import {
	defaultGgufQuant,
	type GgufRepository,
	type GgufRepositoryFile,
	type LlamaCppVariant,
	type ModelFitRecommendation,
	type ModelFitUseCase,
	modelFitUseCases,
	modelRecommendationCheckTemplateId,
	recommendationRefreshLimit,
	type RunningModel,
} from "@/features/model-fit/models/ModelFitModels";
import {
	useBrowseGgufRepositories,
	useCancelGgufDownload,
	useEjectRunningModel,
	useEnsureLlamaCppBinary,
	useHardwareProfile,
	useHfTokenStatus,
	useLatestRecommendations,
	useLlamaCppVersion,
	useRefreshRecommendations,
	useRunningModels,
	useSetHfToken,
	useStartGgufDownload,
} from "@/features/model-fit/queries/useModelFit";
import { useModelFitManagementStore } from "@/features/model-fit/stores/ModelFitManagementStore";
import { useScheduledJobs } from "@/features/scheduler/queries/useScheduler";

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

export function ModelRecommendationsPage() {
	const { t } = useTranslation();
	const navigate = useNavigate();

	const useCase = useModelFitManagementStore((state) => state.useCase);
	const setUseCase = useModelFitManagementStore((state) => state.actions.setUseCase);
	const browseQuery = useModelFitManagementStore((state) => state.browseQuery);
	const setBrowseQuery = useModelFitManagementStore((state) => state.actions.setBrowseQuery);
	const tokenDraft = useModelFitManagementStore((state) => state.tokenDraft);
	const setTokenDraft = useModelFitManagementStore((state) => state.actions.setTokenDraft);
	const clearTokenDraft = useModelFitManagementStore((state) => state.actions.clearTokenDraft);

	// Forced hardware re-probe flag — flipped on by the refresh action so the next profile query re-detects the box.
	const [hardwareRefresh, setHardwareRefresh] = useState(false);
	// Names of GGUF downloads started this session — there is no byte-level progress from the backend, so the page
	// tracks which downloads it kicked off to surface them (indeterminate + cancel) in the download panel.
	const [inFlightDownloads, setInFlightDownloads] = useState<readonly string[]>([]);
	// The llama.cpp version GET may trigger the first prebuilt binary download backend-side, so it must not run on
	// mount. Flipped on only when the operator explicitly clicks "Check version" in the llama.cpp panel.
	const [versionChecked, setVersionChecked] = useState(false);
	// The repo whose quant picker dialog is open (null = closed). Selecting a browse row opens the dialog so the
	// operator picks the exact quant (incl. Unsloth Dynamic UD- quants) instead of always pulling the default Q4_K_M.
	const [downloadRepo, setDownloadRepo] = useState<GgufRepository | null>(null);

	const filters = useMemo(() => ({ useCase }), [useCase]);
	const latestQuery = useLatestRecommendations(filters);
	const jobsQuery = useScheduledJobs();
	const refreshMutation = useRefreshRecommendations();

	const hardwareQuery = useHardwareProfile(hardwareRefresh);
	const runningQuery = useRunningModels();
	const versionQuery = useLlamaCppVersion(versionChecked);
	const hfTokenQuery = useHfTokenStatus();
	const browseQueryResult = useBrowseGgufRepositories(browseQuery, true);

	const startDownload = useStartGgufDownload();
	const cancelDownload = useCancelGgufDownload();
	const ejectModel = useEjectRunningModel();
	const ensureBinary = useEnsureLlamaCppBinary();
	const setHfToken = useSetHfToken();

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

	// Marks a model as in-flight (deduped) so the download panel surfaces it.
	const markInFlight = useCallback((modelName: string): void => {
		setInFlightDownloads((current) => (current.includes(modelName) ? current : [...current, modelName]));
	}, []);

	const removeInFlight = useCallback((modelName: string): void => {
		setInFlightDownloads((current) => current.filter((name) => name !== modelName));
	}, []);

	// Starts a GGUF download by repo id (the model name the backend resolves rides the response). On success the model
	// is tracked as in-flight; alreadyInFlight responses are surfaced too (the download was already running).
	const startGgufDownload = useCallback(
		(repoId: string, fileName?: string, quant?: string): void => {
			startDownload.mutate(
				{ repoId, fileName, quant },
				{
					onSuccess: (response) => {
						const modelName = response?.modelName ?? repoId;
						markInFlight(modelName);
						if (response?.alreadyInFlight) {
							toast.info(t("pages.modelFit.download.alreadyInFlight", "That download is already in progress."));
						} else {
							toast.success(t("pages.modelFit.download.started", "Download started."));
						}
					},
					onError: (error) =>
						toast.error(errorMessage(error, t("pages.modelFit.download.error", "Could not start the download."))),
				},
			);
		},
		[startDownload, markInFlight, t],
	);

	const handleRecommendationDownload = (recommendation: ModelFitRecommendation): void => {
		// The recommendation row carries the pullable model name (the repo/model the advisor chose) + its quant.
		if (recommendation.pullModelName) {
			startGgufDownload(recommendation.pullModelName, undefined, recommendation.quantization ?? defaultGgufQuant);
		}
	};

	// Opens the quant picker for a browse row instead of immediately pulling the default quant.
	const handleBrowseDownload = (repository: GgufRepository): void => {
		setDownloadRepo(repository);
	};

	// Confirms a specific quant from the picker: downloads the exact chosen file (fileName is resolved verbatim by the
	// backend, so a Dynamic UD- quant downloads unambiguously) and closes the dialog.
	const handleConfirmQuantDownload = (repoId: string, file: GgufRepositoryFile): void => {
		startGgufDownload(repoId, file.fileName, file.quant);
		setDownloadRepo(null);
	};

	// Fallback used when the picker has no files to offer (degraded/unreachable inspection): download the default quant
	// by repo id only, restoring the pre-picker one-click capability so a degraded inspect never blocks downloading.
	const handleConfirmDefaultDownload = (repoId: string): void => {
		startGgufDownload(repoId, undefined, defaultGgufQuant);
		setDownloadRepo(null);
	};

	const handleCancelDownload = (modelName: string): void => {
		cancelDownload.mutate(modelName, {
			onSuccess: () => {
				removeInFlight(modelName);
				toast.success(t("pages.modelFit.download.cancelled", "Download cancelled."));
			},
			onError: (error) =>
				toast.error(errorMessage(error, t("pages.modelFit.download.cancelError", "Could not cancel the download."))),
		});
	};

	const handleEject = (model: RunningModel): void => {
		ejectModel.mutate(
			{ modelName: model.modelName, role: model.role || undefined },
			{
				onSuccess: () => {
					removeInFlight(model.modelName);
					toast.success(t("pages.modelFit.running.ejected", "Model ejected."));
				},
				onError: (error) => toast.error(errorMessage(error, t("pages.modelFit.running.ejectError", "Could not eject the model."))),
			},
		);
	};

	// Operator-initiated llama.cpp version probe. Latches the flag so the (possibly download-triggering) GET fires
	// once on demand; a subsequent click re-fetches the now-enabled query.
	const handleCheckVersion = (): void => {
		if (!versionChecked) {
			setVersionChecked(true);
			return;
		}
		versionQuery.refetch().catch(() => undefined);
	};

	const handleEnsureBinary = (variant: LlamaCppVariant): void => {
		ensureBinary.mutate(variant, {
			onSuccess: () => toast.success(t("pages.modelFit.llamaCpp.ensured", "llama.cpp binary ready.")),
			onError: (error) =>
				toast.error(errorMessage(error, t("pages.modelFit.llamaCpp.ensureError", "Could not ensure the llama.cpp binary."))),
		});
	};

	const handleSaveToken = (): void => {
		setHfToken.mutate(tokenDraft.trim(), {
			onSuccess: () => {
				clearTokenDraft();
				toast.success(t("pages.modelFit.hfToken.saved", "Token saved."));
			},
			onError: (error) => toast.error(errorMessage(error, t("pages.modelFit.hfToken.saveError", "Could not save the token."))),
		});
	};

	const handleClearToken = (): void => {
		setHfToken.mutate(undefined, {
			onSuccess: () => {
				clearTokenDraft();
				toast.success(t("pages.modelFit.hfToken.cleared", "Token cleared."));
			},
			onError: (error) => toast.error(errorMessage(error, t("pages.modelFit.hfToken.clearError", "Could not clear the token."))),
		});
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
								"Hardware-aware local model guidance for this node. Detect hardware, browse and download GGUF models, manage the llama.cpp runtime, and eject running models.",
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

				<DownloadProgressPanel
					inFlight={inFlightDownloads}
					onCancel={handleCancelDownload}
					cancellingModelName={cancelDownload.isPending ? (cancelDownload.variables ?? null) : null}
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

				<GgufBrowsePanel
					repositories={browseQueryResult.data ?? []}
					isLoading={browseQueryResult.isLoading && browseQuery.trim().length > 0}
					error={browseQueryResult.error}
					hasSearched={browseQuery.trim().length > 0}
					onSearch={setBrowseQuery}
					onDownload={handleBrowseDownload}
					downloadingRepoId={downloadingModelName}
				/>

				<GgufDownloadDialog
					repository={downloadRepo}
					onClose={() => setDownloadRepo(null)}
					onConfirm={handleConfirmQuantDownload}
					onConfirmDefault={handleConfirmDefaultDownload}
					isDownloading={startDownload.isPending}
				/>

				<RunningModelsPanel
					runningModels={runningQuery.data ?? []}
					isLoading={runningQuery.isLoading}
					error={runningQuery.error}
					onEject={handleEject}
					ejectingModelName={ejectModel.isPending ? (ejectModel.variables?.modelName ?? null) : null}
				/>

				<LlamaCppVersionPanel
					version={versionQuery.data}
					// `isLoading` is true while a DISABLED query idles, so gate the spinner on an actual in-flight fetch — the
					// panel shows its idle "not checked yet" state until the operator triggers the (download-capable) probe.
					isLoading={versionChecked && versionQuery.isFetching}
					error={versionChecked ? versionQuery.error : null}
					hasChecked={versionChecked}
					onCheck={handleCheckVersion}
					onEnsure={handleEnsureBinary}
					isEnsuring={ensureBinary.isPending}
				/>

				<HfTokenPanel
					hasToken={hfTokenQuery.data ?? false}
					isLoading={hfTokenQuery.isLoading}
					tokenDraft={tokenDraft}
					onTokenDraftChange={setTokenDraft}
					onSave={handleSaveToken}
					onClear={handleClearToken}
					isSaving={setHfToken.isPending}
				/>
			</Stack>
		</Container>
	);
}
