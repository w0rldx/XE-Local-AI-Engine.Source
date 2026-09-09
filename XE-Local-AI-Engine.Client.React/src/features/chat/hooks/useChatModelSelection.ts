import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useEffect, useMemo } from "react";

import { getLocalModelDetailsOptions, listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import type { ModelOption, ReasoningEffort } from "@/features/chat/models/ChatModels";
import { deriveModelDisplay, deriveModelIdDisplay } from "@/features/chat/models/ModelDisplay";
import { localDefaultModelValue, toNodeChatRequestModel } from "@/features/chat/models/NodeChatModelSelection";
import { resolveContextCapacityTokens, shouldFetchLocalModelDetails } from "@/features/chat/pages/ChatModelDetailsQuery";
import {
	hasNoLocalChatModels,
	resolveLocalDefaultModelCapabilities,
	resolveLocalDefaultModelName,
	toChatModelOptions,
} from "@/features/chat/pages/ChatModelOptions";
import { resolveAvailableReasoningEfforts } from "@/features/chat/pages/ChatReasoningEfforts";
import { useCodexModelOptions } from "@/features/chat/queries/useCodexModelOptions";
import { useExternalModelOptions } from "@/features/chat/queries/useExternalModelOptions";
import { clampReasoningEffort, useNodeChatPreferencesStore } from "@/features/chat/stores/NodeChatPreferencesStore";

// Base identity for the synthetic "Local default" composer option. Capabilities are filled in dynamically inside
// modelOptions (see below) from the concrete model the runtime will resolve, so picking "Local default" mirrors the
// reasoning/tool controls of picking that model directly. The false capabilities here are only the pre-load default
// (used until the local model list arrives).
const localDefaultModelOptionBase: ModelOption = {
	value: localDefaultModelValue,
	label: "Local default",
	displayName: "Local runtime default",
	isReasoningModel: false,
	isNativeReasoningModel: false,
	isToolCapable: false,
	isMultimodal: false,
	isAvailable: true,
	statusLabel: "Runtime-selected model",
};

interface ChatModelSelectionInput {
	// True in owner-embedded mode: the displayed model comes from the pinned agent, not the operator's store.
	isScoped: boolean;
	pinnedAgentId?: string;
	pinnedAgentModelProfile?: string | null;
	scopedModelPending: boolean;
}

interface ChatModelSelection {
	modelOptions: ModelOption[];
	cloudModelOptions: ModelOption[];
	selectedModel: string;
	reasoningEffort: ReasoningEffort;
	availableReasoningEfforts: ReasoningEffort[];
	activeModelToolCapable: boolean;
	activeModelMultimodal: boolean;
	showNoModelGuidance: boolean;
	effectiveMaxContextTokens?: number;
	contextModelLabel: string;
	setSelectedModel: (model: string) => void;
	setReasoningEffort: (effort: ReasoningEffort) => void;
}

/** The composer's model list, the active selection and everything derived from it (capabilities, context meter). */
export function useChatModelSelection({
	isScoped,
	pinnedAgentId,
	pinnedAgentModelProfile,
	scopedModelPending,
}: ChatModelSelectionInput): ChatModelSelection {
	const preferredModel = useNodeChatPreferencesStore((state) => state.selectedModel);
	const reasoningEffort = useNodeChatPreferencesStore((state) => state.reasoningEffort);
	const { setSelectedModel, setReasoningEffort } = useNodeChatPreferencesStore((state) => state.actions);
	const selectedModel = pinnedAgentId ? (pinnedAgentModelProfile ?? localDefaultModelValue) : preferredModel;

	const { data: localModelsData } = useQuery({
		...withResponseValidation(listLocalModelsOptions()),
		// Keep the prior model list while a refetch is in flight so a transient response that momentarily omits
		// the selected model can't trip the reconcile effect and reset selectedModel to the default (which would
		// undercut the persisted model selection restored from localStorage).
		placeholderData: keepPreviousData,
	});

	const modelOptions = useMemo<ModelOption[]>(() => {
		const response = localModelsData;
		if (!response) {
			return [localDefaultModelOptionBase];
		}

		// Mirror the resolved concrete model's capabilities onto the Local-default option so its reasoning/tool
		// controls match picking that model directly (see resolveLocalDefaultModelCapabilities).
		const items = response.items ?? [];
		const localDefaultModelOption: ModelOption = {
			...localDefaultModelOptionBase,
			...resolveLocalDefaultModelCapabilities(items),
		};
		return [localDefaultModelOption, ...toChatModelOptions(items, response.isAvailable ?? false)];
	}, [localModelsData]);
	// Cloud (Codex + Azure) model options — empty array when signed out; non-empty only when Codex session active.
	const codexModelOptions = useCodexModelOptions();
	// Models served by an operator-registered external OpenAI-compatible endpoint, one per registered model.
	const externalModelOptions = useExternalModelOptions();
	// Everything the node can send to that its own installed-model list will never contain. Send validation, the
	// stale-selection reconcile and the picker all treat cloud and external entries the same way, so they share one
	// list; only the picker's grouping tells them apart (external options carry their connection identity).
	const cloudModelOptions = useMemo(
		() => [...codexModelOptions, ...externalModelOptions],
		[codexModelOptions, externalModelOptions],
	);
	// Pre-empt the first-send ModelNotInstalled failure with inline guidance, instead of only surfacing it
	// after a failed send (ChatMessage's error Alert). Gated on BOTH no installed local chat model AND no signed-in
	// cloud provider — a Codex/Azure session is still a usable send path, so the guidance would be misleading there.
	// `localModelsData !== undefined` guards the pre-load default-only modelOptions shape (before the query
	// resolves) from being mistaken for a genuinely empty node.
	const showNoModelGuidance =
		localModelsData !== undefined && hasNoLocalChatModels(modelOptions) && cloudModelOptions.length === 0;
	const selectedModelOption = useMemo(
		() =>
			modelOptions.find((option) => option.value === selectedModel) ??
			cloudModelOptions.find((option) => option.value === selectedModel),
		[cloudModelOptions, modelOptions, selectedModel],
	);
	// Per-model capability gating: the tool controls gate on the model's `tools` capability (combined with the
	// node-wide gate inside ChatInputArea), image attachment on its vision projector.
	const activeModelToolCapable = selectedModelOption?.isToolCapable ?? false;
	const activeModelMultimodal = selectedModelOption?.isMultimodal ?? false;
	const selectedModelIsCloud = selectedModelOption?.isCloud === true;
	// Which effort vocabulary this model's provider actually honours — see ChatReasoningEfforts for the rule.
	const availableReasoningEfforts = useMemo<ReasoningEffort[]>(
		() => resolveAvailableReasoningEfforts(selectedModelOption),
		[selectedModelOption],
	);

	// Reconcile a persisted model selection against the live list: once the models query has resolved, a
	// stored model that no longer exists (renamed/removed on the node) falls back to the local default so the
	// composer never points at a phantom model. Guarded on loaded data so the initial default-only list
	// (before the query settles) does not clobber a still-valid persisted concrete model.
	// Also exempt cloud model selections — they are not in the local list and must not be evicted.
	useEffect(() => {
		if (!localModelsData) {
			return;
		}

		// Under scope the displayed model comes from the pinned agent, not the store — reconciling here would
		// rewrite the operator's own /chat preference from a session they merely opened.
		if (isScoped) {
			return;
		}

		const isCloudSelection = cloudModelOptions.some((option) => option.value === selectedModel);
		if (!isCloudSelection && !modelOptions.some((option) => option.value === selectedModel)) {
			setSelectedModel(localDefaultModelValue);
		}
	}, [cloudModelOptions, isScoped, localModelsData, modelOptions, selectedModel, setSelectedModel]);
	// Keep the selected reasoning effort valid for the active model's reasoning mode so the composer never SENDS an
	// effort the model can't honor. Graded models accept none/low/medium/high; binary models accept on/none; Codex
	// models add minimal/xhigh. When the current effort isn't in the active model's set (a Codex "xhigh" carried onto
	// a graded model, or a binary "on" carried onto a graded model) clampReasoningEffort maps to the nearest valid
	// level that PRESERVES reasoning intent — xhigh→high, minimal→low, graded→"on" for binary — instead of collapsing
	// reasoning OFF. Only "none" maps to "none". Runs on every model switch and on first load.
	useEffect(() => {
		const clamped = clampReasoningEffort(reasoningEffort, availableReasoningEfforts);
		if (clamped !== reasoningEffort) {
			setReasoningEffort(clamped);
		}
	}, [availableReasoningEfforts, reasoningEffort, setReasoningEffort]);
	const selectedConcreteModelName = useMemo(() => {
		const requestModel = toNodeChatRequestModel(selectedModel);
		// For the "Local default" sentinel, prefer the INSTALLED model the backend resolver will actually run
		// (resolveLocalDefaultModel mirror) over the store's selected/configured name — those may name a model whose
		// GGUF was never downloaded (configured-but-not-installed starter model), which permanently disabled the
		// model-details poll and left the context-usage meter capacity unknown ("N of —") even while an installed
		// model was serving chat fine. The store names stay as fallback for the no-installed-models case, where the
		// installed-list gate below keeps the details poll off anyway.
		return (
			requestModel ??
			resolveLocalDefaultModelName(localModelsData?.items ?? []) ??
			localModelsData?.selectedModelName ??
			localModelsData?.configuredDefaultModelName ??
			""
		);
	}, [localModelsData, selectedModel]);

	// Only poll model-details when the selection can actually return them: a non-empty local (non-cloud) name whose
	// list option, if known, is available AND whose concrete name is actually installed. Cloud (Codex) ids have no
	// LOCAL details (the endpoint 404s for them), an unavailable model just retries a guaranteed failure, and a
	// configured-but-not-installed default (its GGUF never downloaded) 404s forever until the install lands. GGUF
	// (llamacpp) selections that are installed ARE polled — the details endpoint answers with a 200 carrying
	// maxContextTokens, which the context meter needs.
	const concreteModelInstalled = useMemo(
		() => selectedConcreteModelName.length > 0 && modelOptions.some((option) => option.value === selectedConcreteModelName),
		[modelOptions, selectedConcreteModelName],
	);
	const selectedModelDetailsEnabled =
		// Don't poll for a name we are about to replace once the pinned agent lands.
		!scopedModelPending &&
		shouldFetchLocalModelDetails(selectedConcreteModelName, selectedModelOption, selectedModelIsCloud, concreteModelInstalled);
	const { data: selectedModelDetails } = useQuery({
		...withResponseValidation(getLocalModelDetailsOptions({ path: { modelName: selectedConcreteModelName } })),
		enabled: selectedModelDetailsEnabled,
	});

	// Prefer the RUNNING process's effective context window (the launched -c) over the model's advertised
	// train ceiling, so the meter shows the real capacity once the model is warm; fall back to the ceiling, then unknown.
	const effectiveMaxContextTokens = resolveContextCapacityTokens(selectedModelDetails);
	// The meter names the model the way the picker's trigger does — the same shortening, so the two controls sitting a
	// few pixels apart cannot spell the same selection differently. The "Local default" sentinel keeps naming the
	// CONCRETE model the resolver picked, which has no option of its own and so goes through the raw-id shortener.
	const contextModelLabel = useMemo(() => {
		if (selectedModelOption !== undefined && selectedModelOption.value !== localDefaultModelValue) {
			return deriveModelDisplay(selectedModelOption, selectedConcreteModelName).primary;
		}

		return selectedConcreteModelName.length > 0
			? deriveModelIdDisplay(selectedConcreteModelName).primary
			: "Local runtime default";
	}, [selectedConcreteModelName, selectedModelOption]);

	return {
		modelOptions,
		cloudModelOptions,
		selectedModel,
		reasoningEffort,
		availableReasoningEfforts,
		activeModelToolCapable,
		activeModelMultimodal,
		showNoModelGuidance,
		effectiveMaxContextTokens,
		contextModelLabel,
		setSelectedModel,
		setReasoningEffort,
	};
}
