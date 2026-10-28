import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { listGraphWorkflowDefinitionsOptions, listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { toChatModelOptions, toDraftModelOptions } from "@/core/models/ChatModelOptions";
import type { NodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";

/**
 * The installed-model pickers on the settings form, the intent-router workflow picker, plus the keep-warm "model is
 * gone" visible-error overlay.
 */
export function useNodeSettingsModelOptions(form: NodeSettingsFieldsForm, errors: Readonly<Record<string, string>>) {
	const { t } = useTranslation();
	const { data: localModels } = useQuery(listLocalModelsOptions());
	// A node with graph workflows off answers an error here; the picker then offers "none" and any stored id only.
	const { data: graphDefinitions } = useQuery(listGraphWorkflowDefinitionsOptions());
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
	// Every installed node-local chat model, llama.cpp and Ollama alike; cloud and external entries are excluded.
	const backgroundModelOptions = useMemo(
		() =>
			toChatModelOptions(localModels?.items ?? [], localModels?.isAvailable ?? false).map((option) => ({
				value: option.value,
				label: option.label,
			})),
		[localModels],
	);
	const rerankerModelOptions = useMemo(
		() =>
			(localModels?.items ?? [])
				.map((model) => ({ value: model.modelName ?? "", label: model.modelName ?? "" }))
				.filter((option) => option.value.length > 0),
		[localModels],
	);

	// The embedding pin takes only installed embedding models; the backend refuses anything else at save.
	const embeddingModelOptions = useMemo(
		() =>
			(localModels?.items ?? [])
				.filter((model) => model.kind === "Embedding")
				.map((model) => ({ value: model.modelName ?? "", label: model.modelName ?? "" }))
				.filter((option) => option.value.length > 0),
		[localModels],
	);

	const selectedRouterId = form.intentRouterDefinitionId;
	const intentRouterOptions = useMemo(() => {
		const options = (graphDefinitions?.definitions ?? [])
			.map((definition) => ({ value: definition.id, label: definition.name }))
			.sort((left, right) => left.label.localeCompare(right.label));
		// A stored router whose definition is gone (or not yet loaded) stays selectable under its id, never silently blank.
		return selectedRouterId.length > 0 && !options.some((option) => option.value === selectedRouterId)
			? [...options, { value: selectedRouterId, label: selectedRouterId }]
			: options;
	}, [graphDefinitions, selectedRouterId]);

	return {
		draftModelOptions,
		intentRouterOptions,
		keepWarmModelOptions,
		// The fast model for automatic reasoning effort takes exactly the keep-warm filter: an installed llama.cpp chat
		// model, never a cloud id, an external id or an Ollama name. The backend refuses anything else at save.
		autoEffortFastModelOptions: installedKeepWarmModelOptions,
		rerankerModelOptions,
		embeddingModelOptions,
		backgroundModelOptions,
		keepWarmModelUnavailable,
		visibleErrors: keepWarmModelUnavailable ? { ...errors, keepModelWarmModelName: "unavailableKeepWarmModel" } : errors,
	};
}
