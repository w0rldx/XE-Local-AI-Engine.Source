import { Alert, Button, Card, Container, Group, Loader, NumberInput, Stack, Switch, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconCode, IconDeviceFloppy, IconRefresh, IconSettings } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import type { SaveNodeSettingsResponse } from "@/core/api/generated";
import {
	downloadRecommendedEmbeddingMutation,
	downloadRecommendedRerankerMutation,
	getNodeSettingsOptions,
	getNodeSettingsQueryKey,
	listLocalModelsOptions,
	saveNodeSettingsMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { toast } from "@/core/ui/notifications/Toast";
import { toChatModelOptions, toDraftModelOptions } from "@/features/chat/pages/ChatModelOptions";
import { DownloadProgressPanel } from "@/features/models/components/DownloadProgressPanel";
import { useActiveGgufDownloads, useCancelGgufDownload } from "@/features/models/queries/useGgufDownload";
import { useGgufBrowseStore } from "@/features/models/stores/GgufBrowseStore";
import { HfTokenPanel } from "@/features/node-settings/components/HfTokenPanel";
import { ImageRuntimeSourceBuildCard } from "@/features/node-settings/components/ImageRuntimeSourceBuildCard";
import { LlamaCppUpdaterPanel } from "@/features/node-settings/components/LlamaCppUpdaterPanel";
import { NodeSettingsFieldsCard } from "@/features/node-settings/components/NodeSettingsFieldsCard";
import { SourceBuildCard } from "@/features/node-settings/components/SourceBuildCard";
import {
	buildNodeSettingsRequest,
	type NodeSettingsFieldsForm,
	toNodeSettingsFieldBounds,
	toNodeSettingsFieldsForm,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";
import {
	type NodeSettingsTimeoutInput,
	nodeSettingsDefaults,
	toValidNodeSettingsTimeoutSeconds,
} from "@/features/node-settings/models/NodeSettingsModel";
import { useHfTokenStatus, useSetHfToken } from "@/features/node-settings/queries/useLocalRuntime";
import { useHfTokenStore } from "@/features/node-settings/stores/HfTokenStore";
import { VoiceSettingsCard } from "@/features/voice/components/VoiceSettingsCard";

function errorMessage(error: unknown): string {
	return error instanceof Error ? error.message : "Unexpected node settings error";
}

function runtimeErrorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

export function NodeSettings() {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const {
		data: settings,
		isLoading: settingsIsLoading,
		error: settingsError,
		refetch: settingsRefetch,
		isFetching: settingsIsFetching,
	} = useQuery(withResponseValidation(getNodeSettingsOptions()));
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const { toggle: toggleDeveloperMode } = useDeveloperModeStore((state) => state.actions);
	const [timeoutSeconds, setTimeoutSeconds] = useState<NodeSettingsTimeoutInput>(
		nodeSettingsDefaults.maxMessageRequestTimeoutSeconds,
	);

	// The migrated appsettings knobs. `fieldsForm` is the editable draft; `fieldsBaselineRef` is the last-loaded
	// authoritative state — only fields that differ from the baseline are sent on save (optional-request semantics).
	// The baseline is read only inside handlers (never rendered), so it lives in a ref to avoid an extra render on load.
	const [fieldsForm, setFieldsForm] = useState<NodeSettingsFieldsForm>(() => toNodeSettingsFieldsForm(undefined));
	// Lazy ref init: build the default baseline once (on first render), not on every render. The effect overwrites it
	// with the loaded settings as soon as they arrive.
	const fieldsBaselineRef = useRef<NodeSettingsFieldsForm | null>(null);
	if (fieldsBaselineRef.current === null) {
		fieldsBaselineRef.current = toNodeSettingsFieldsForm(undefined);
	}
	const [fieldErrors, setFieldErrors] = useState<Readonly<Record<string, string>>>({});
	const fieldBounds = useMemo(() => toNodeSettingsFieldBounds(settings), [settings]);

	// Models offered as the speculative draft model (value = model name, resolved server-side). Purpose-built MTP
	// drafters are `Draft`-kind and so are absent from the chat list — they must be offered HERE, which is the only
	// place they are usable at all.
	const { data: localModels } = useQuery(withResponseValidation(listLocalModelsOptions()));
	const draftModelOptions = useMemo(
		() =>
			toDraftModelOptions(localModels?.items ?? [], localModels?.isAvailable ?? false).map((option) => ({
				value: option.value,
				label: option.label,
			})),
		[localModels],
	);

	// Keep-warm targets the supervised llama-server runtime, so only installed chat models owned by the llama.cpp
	// provider are eligible. Provider matching is case-insensitive because provider names cross a persisted/API boundary.
	const installedKeepWarmModelOptions = useMemo(
		() =>
			toChatModelOptions(
				(localModels?.items ?? []).filter((model) => (model.provider ?? "").toLowerCase() === "llamacpp"),
				localModels?.isAvailable ?? false,
			).map((option) => ({ value: option.value, label: option.label })),
		[localModels],
	);
	const selectedKeepWarmModel = fieldsForm.keepModelWarmModelName.trim();
	const keepWarmModelUnavailable =
		localModels !== undefined &&
		fieldsForm.keepModelWarmEnabled &&
		selectedKeepWarmModel.length > 0 &&
		!installedKeepWarmModelOptions.some((option) => option.value === selectedKeepWarmModel);
	const keepWarmModelOptions = useMemo(
		() =>
			keepWarmModelUnavailable
				? [
						...installedKeepWarmModelOptions,
						{
							value: selectedKeepWarmModel,
							label: t("pages.nodeSettings.fields.keepModelWarm.unavailableOption", "{{model}} (not installed)", {
								model: selectedKeepWarmModel,
							}),
						},
					]
				: installedKeepWarmModelOptions,
		[installedKeepWarmModelOptions, keepWarmModelUnavailable, selectedKeepWarmModel, t],
	);
	const visibleFieldErrors = keepWarmModelUnavailable
		? { ...fieldErrors, keepModelWarmModelName: "unavailableKeepWarmModel" }
		: fieldErrors;

	// Installed models offered as the knowledge-base reranker. Reranker GGUFs are not a chat kind, so this list is NOT
	// filtered to chat-capable models (value = model name, resolved server-side).
	const rerankerModelOptions = useMemo(
		() =>
			(localModels?.items ?? [])
				.map((model) => ({ value: model.modelName ?? "", label: model.modelName ?? "" }))
				.filter((option) => option.value.length > 0),
		[localModels],
	);

	// One-click recommended-reranker download. Progress reuses the SAME GgufDownload feed as Model Management: the
	// coordinator streams status over SignalR, the shared GgufBrowseStore tracks in-flight names, and completion
	// invalidates the installed-models list (which this page's reranker dropdown reads) — so the model becomes
	// selectable without a manual refresh. We remember the canonical model name from the mutation response so the
	// progress panel + duplicate-guard scope to exactly the reranker download this page initiated.
	const rerankerDownloadStatuses = useActiveGgufDownloads();
	const inFlightDownloads = useGgufBrowseStore((state) => state.inFlightDownloads);
	const markInFlight = useGgufBrowseStore((state) => state.actions.markInFlight);
	const removeInFlight = useGgufBrowseStore((state) => state.actions.removeInFlight);
	const [recommendedRerankerModelName, setRecommendedRerankerModelName] = useState<string | null>(null);
	const cancelGgufDownloadMutation = useCancelGgufDownload();

	const isRecommendedRerankerInFlight =
		recommendedRerankerModelName !== null && inFlightDownloads.includes(recommendedRerankerModelName);
	const rerankerInFlight = useMemo(
		() => (isRecommendedRerankerInFlight && recommendedRerankerModelName !== null ? [recommendedRerankerModelName] : []),
		[isRecommendedRerankerInFlight, recommendedRerankerModelName],
	);

	const downloadRecommendedReranker = useMutation({
		...withResponseValidation(downloadRecommendedRerankerMutation()),
		onSuccess: (response) => {
			setRecommendedRerankerModelName(response.modelName);
			if (response.alreadyInstalled) {
				toast.info(
					t(
						"pages.nodeSettings.fields.rerankerModel.downloadAlreadyInstalled",
						"The recommended reranker ({{model}}) is already installed.",
						{
							model: response.modelName,
						},
					),
				);
				return;
			}
			// Mark in-flight immediately so the button duplicate-guards and the progress panel appears without waiting for
			// the first SignalR push (markInFlight is idempotent, so an already-in-flight rejoin is a no-op).
			markInFlight(response.modelName);
			toast.info(
				response.alreadyInFlight
					? t(
							"pages.nodeSettings.fields.rerankerModel.downloadInFlight",
							"The recommended reranker ({{model}}) is already downloading.",
							{
								model: response.modelName,
							},
						)
					: t("pages.nodeSettings.fields.rerankerModel.downloadStarted", "Downloading the recommended reranker ({{model}}).", {
							model: response.modelName,
						}),
			);
		},
		onError: (error) =>
			toast.error(
				runtimeErrorMessage(
					error,
					t("pages.nodeSettings.fields.rerankerModel.downloadError", "Could not start the recommended reranker download."),
				),
			),
	});

	const handleDownloadRecommendedReranker = (): void => {
		downloadRecommendedReranker.mutate({});
	};

	const handleCancelRerankerDownload = (modelName: string): void => {
		cancelGgufDownloadMutation.mutate(modelName, {
			onSuccess: () => {
				removeInFlight(modelName);
				toast.success(t("pages.models.gguf.download.cancelled", "Download cancelled."));
			},
			onError: (error) =>
				toast.error(runtimeErrorMessage(error, t("pages.models.gguf.download.cancelError", "Could not cancel the download."))),
		});
	};

	// One-click recommended-embedding download. The embedding model is not a node-settings field (there is nothing to
	// select/save) — it just needs to be installed for the knowledge base to index documents at all. Progress reuses
	// the same GgufDownload feed and in-flight tracking as the reranker download above.
	const [recommendedEmbeddingModelName, setRecommendedEmbeddingModelName] = useState<string | null>(null);

	const isRecommendedEmbeddingInFlight =
		recommendedEmbeddingModelName !== null && inFlightDownloads.includes(recommendedEmbeddingModelName);
	const embeddingInFlight = useMemo(
		() => (isRecommendedEmbeddingInFlight && recommendedEmbeddingModelName !== null ? [recommendedEmbeddingModelName] : []),
		[isRecommendedEmbeddingInFlight, recommendedEmbeddingModelName],
	);

	const downloadRecommendedEmbedding = useMutation({
		...withResponseValidation(downloadRecommendedEmbeddingMutation()),
		onSuccess: (response) => {
			setRecommendedEmbeddingModelName(response.modelName);
			if (response.alreadyInstalled) {
				toast.info(
					t(
						"pages.nodeSettings.fields.embeddingModel.downloadAlreadyInstalled",
						"The recommended embedding model ({{model}}) is already installed.",
						{
							model: response.modelName,
						},
					),
				);
				return;
			}
			// Mark in-flight immediately so the button duplicate-guards and the progress panel appears without waiting for
			// the first SignalR push (markInFlight is idempotent, so an already-in-flight rejoin is a no-op).
			markInFlight(response.modelName);
			toast.info(
				response.alreadyInFlight
					? t(
							"pages.nodeSettings.fields.embeddingModel.downloadInFlight",
							"The recommended embedding model ({{model}}) is already downloading.",
							{
								model: response.modelName,
							},
						)
					: t(
							"pages.nodeSettings.fields.embeddingModel.downloadStarted",
							"Downloading the recommended embedding model ({{model}}).",
							{
								model: response.modelName,
							},
						),
			);
		},
		onError: (error) =>
			toast.error(
				runtimeErrorMessage(
					error,
					t(
						"pages.nodeSettings.fields.embeddingModel.downloadError",
						"Could not start the recommended embedding model download.",
					),
				),
			),
	});

	const handleDownloadRecommendedEmbedding = (): void => {
		downloadRecommendedEmbedding.mutate({});
	};

	useEffect(() => {
		if (settings?.maxMessageRequestTimeoutSeconds !== undefined) {
			setTimeoutSeconds(settings.maxMessageRequestTimeoutSeconds);
		}
		if (settings !== undefined) {
			const loaded = toNodeSettingsFieldsForm(settings);
			setFieldsForm(loaded);
			fieldsBaselineRef.current = loaded;
		}
	}, [settings]);

	const minTimeout = settings?.minMessageRequestTimeoutSeconds ?? nodeSettingsDefaults.minMessageRequestTimeoutSeconds;
	const maxTimeout =
		settings?.maxAllowedMessageRequestTimeoutSeconds ?? nodeSettingsDefaults.maxAllowedMessageRequestTimeoutSeconds;
	const timeoutToSave = useMemo(
		() => toValidNodeSettingsTimeoutSeconds(timeoutSeconds, minTimeout, maxTimeout),
		[maxTimeout, minTimeout, timeoutSeconds],
	);

	const handleFieldChange = <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]): void => {
		setFieldsForm((current) => ({ ...current, [field]: value }));
		// Clear a field's stale error as soon as the operator edits it.
		setFieldErrors((current) => {
			if (current[field as string] === undefined) {
				return current;
			}
			const next = { ...current };
			delete next[field as string];
			return next;
		});
	};

	const saveMutation = useMutation({
		...withResponseValidation(saveNodeSettingsMutation()),
		onSuccess: async (updatedSettings: SaveNodeSettingsResponse) => {
			toast.success(
				t("pages.nodeSettings.saved", "Node settings saved. Capability reporting was requested for the worker connection."),
			);
			setTimeoutSeconds(updatedSettings.maxMessageRequestTimeoutSeconds ?? nodeSettingsDefaults.maxMessageRequestTimeoutSeconds);
			const loaded = toNodeSettingsFieldsForm(updatedSettings);
			setFieldsForm(loaded);
			fieldsBaselineRef.current = loaded;
			setFieldErrors({});
			queryClient.setQueryData(getNodeSettingsQueryKey(), updatedSettings);
			await queryClient.invalidateQueries({ queryKey: getNodeSettingsQueryKey() });
		},
		onError: (error) => toast.error(errorMessage(error)),
	});

	// Builds the merged PUT body (timeout + only-changed migrated fields). Developer-only fields are included ONLY when
	// developer mode is on (off-mode the advanced card is unmounted, so an off-mode save must not touch them).
	const handleSave = (): void => {
		if (timeoutToSave === undefined) {
			return;
		}
		if (keepWarmModelUnavailable) {
			setFieldErrors((current) => ({ ...current, keepModelWarmModelName: "unavailableKeepWarmModel" }));
			toast.error(t("pages.nodeSettings.fields.validationError", "Some settings are invalid. Fix the highlighted fields."));
			return;
		}
		const baseline = fieldsBaselineRef.current ?? toNodeSettingsFieldsForm(undefined);
		const { body, errors } = buildNodeSettingsRequest(fieldsForm, baseline, fieldBounds, developerMode);
		if (Object.keys(errors).length > 0) {
			setFieldErrors(errors);
			toast.error(t("pages.nodeSettings.fields.validationError", "Some settings are invalid. Fix the highlighted fields."));
			return;
		}
		setFieldErrors({});
		saveMutation.mutate({ body: { ...body, maxMessageRequestTimeoutSeconds: timeoutToSave } });
	};

	const canSave = timeoutToSave !== undefined && !saveMutation.isPending;

	// HF token: the draft lives in a store so it survives a remount; the token itself is write-only (never read back
	// into the draft). The llama.cpp runtime card (installed tag/variant, recommended/upstream, ensure/update) is fully
	// self-contained in LlamaCppUpdaterPanel and owns its own data layer.
	const tokenDraft = useHfTokenStore((state) => state.tokenDraft);
	const setTokenDraft = useHfTokenStore((state) => state.actions.setTokenDraft);
	const clearTokenDraft = useHfTokenStore((state) => state.actions.clearTokenDraft);

	const hfTokenQuery = useHfTokenStatus();
	const setHfToken = useSetHfToken();

	const handleSaveToken = (): void => {
		setHfToken.mutate(tokenDraft.trim(), {
			onSuccess: () => {
				clearTokenDraft();
				toast.success(t("pages.nodeSettings.hfToken.saved", "Token saved."));
			},
			onError: (error) =>
				toast.error(runtimeErrorMessage(error, t("pages.nodeSettings.hfToken.saveError", "Could not save the token."))),
		});
	};

	const handleClearToken = (): void => {
		setHfToken.mutate(undefined, {
			onSuccess: () => {
				clearTokenDraft();
				toast.success(t("pages.nodeSettings.hfToken.cleared", "Token cleared."));
			},
			onError: (error) =>
				toast.error(runtimeErrorMessage(error, t("pages.nodeSettings.hfToken.clearError", "Could not clear the token."))),
		});
	};

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Stack gap={4}>
					<Text size="sm" tt="uppercase" fw={700} c="dimmed">
						{t("common.workerNode", "Worker Node")}
					</Text>
					<Title order={2}>Node settings</Title>
					<Text c="dimmed">Tune non-secret local runtime settings stored on this worker.</Text>
				</Stack>

				{settingsIsLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">Loading node settings…</Text>
					</Group>
				) : null}

				{settingsError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{errorMessage(settingsError)}
					</Alert>
				) : null}

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						<Group justify="space-between" align="center">
							<Title order={3}>Local chat runtime</Title>
							<IconSettings size={22} />
						</Group>
						<Text c="dimmed">
							The maximum message request timeout is included in capability reports so the platform can respect this worker's
							local runtime limit.
						</Text>
						<NumberInput
							label="Maximum message request timeout"
							description={`Allowed range: ${minTimeout}–${maxTimeout} seconds.`}
							suffix=" seconds"
							min={minTimeout}
							max={maxTimeout}
							step={5}
							allowDecimal={false}
							value={timeoutSeconds}
							onChange={setTimeoutSeconds}
							error={timeoutToSave === undefined ? `Enter a whole number from ${minTimeout} to ${maxTimeout}.` : undefined}
						/>
						<Group>
							<Button
								leftSection={<IconDeviceFloppy size={16} />}
								onClick={handleSave}
								loading={saveMutation.isPending}
								disabled={!canSave}
								data-testid="node-settings-save-button"
							>
								Save settings
							</Button>
							<Button
								variant="subtle"
								leftSection={<IconRefresh size={16} />}
								onClick={() => settingsRefetch()}
								disabled={settingsIsFetching}
							>
								Reload
							</Button>
						</Group>
					</Stack>
				</Card>

				<LlamaCppUpdaterPanel />

				<SourceBuildCard />

				<ImageRuntimeSourceBuildCard />

				<NodeSettingsFieldsCard
					form={fieldsForm}
					bounds={fieldBounds}
					errors={visibleFieldErrors}
					onChange={handleFieldChange}
					showDeveloperFields={developerMode}
					draftModelOptions={draftModelOptions}
					keepWarmModelOptions={keepWarmModelOptions}
					rerankerModelOptions={rerankerModelOptions}
					onDownloadRecommendedReranker={handleDownloadRecommendedReranker}
					isDownloadRecommendedRerankerPending={downloadRecommendedReranker.isPending}
					isRecommendedRerankerInFlight={isRecommendedRerankerInFlight}
					onDownloadRecommendedEmbedding={handleDownloadRecommendedEmbedding}
					isDownloadRecommendedEmbeddingPending={downloadRecommendedEmbedding.isPending}
					isRecommendedEmbeddingInFlight={isRecommendedEmbeddingInFlight}
				/>

				<DownloadProgressPanel
					inFlight={[...rerankerInFlight, ...embeddingInFlight]}
					downloadStatuses={rerankerDownloadStatuses}
					onCancel={handleCancelRerankerDownload}
					cancellingModelName={cancelGgufDownloadMutation.isPending ? (cancelGgufDownloadMutation.variables ?? null) : null}
				/>

				<Group>
					<Button
						leftSection={<IconDeviceFloppy size={16} />}
						onClick={handleSave}
						loading={saveMutation.isPending}
						disabled={!canSave}
						data-testid="node-settings-fields-save-button"
					>
						{t("pages.nodeSettings.fields.save", "Save node settings")}
					</Button>
				</Group>

				<HfTokenPanel
					hasToken={hfTokenQuery.data ?? false}
					isLoading={hfTokenQuery.isLoading}
					tokenDraft={tokenDraft}
					onTokenDraftChange={setTokenDraft}
					onSave={handleSaveToken}
					onClear={handleClearToken}
					isSaving={setHfToken.isPending}
				/>

				<VoiceSettingsCard />

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						<Group justify="space-between" align="center">
							<Title order={3}>{t("pages.nodeSettings.developerMode.title", "Developer settings")}</Title>
							<IconCode size={22} />
						</Group>
						<Switch
							label={t("pages.nodeSettings.developerMode.label", "Developer mode")}
							description={t(
								"pages.nodeSettings.developerMode.description",
								"Enables advanced, experimental controls in the app (e.g. chat sampling options). Stored in this browser only.",
							)}
							checked={developerMode}
							onChange={() => toggleDeveloperMode()}
							data-testid="developer-mode-switch"
						/>
					</Stack>
				</Card>
			</Stack>
		</Container>
	);
}
