import { Button, Group, Loader, NumberInput, Text } from "@mantine/core";
import { IconDeviceFloppy, IconRefresh, IconSettings } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import type {
	XeLocalAiEngineClientEndpointsNodeSettingsV1NodeSettingsResponse as NodeSettingsResponse,
	XeLocalAiEngineClientEndpointsNodeSettingsV1SaveNodeSettingsRequest as SaveNodeSettingsRequest,
	SaveNodeSettingsResponse,
} from "@/core/api/generated";
import {
	getNodeSettingsOptions,
	getNodeSettingsQueryKey,
	saveNodeSettingsMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { useOllamaRuntimeConfigured } from "@/core/runtime/hooks/useOllamaRuntimeConfigured";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { toast } from "@/core/ui/notifications/Toast";
import { DownloadProgressPanel } from "@/features/models/components/DownloadProgressPanel";
import { ImageRuntimeSourceBuildCard } from "@/features/node-settings/components/ImageRuntimeSourceBuildCard";
import { LlamaCppUpdaterPanel } from "@/features/node-settings/components/LlamaCppUpdaterPanel";
import {
	NodeSettingsAuxiliaryPanels,
	NodeSettingsDeveloperModePanel,
} from "@/features/node-settings/components/NodeSettingsAuxiliaryPanels";
import { NodeSettingsFieldsCard } from "@/features/node-settings/components/NodeSettingsFieldsCard";
import { SourceBuildCard } from "@/features/node-settings/components/SourceBuildCard";
import { useNodeSettingsModelOptions } from "@/features/node-settings/hooks/useNodeSettingsModelOptions";
import { useRecommendedModelDownloads } from "@/features/node-settings/hooks/useRecommendedModelDownloads";
import {
	applyExternalAccessPreset,
	buildNodeSettingsRequest,
	type ExternalAccessPreset,
	isExternalAccessBooleanField,
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

	// The migrated appsettings knobs. `fieldsForm` is the editable draft; `fieldsBaseline` is the last-loaded
	// authoritative state — only fields that differ from the baseline are sent on save (optional-request semantics).
	const [fieldsForm, setFieldsForm] = useState<NodeSettingsFieldsForm>(() => toNodeSettingsFieldsForm(undefined));
	const [fieldsBaseline, setFieldsBaseline] = useState<NodeSettingsFieldsForm>(() => toNodeSettingsFieldsForm(undefined));
	// The server state the draft was last seeded from, and whether the operator has since touched anything. Together
	// they decide whether a newly arrived `settings` may be adopted.
	const [seededSource, setSeededSource] = useState<NodeSettingsResponse | SaveNodeSettingsResponse>();
	const [isDirty, setIsDirty] = useState(false);
	// The profile the operator just picked and has not since overridden by hand. UI-only, deliberately NOT a form field:
	// keeping it out of the draft is what makes "a save carries the profile OR the switches, never both" expressible.
	const [pendingPreset, setPendingPreset] = useState<ExternalAccessPreset | null>(null);
	const [fieldErrors, setFieldErrors] = useState<Readonly<Record<string, string>>>({});
	const fieldBounds = useMemo(() => toNodeSettingsFieldBounds(settings), [settings]);

	const modelOptions = useNodeSettingsModelOptions(fieldsForm, fieldErrors);

	const recommendedDownloads = useRecommendedModelDownloads();

	// Whether the optional Ollama runtime is gated off on this node (XE_OLLAMA_RUNTIME_ENABLED=false). FAIL OPEN: only
	// a definite `false` disables the endpoint field, so a still-loading or failed probe leaves the field in place.
	const ollamaRuntimeDisabled = useOllamaRuntimeConfigured().data === false;

	// Replaces the draft (and the save baseline) with a server state. Every deliberate "take the server's values" path
	// goes through here: the first load, an explicit Reload, and a successful save.
	const seedDraft = (loaded: NodeSettingsResponse | SaveNodeSettingsResponse): void => {
		const form = toNodeSettingsFieldsForm(loaded);
		setFieldsForm(form);
		setFieldsBaseline(form);
		setSeededSource(loaded);
		setIsDirty(false);
		setPendingPreset(null);
		if (loaded.maxMessageRequestTimeoutSeconds !== undefined) {
			setTimeoutSeconds(loaded.maxMessageRequestTimeoutSeconds);
		}
	};

	// Adopt every newer server state while the draft is PRISTINE. Seeding only once was wrong in both directions: the
	// page can mount against a cached response, seed from it, and then ignore the mount refetch's fresher values — a
	// Save would submit the stale ones straight back over the newer server state. Once the operator has typed, adoption
	// stops, because a background refetch (window focus, the post-save invalidation) must never discard their edits.
	// Reload re-seeds explicitly below and Save re-seeds from its own response, so both still take the server's values.
	// Adjusted during render rather than in an effect so the values are on the paint, not one paint later.
	if (settings !== undefined && settings !== seededSource && !isDirty) {
		seedDraft(settings);
	}

	const minTimeout = settings?.minMessageRequestTimeoutSeconds ?? nodeSettingsDefaults.minMessageRequestTimeoutSeconds;
	const maxTimeout =
		settings?.maxAllowedMessageRequestTimeoutSeconds ?? nodeSettingsDefaults.maxAllowedMessageRequestTimeoutSeconds;
	const timeoutToSave = useMemo(
		() => toValidNodeSettingsTimeoutSeconds(timeoutSeconds, minTimeout, maxTimeout),
		[maxTimeout, minTimeout, timeoutSeconds],
	);

	const handleFieldChange = <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]): void => {
		setIsDirty(true);
		// A switch edited by hand after a preset click wins: the save then carries the booleans and the server stamps the
		// profile "custom", instead of honouring the preset and discarding the edit.
		if (isExternalAccessBooleanField(field)) {
			setPendingPreset(null);
		}
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

	const handleApplyExternalAccessPreset = (preset: ExternalAccessPreset): void => {
		setIsDirty(true);
		setFieldsForm((current) => applyExternalAccessPreset(current, preset));
		setPendingPreset(preset);
	};

	const handleTimeoutChange = (value: NodeSettingsTimeoutInput): void => {
		setIsDirty(true);
		setTimeoutSeconds(value);
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
			seedDraft(updatedSettings);
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
		const { body, errors } = buildNodeSettingsRequest(fieldsForm, fieldsBaseline, fieldBounds, developerMode, pendingPreset);
		if (Object.keys(errors).length > 0) {
			setFieldErrors(errors);
			toast.error(t("pages.nodeSettings.fields.validationError", "Some settings are invalid. Fix the highlighted fields."));
			return;
		}
		setFieldErrors({});
		saveMutation.mutate({ body: { ...body, maxMessageRequestTimeoutSeconds: timeoutToSave } });
	};

	// Reload is the operator asking for the server's values, so it is the one refetch that DOES replace the draft. It
	// seeds from the refetch's own result rather than clearing the seeded flag, which would re-seed from the stale
	// cached state one render before the fresh response arrives.
	const handleReload = async (): Promise<void> => {
		const reloaded = await settingsRefetch();
		if (reloaded.data !== undefined) {
			seedDraft(reloaded.data);
			setFieldErrors({});
		}
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

			{settingsError ? <InlineErrorAlert message={errorMessage(settingsError)} /> : null}

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
					onChange={handleTimeoutChange}
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
					<Button variant="subtle" leftSection={<IconRefresh size={16} />} onClick={handleReload} disabled={settingsIsFetching}>
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
				onApplyPreset={handleApplyExternalAccessPreset}
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
				ollamaRuntimeDisabled={ollamaRuntimeDisabled}
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
