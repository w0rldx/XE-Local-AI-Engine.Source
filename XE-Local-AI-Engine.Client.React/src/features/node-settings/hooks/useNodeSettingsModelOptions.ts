import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toChatModelOptions, toDraftModelOptions } from "@/features/chat/pages/ChatModelOptions";
import type { NodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";

/** The installed-model pickers on the settings form, plus the keep-warm "model is gone" visible-error overlay. */
export function useNodeSettingsModelOptions(form: NodeSettingsFieldsForm, errors: Readonly<Record<string, string>>) {
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
