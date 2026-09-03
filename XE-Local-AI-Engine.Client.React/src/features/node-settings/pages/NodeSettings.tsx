import { Alert, Button, Group, Loader, NumberInput, Text } from "@mantine/core";
import { IconAlertTriangle, IconDeviceFloppy, IconRefresh, IconSettings } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import type {
	XeLocalAiEngineClientEndpointsNodeSettingsV1SaveNodeSettingsRequest as SaveNodeSettingsRequest,
	SaveNodeSettingsResponse,
} from "@/core/api/generated";
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
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { toast } from "@/core/ui/notifications/Toast";
import { toChatModelOptions, toDraftModelOptions } from "@/features/chat/pages/ChatModelOptions";
import { DownloadProgressPanel } from "@/features/models/components/DownloadProgressPanel";
import { useActiveGgufDownloads, useCancelGgufDownload } from "@/features/models/queries/useGgufDownload";
import { useGgufBrowseStore } from "@/features/models/stores/GgufBrowseStore";
import { ImageRuntimeSourceBuildCard } from "@/features/node-settings/components/ImageRuntimeSourceBuildCard";
import { LlamaCppUpdaterPanel } from "@/features/node-settings/components/LlamaCppUpdaterPanel";
import {
	NodeSettingsAuxiliaryPanels,
	NodeSettingsDeveloperModePanel,
} from "@/features/node-settings/components/NodeSettingsAuxiliaryPanels";
import { NodeSettingsFieldsCard } from "@/features/node-settings/components/NodeSettingsFieldsCard";
import { SourceBuildCard } from "@/features/node-settings/components/SourceBuildCard";
import {
	buildNodeSettingsRequest,
	type NodeSettingsFieldsForm,
	toNodeSettingsFieldBounds,
	toNodeSettingsFieldsForm,
	touchesRestartGatedField,
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
	return apiErrorMessage(error, "Unexpected node settings error");
}

function useNodeSettingsModelOptions(form: NodeSettingsFieldsForm, errors: Readonly<Record<string, string>>) {
	const { t } = useTranslation();
	const { data: localModels } = useQuery(withResponseValidation(listLocalModelsOptions()));
	const draftModelOptions = useMemo(
		() =>
			toDraftModelOptions(localModels?.items ?? [], localModels?.isAvailable ?? false).map((option) => ({
				value: option.value,
				label: option.label,
			})),
		[localModels],
	);
	const installedKeepWarmModelOptions = useMemo(
		() =>
			toChatModelOptions(
				(localModels?.items ?? []).filter((model) => (model.provider ?? "").toLowerCase() === "llamacpp"),
				localModels?.isAvailable ?? false,
			).map((option) => ({ value: option.value, label: option.label })),
		[localModels],
	);
	const selectedKeepWarmModel = form.keepModelWarmModelName.trim();
	const keepWarmModelUnavailable =
		localModels !== undefined &&
		form.keepModelWarmEnabled &&
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
	const rerankerModelOptions = useMemo(
		() =>
			(localModels?.items ?? [])
				.map((model) => ({ value: model.modelName ?? "", label: model.modelName ?? "" }))
				.filter((option) => option.value.length > 0),
		[localModels],
	);

	return {
		draftModelOptions,
		keepWarmModelOptions,
		// The fast model for automatic reasoning effort takes exactly the keep-warm filter: an installed llama.cpp chat
		// model, never a cloud id, an external id or an Ollama name. The backend refuses anything else at save.
		autoEffortFastModelOptions: installedKeepWarmModelOptions,
		rerankerModelOptions,
		keepWarmModelUnavailable,
		visibleErrors: keepWarmModelUnavailable ? { ...errors, keepModelWarmModelName: "unavailableKeepWarmModel" } : errors,
	};
}

function useRecommendedModelDownloads() {
	const { t } = useTranslation();
	const downloadStatuses = useActiveGgufDownloads();
	const inFlightDownloads = useGgufBrowseStore((state) => state.inFlightDownloads);
	const markInFlight = useGgufBrowseStore((state) => state.actions.markInFlight);
	const removeInFlight = useGgufBrowseStore((state) => state.actions.removeInFlight);
	const cancel = useCancelGgufDownload();
	const [rerankerName, setRerankerName] = useState<string | null>(null);
	const [embeddingName, setEmbeddingName] = useState<string | null>(null);
	const rerankerInFlight = rerankerName !== null && inFlightDownloads.includes(rerankerName);
	const embeddingInFlight = embeddingName !== null && inFlightDownloads.includes(embeddingName);
	const progressNames = useMemo(
		() =>
			[rerankerInFlight ? rerankerName : null, embeddingInFlight ? embeddingName : null].filter(
				(name): name is string => name !== null,
			),
		[embeddingInFlight, embeddingName, rerankerInFlight, rerankerName],
	);

	const reranker = useMutation({
		...withResponseValidation(downloadRecommendedRerankerMutation()),
		onSuccess: (response) => {
			setRerankerName(response.modelName);
			if (response.alreadyInstalled) {
				toast.info(
					t(
						"pages.nodeSettings.fields.rerankerModel.downloadAlreadyInstalled",
						"The recommended reranker ({{model}}) is already installed.",
						{ model: response.modelName },
					),
				);
				return;
			}
			markInFlight(response.modelName);
			toast.info(
				response.alreadyInFlight
					? t(
							"pages.nodeSettings.fields.rerankerModel.downloadInFlight",
							"The recommended reranker ({{model}}) is already downloading.",
							{ model: response.modelName },
						)
					: t("pages.nodeSettings.fields.rerankerModel.downloadStarted", "Downloading the recommended reranker ({{model}}).", {
							model: response.modelName,
						}),
			);
		},
		onError: (error) =>
			toast.error(
				apiErrorMessage(
					error,
					t("pages.nodeSettings.fields.rerankerModel.downloadError", "Could not start the recommended reranker download."),
				),
			),
	});

	const embedding = useMutation({
		...withResponseValidation(downloadRecommendedEmbeddingMutation()),
		onSuccess: (response) => {
			setEmbeddingName(response.modelName);
			if (response.alreadyInstalled) {
				toast.info(
					t(
						"pages.nodeSettings.fields.embeddingModel.downloadAlreadyInstalled",
						"The recommended embedding model ({{model}}) is already installed.",
						{ model: response.modelName },
					),
				);
				return;
			}
			markInFlight(response.modelName);
			toast.info(
				response.alreadyInFlight
					? t(
							"pages.nodeSettings.fields.embeddingModel.downloadInFlight",
							"The recommended embedding model ({{model}}) is already downloading.",
							{ model: response.modelName },
						)
					: t(
							"pages.nodeSettings.fields.embeddingModel.downloadStarted",
							"Downloading the recommended embedding model ({{model}}).",
							{ model: response.modelName },
						),
			);
		},
		onError: (error) =>
			toast.error(
				apiErrorMessage(
					error,
					t(
						"pages.nodeSettings.fields.embeddingModel.downloadError",
						"Could not start the recommended embedding model download.",
					),
				),
			),
	});

	const cancelDownload = (modelName: string): void => {
		cancel.mutate(modelName, {
			onSuccess: () => {
				removeInFlight(modelName);
				toast.success(t("pages.models.gguf.download.cancelled", "Download cancelled."));
			},
			onError: (error) =>
				toast.error(apiErrorMessage(error, t("pages.models.gguf.download.cancelError", "Could not cancel the download."))),
		});
	};

	return {
		downloadStatuses,
		progressNames,
		cancelDownload,
		cancellingModelName: cancel.isPending ? (cancel.variables ?? null) : null,
		reranker: { start: () => reranker.mutate({}), isPending: reranker.isPending, isInFlight: rerankerInFlight },
		embedding: { start: () => embedding.mutate({}), isPending: embedding.isPending, isInFlight: embeddingInFlight },
	};
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

	const modelOptions = useNodeSettingsModelOptions(fieldsForm, fieldErrors);

	const recommendedDownloads = useRecommendedModelDownloads();

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
		onSuccess: async (updatedSettings: SaveNodeSettingsResponse, variables: { body: SaveNodeSettingsRequest }) => {
			// Restart-gated fields (see restartGatedNodeSettingsFields) persist immediately but the running node keeps its
			// old value, so the save notice has to say so instead of implying the change is already live.
			toast.success(
				touchesRestartGatedField(variables.body)
					? t(
							"pages.nodeSettings.savedRestartRequired",
							"Node settings saved. Some of the changed settings only take effect after the node restarts.",
						)
					: t("pages.nodeSettings.saved", "Node settings saved. Capability reporting was requested for the worker connection."),
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
		if (modelOptions.keepWarmModelUnavailable) {
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
				toast.error(apiErrorMessage(error, t("pages.nodeSettings.hfToken.saveError", "Could not save the token."))),
		});
	};

	const handleClearToken = (): void => {
		setHfToken.mutate(undefined, {
			onSuccess: () => {
				clearTokenDraft();
				toast.success(t("pages.nodeSettings.hfToken.cleared", "Token cleared."));
			},
			onError: (error) =>
				toast.error(apiErrorMessage(error, t("pages.nodeSettings.hfToken.clearError", "Could not clear the token."))),
		});
	};

	return (
		<PageShell>
			<PageHeader
				title={t("pages.nodeSettings.title", "Node settings")}
				icon={<IconSettings size={24} />}
				subtitle={t("pages.nodeSettings.subtitle", "Tune non-secret local runtime settings stored on this worker.")}
			/>

			{settingsIsLoading ? (
				<Group gap="sm">
					<Loader size="sm" />
					<Text c="dimmed">{t("pages.nodeSettings.loading", "Loading node settings…")}</Text>
				</Group>
			) : null}

			{settingsError ? (
				<Alert color="red" icon={<IconAlertTriangle size={16} />}>
					{errorMessage(settingsError)}
				</Alert>
			) : null}

			<SectionCard title="Local chat runtime" icon={<IconSettings size={22} />}>
				<Text c="dimmed">
					The maximum message request timeout bounds how long a single local chat message request (send or regenerate) may run
					before it is cancelled with a timeout. It is also reported to the platform via capability reports.
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
			</SectionCard>

			<LlamaCppUpdaterPanel />

			<SourceBuildCard />

			<ImageRuntimeSourceBuildCard />

			<NodeSettingsFieldsCard
				form={fieldsForm}
				bounds={fieldBounds}
				errors={modelOptions.visibleErrors}
				onChange={handleFieldChange}
				showDeveloperFields={developerMode}
				draftModelOptions={modelOptions.draftModelOptions}
				keepWarmModelOptions={modelOptions.keepWarmModelOptions}
				autoEffortFastModelOptions={modelOptions.autoEffortFastModelOptions}
				rerankerModelOptions={modelOptions.rerankerModelOptions}
				onDownloadRecommendedReranker={recommendedDownloads.reranker.start}
				isDownloadRecommendedRerankerPending={recommendedDownloads.reranker.isPending}
				isRecommendedRerankerInFlight={recommendedDownloads.reranker.isInFlight}
				onDownloadRecommendedEmbedding={recommendedDownloads.embedding.start}
				isDownloadRecommendedEmbeddingPending={recommendedDownloads.embedding.isPending}
				isRecommendedEmbeddingInFlight={recommendedDownloads.embedding.isInFlight}
			/>

			<DownloadProgressPanel
				inFlight={recommendedDownloads.progressNames}
				downloadStatuses={recommendedDownloads.downloadStatuses}
				onCancel={recommendedDownloads.cancelDownload}
				cancellingModelName={recommendedDownloads.cancellingModelName}
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

			<NodeSettingsAuxiliaryPanels
				hasToken={hfTokenQuery.data ?? false}
				isTokenLoading={hfTokenQuery.isLoading}
				tokenDraft={tokenDraft}
				isSavingToken={setHfToken.isPending}
				onTokenDraftChange={setTokenDraft}
				onSaveToken={handleSaveToken}
				onClearToken={handleClearToken}
			/>
			<VoiceSettingsCard />
			<NodeSettingsDeveloperModePanel developerMode={developerMode} onToggleDeveloperMode={toggleDeveloperMode} />
		</PageShell>
	);
}
