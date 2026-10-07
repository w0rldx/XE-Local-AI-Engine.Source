import { Box, Grid, Group, Loader, NumberInput, Stack, Text } from "@mantine/core";
import { IconMessage, IconSettings } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { type ReactNode, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import type {
	XeLocalAiEngineClientEndpointsNodeSettingsV1NodeSettingsResponse as NodeSettingsResponse,
	XeLocalAiEngineClientEndpointsNodeSettingsV1SaveNodeSettingsRequest as SaveNodeSettingsRequest,
	SaveNodeSettingsResponse,
} from "@/core/api/generated";
import {
	getDefaultAssistantToolOfferQueryKey,
	getDevelopmentCapabilityQueryKey,
	getDevWorkflowCapabilityQueryKey,
	getGraphWorkflowCapabilityQueryKey,
	getNodeSettingsOptions,
	getNodeSettingsQueryKey,
	getToolCatalogQueryKey,
	getTranscriptionRuntimeStatusQueryKey,
	getWorkSessionCapabilityQueryKey,
	saveNodeSettingsMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { useUiMode } from "@/core/layout/hooks/useUiMode";
import { useEffectiveToolCapableModels } from "@/features/node-settings/queries/useEffectiveToolCapableModels";
import { useOllamaRuntimeConfigured } from "@/features/node-settings/queries/useOllamaRuntimeConfigured";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { DownloadProgressPanel } from "@/features/models/components/DownloadProgressPanel";
import { HfTokenPanel } from "@/features/node-settings/components/HfTokenPanel";
import { ImageRuntimeSourceBuildCard } from "@/features/node-settings/components/ImageRuntimeSourceBuildCard";
import { LlamaCppUpdaterPanel } from "@/features/node-settings/components/LlamaCppUpdaterPanel";
import { ManagedPythonCard } from "@/features/node-settings/components/ManagedPythonCard";
import { NodeChangePasswordCard } from "@/features/node-settings/components/NodeChangePasswordCard";
import {
	NodeSettingsBrowserPreferencesCard,
	NodeSettingsIntegrationPanels,
} from "@/features/node-settings/components/NodeSettingsAuxiliaryPanels";
import { NodeSettingsBrowserOnlyBadge } from "@/features/node-settings/components/NodeSettingsBrowserOnlyBadge";
import { NodeSettingsFieldsCard } from "@/features/node-settings/components/NodeSettingsFieldsCard";
import { NodeSettingsSaveBar } from "@/features/node-settings/components/NodeSettingsSaveBar";
import { NodeSettingsSectionNav } from "@/features/node-settings/components/NodeSettingsSectionNav";
import { SourceBuildCard } from "@/features/node-settings/components/SourceBuildCard";
import { WhisperRuntimeSourceBuildCard } from "@/features/node-settings/components/WhisperRuntimeSourceBuildCard";
import { useNodeSettingsModelOptions } from "@/features/node-settings/hooks/useNodeSettingsModelOptions";
import { useRecommendedModelDownloads } from "@/features/node-settings/hooks/useRecommendedModelDownloads";
import {
	applyExternalAccessPreset,
	buildNodeSettingsRequest,
	type ExternalAccessPreset,
	featureSwitchFields,
	isExternalAccessBooleanField,
	type NodeSettingsFieldsForm,
	summarizePendingChanges,
	toNodeSettingsFieldBounds,
	toNodeSettingsFieldsForm,
	touchesRestartGatedField,
	turnsOnExecutionPreviews,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";
import {
	type NodeSettingsTimeoutInput,
	nodeSettingsDefaults,
	toValidNodeSettingsTimeoutSeconds,
} from "@/features/node-settings/models/NodeSettingsModel";
import {
	defaultNodeSettingsSection,
	isAdvancedNodeSettingsSection,
	type NodeSettingsSectionId,
	nodeSettingsSectionIds,
	nodeSettingsSectionOf,
} from "@/features/node-settings/models/NodeSettingsSections";
import { useHfTokenStatus, useSetHfToken } from "@/features/node-settings/queries/useLocalRuntime";
import { useHfTokenStore } from "@/features/node-settings/stores/HfTokenStore";
import { VoiceSettingsCard } from "@/features/voice/components/VoiceSettingsCard";

function errorMessage(error: unknown): string {
	return apiErrorMessage(error, "Unexpected node settings error");
}

interface NodeSettingsProps {
	// The active section, from the route's `?section=` search param. Absent means the page keeps its own selection,
	// starting at General.
	readonly section?: NodeSettingsSectionId;
	readonly onSectionChange?: (section: NodeSettingsSectionId) => void;
	// The application update-channel picker. It lives in another feature, so the route supplies it.
	readonly updateChannelSelector?: ReactNode;
}

export function NodeSettings({ section, onSectionChange, updateChannelSelector }: NodeSettingsProps) {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const { confirm } = useConfirm();
	const {
		data: settings,
		isLoading: settingsIsLoading,
		error: settingsError,
		refetch: settingsRefetch,
		isFetching: settingsIsFetching,
	} = useQuery(withResponseValidation(getNodeSettingsOptions()));
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const { toggle: toggleDeveloperMode } = useDeveloperModeStore((state) => state.actions);
	const savedUiMode = useUiMode();
	const [timeoutSeconds, setTimeoutSeconds] = useState<NodeSettingsTimeoutInput>(
		nodeSettingsDefaults.maxMessageRequestTimeoutSeconds,
	);

	const [ownSection, setOwnSection] = useState<NodeSettingsSectionId>(defaultNodeSettingsSection);
	const activeSection = section ?? ownSection;
	const selectSection = onSectionChange ?? setOwnSection;
	const [showAdvancedSections, setShowAdvancedSections] = useState(false);

	// The whole page is ONE draft. `fieldsForm` is the editable draft; `fieldsBaseline` is the last-loaded authoritative
	// state — only fields that differ from the baseline are sent on save (optional-request semantics).
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
	const effectiveToolCapableModels = useEffectiveToolCapableModels();

	// Replaces the draft (and the save baseline) with a server state. Every deliberate "take the server's values" path
	// goes through here: the first load, Reset, and a successful save.
	const seedDraft = (loaded: NodeSettingsResponse | SaveNodeSettingsResponse): void => {
		const form = toNodeSettingsFieldsForm(loaded);
		setFieldsForm(form);
		setFieldsBaseline(form);
		setSeededSource(loaded);
		setIsDirty(false);
		setPendingPreset(null);
		setTimeoutSeconds(loaded.maxMessageRequestTimeoutSeconds ?? nodeSettingsDefaults.maxMessageRequestTimeoutSeconds);
	};

	// Adopt every newer server state while the draft is PRISTINE. Seeding only once was wrong in both directions: the
	// page can mount against a cached response, seed from it, and then ignore the mount refetch's fresher values — a
	// Save would submit the stale ones straight back over the newer server state. Once the operator has typed, adoption
	// stops, because a background refetch (window focus, the post-save invalidation) must never discard their edits.
	// Reset re-seeds explicitly below and Save re-seeds from its own response, so both still take the server's values.
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
	const timeoutBaseline = seededSource?.maxMessageRequestTimeoutSeconds ?? nodeSettingsDefaults.maxMessageRequestTimeoutSeconds;

	// The save body and the save bar's counts come from the same diff, so the bar never promises a change Save skips.
	// Developer-only fields are included ONLY when developer mode is on (off-mode their cards are unmounted).
	const draftRequest = useMemo(
		() =>
			buildNodeSettingsRequest(fieldsForm, fieldsBaseline, fieldBounds, developerMode, pendingPreset, effectiveToolCapableModels),
		[fieldsForm, fieldsBaseline, fieldBounds, developerMode, pendingPreset, effectiveToolCapableModels],
	);
	const pendingChanges = summarizePendingChanges(draftRequest, fieldsForm, fieldsBaseline);
	const timeoutChanged = timeoutSeconds !== timeoutBaseline;
	const changedFields: string[] = [...pendingChanges.changed, ...(timeoutChanged ? ["maxMessageRequestTimeoutSeconds"] : [])];
	const dirtyCounts = new Map<NodeSettingsSectionId, number>();
	for (const field of changedFields) {
		const fieldSection = nodeSettingsSectionOf(field);
		if (fieldSection !== undefined) {
			dirtyCounts.set(fieldSection, (dirtyCounts.get(fieldSection) ?? 0) + 1);
		}
	}

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
			seedDraft(updatedSettings);
			setFieldErrors({});
			// Seeding the shared query is also what re-renders the navigation rail after a uiMode change, and what the
			// voice runtime reads its gate from.
			queryClient.setQueryData(getNodeSettingsQueryKey(), updatedSettings);
			await queryClient.invalidateQueries({ queryKey: getNodeSettingsQueryKey() });
			// The Default Assistant's tool offer and the tool catalog are computed from the live switches (web access,
			// tools, custom tools); a cached pre-save answer would show the old offer until it went stale. Every model
			// variant of the offer is matched by its operation id.
			await Promise.all(
				[getDefaultAssistantToolOfferQueryKey(), getToolCatalogQueryKey()].map(([{ _id }]) =>
					queryClient.invalidateQueries({ queryKey: [{ _id }] }),
				),
			);
			// The capability probes the nav and the gated pages read answer from the live switches; a cached answer from
			// before the save would keep a re-enabled feature hidden (or a disabled one offered) until it went stale.
			if (featureSwitchFields.some((field) => field in variables.body)) {
				await Promise.all(
					[
						getDevelopmentCapabilityQueryKey(),
						getDevWorkflowCapabilityQueryKey(),
						getGraphWorkflowCapabilityQueryKey(),
						getWorkSessionCapabilityQueryKey(),
						getTranscriptionRuntimeStatusQueryKey(),
					].map((queryKey) => queryClient.invalidateQueries({ queryKey })),
				);
			}
		},
		onError: (error) => toast.error(errorMessage(error)),
	});

	// A validation failure can sit in a section the operator is not looking at; take them to the first one.
	const showErrors = (errors: Readonly<Record<string, string>>): void => {
		setFieldErrors(errors);
		toast.error(t("pages.nodeSettings.fields.validationError", "Some settings are invalid. Fix the highlighted fields."));
		const firstSection = Object.keys(errors)
			.map(nodeSettingsSectionOf)
			.find((candidate) => candidate !== undefined);
		if (firstSection !== undefined && firstSection !== activeSection) {
			selectSection(firstSection);
		}
	};

	const handleSave = async (): Promise<void> => {
		if (timeoutToSave === undefined) {
			selectSection("chat");
			return;
		}
		if (modelOptions.keepWarmModelUnavailable) {
			showErrors({ ...fieldErrors, keepModelWarmModelName: "unavailableKeepWarmModel" });
			return;
		}
		if (Object.keys(draftRequest.errors).length > 0) {
			showErrors(draftRequest.errors);
			return;
		}
		// Turning execution previews on widens what a sandboxed workload may run under, so the operator confirms it knowing the
		// residual risk; declining keeps the whole draft so nothing else they edited is lost.
		if (
			turnsOnExecutionPreviews(draftRequest.body) &&
			!(await confirm({
				title: t("pages.nodeSettings.executionPreviewsConfirm.title", "Turn on execution previews?"),
				description: t(
					"pages.nodeSettings.executionPreviewsConfirm.description",
					"Preview sandbox mechanisms will run Python and sandboxed MCP servers. On Windows that is the AppContainer boundary: the code cannot read your user profile or the engine's data, but it runs with no CPU, memory or process-count limit (only the time limit stops it), and it can see host path names. Turn this on only if you accept that.",
				),
				confirmationText: t("pages.nodeSettings.executionPreviewsConfirm.confirm", "Turn on"),
				cancellationText: t("pages.nodeSettings.executionPreviewsConfirm.cancel", "Cancel"),
			}))
		) {
			return;
		}
		setFieldErrors({});
		saveMutation.mutate({ body: { ...draftRequest.body, maxMessageRequestTimeoutSeconds: timeoutToSave } });
	};

	// Reset is the operator asking for the server's values, so it is the one refetch that DOES replace the draft. It
	// seeds from the refetch's own result rather than clearing the seeded flag, which would re-seed from the stale
	// cached state one render before the fresh response arrives.
	const handleReset = async (): Promise<void> => {
		const reloaded = await settingsRefetch();
		if (reloaded.data !== undefined) {
			seedDraft(reloaded.data);
			setFieldErrors({});
		}
	};

	// HF token: the draft lives in a store so it survives a remount; the token itself is write-only (never read back
	// into the draft). It saves on its own endpoint, outside the save bar.
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

	// Simple UI mode lists the everyday sections; the advanced ones sit behind a toggle but stay reachable by URL, so a
	// linked advanced section is listed even while the toggle is off.
	const isSimpleMode = savedUiMode === "simple";
	const visibleSections = nodeSettingsSectionIds.filter(
		(candidate) =>
			!isSimpleMode || showAdvancedSections || candidate === activeSection || !isAdvancedNodeSettingsSection(candidate),
	);

	const fields = (
		<NodeSettingsFieldsCard
			section={activeSection}
			form={fieldsForm}
			bounds={fieldBounds}
			errors={modelOptions.visibleErrors}
			onChange={handleFieldChange}
			onApplyPreset={handleApplyExternalAccessPreset}
			showDeveloperFields={developerMode}
			draftModelOptions={modelOptions.draftModelOptions}
			keepWarmModelOptions={modelOptions.keepWarmModelOptions}
			autoEffortFastModelOptions={modelOptions.autoEffortFastModelOptions}
			backgroundModelOptions={modelOptions.backgroundModelOptions}
			rerankerModelOptions={modelOptions.rerankerModelOptions}
			onDownloadRecommendedReranker={recommendedDownloads.reranker.start}
			isDownloadRecommendedRerankerPending={recommendedDownloads.reranker.isPending}
			isRecommendedRerankerInFlight={recommendedDownloads.reranker.isInFlight}
			onDownloadRecommendedEmbedding={recommendedDownloads.embedding.start}
			isDownloadRecommendedEmbeddingPending={recommendedDownloads.embedding.isPending}
			isRecommendedEmbeddingInFlight={recommendedDownloads.embedding.isInFlight}
			ollamaRuntimeDisabled={ollamaRuntimeDisabled}
			updateChannelSelector={updateChannelSelector}
		/>
	);

	const renderSection = (): ReactNode => {
		switch (activeSection) {
			case "general":
				return (
					<>
						{fields}
						<NodeSettingsBrowserPreferencesCard developerMode={developerMode} onToggleDeveloperMode={toggleDeveloperMode} />
						<NodeChangePasswordCard />
					</>
				);
			case "chat":
				return (
					<>
						<SectionCard
							title={t("pages.nodeSettings.localChatRuntime.title", "Local chat runtime")}
							icon={<IconMessage size={22} />}
						>
							<Text c="dimmed">
								{t(
									"pages.nodeSettings.localChatRuntime.description",
									"The maximum message request timeout bounds how long a single local chat message request (send or regenerate) may run before it is cancelled with a timeout. It is also reported to the platform via capability reports.",
								)}
							</Text>
							<NumberInput
								label={t("pages.nodeSettings.localChatRuntime.timeoutLabel", "Maximum message request timeout")}
								description={`${t("pages.nodeSettings.localChatRuntime.timeoutRange", "Allowed range: {{min}}–{{max}} seconds.", {
									min: minTimeout,
									max: maxTimeout,
								})} ${t(
									"pages.nodeSettings.localChatRuntime.outputCapHint",
									"On slow hardware a long answer can reach this timeout before the answer length limit (Longest answer) stops it. Raise this timeout, or lower Longest answer.",
								)}`}
								suffix={` ${t("pages.nodeSettings.fields.seconds", "seconds")}`}
								min={minTimeout}
								max={maxTimeout}
								step={5}
								allowDecimal={false}
								value={timeoutSeconds}
								onChange={handleTimeoutChange}
								error={
									timeoutToSave === undefined
										? t("pages.nodeSettings.localChatRuntime.timeoutError", "Enter a whole number from {{min}} to {{max}}.", {
												min: minTimeout,
												max: maxTimeout,
											})
										: undefined
								}
							/>
						</SectionCard>
						{fields}
					</>
				);
			case "runtimes":
				return (
					<>
						<LlamaCppUpdaterPanel />
						<SourceBuildCard />
						<ImageRuntimeSourceBuildCard />
						<WhisperRuntimeSourceBuildCard />
						<ManagedPythonCard />
						{fields}
					</>
				);
			case "models":
				return (
					<>
						{fields}
						<HfTokenPanel
							hasToken={hfTokenQuery.data ?? false}
							isLoading={hfTokenQuery.isLoading}
							tokenDraft={tokenDraft}
							onTokenDraftChange={setTokenDraft}
							onSave={handleSaveToken}
							onClear={handleClearToken}
							isSaving={setHfToken.isPending}
						/>
					</>
				);
			case "knowledge":
				return (
					<>
						{fields}
						<DownloadProgressPanel
							inFlight={recommendedDownloads.progressNames}
							downloadStatuses={recommendedDownloads.downloadStatuses}
							onCancel={recommendedDownloads.cancelDownload}
							cancellingModelName={recommendedDownloads.cancellingModelName}
						/>
					</>
				);
			case "voice":
				return (
					<>
						<VoiceSettingsCard
							voiceFeatureEnabled={fieldsForm.voiceFeatureEnabled}
							defaultVoiceProfile={fieldsForm.defaultVoiceProfile}
							onVoiceFeatureEnabledChange={(enabled) => handleFieldChange("voiceFeatureEnabled", enabled)}
							onDefaultVoiceProfileChange={(profile) => handleFieldChange("defaultVoiceProfile", profile)}
							browserOnlyBadge={<NodeSettingsBrowserOnlyBadge />}
						/>
						{fields}
					</>
				);
			case "integrations":
				return <NodeSettingsIntegrationPanels />;
			default:
				return fields;
		}
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

			<Grid gap="lg">
				<Grid.Col span={{ base: 12, sm: 4, md: 3 }}>
					<Box pos={{ base: "static", sm: "sticky" }} top={0}>
						<NodeSettingsSectionNav
							sections={visibleSections}
							active={activeSection}
							onSelect={selectSection}
							dirtyCounts={dirtyCounts}
							advancedToggle={
								isSimpleMode
									? { shown: showAdvancedSections, onToggle: () => setShowAdvancedSections((shown) => !shown) }
									: undefined
							}
						/>
					</Box>
				</Grid.Col>
				<Grid.Col span={{ base: 12, sm: 8, md: 9 }}>
					<Stack gap="lg" data-testid={`node-settings-section-content-${activeSection}`}>
						{renderSection()}
					</Stack>
				</Grid.Col>
			</Grid>

			<NodeSettingsSaveBar
				unsavedCount={changedFields.length}
				restartCount={pendingChanges.restartRequired.length}
				canSave={changedFields.length > 0 && !saveMutation.isPending}
				canReset={isDirty && !settingsIsFetching}
				isSaving={saveMutation.isPending}
				onSave={handleSave}
				onReset={handleReset}
			/>
		</PageShell>
	);
}
