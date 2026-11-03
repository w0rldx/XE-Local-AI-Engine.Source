import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { toChatModelOptions, toExternalModelOptions } from "@/core/models/ChatModelOptions";
import { LLAMACPP_PROVIDER } from "@/core/models/LocalModelProviders";

// The providers the unified models list tags cloud entries with (the chat picker reads the same two).
const cloudProviders = new Set(["CodexOAuth", "AzureFoundry"]);

interface ModelGroup {
	group: string;
	items: { value: string; label: string }[];
}

export function useDirectInferenceModels(): {
	modelGroups: ModelGroup[];
	llamaCppModels: Set<string>;
	status: "pending" | "error" | "success";
} {
	const { t } = useTranslation();
	const { data, status } = useQuery({ ...listLocalModelsOptions(), staleTime: 30_000 });
	return useMemo(() => {
		const items = data?.items ?? [];
		// Availability is not filtered here: the server refuses a model it cannot run with a classified error event.
		const toItems = (options: { value: string; label: string; displayName?: string }[]) =>
			options.map((option) => ({ value: option.value, label: option.displayName ?? option.label }));
		const localOptions = toChatModelOptions(items, data?.isAvailable ?? false);
		const groups: ModelGroup[] = [
			{
				group: t("pages.directInference.modelGroups.local", "Local"),
				items: toItems(localOptions),
			},
			{ group: t("pages.directInference.modelGroups.external", "External"), items: toItems(toExternalModelOptions(items, true)) },
			{
				group: t("pages.directInference.modelGroups.cloud", "Cloud"),
				items: items
					.filter((item) => cloudProviders.has(item.provider ?? "") && (item.modelName ?? "").length > 0)
					.map((item) => ({ value: item.modelName ?? "", label: item.displayLabel ?? item.modelName ?? "" })),
			},
		];
		return {
			status,
			modelGroups: groups.filter((group) => group.items.length > 0),
			llamaCppModels: new Set(
				localOptions.filter((option) => option.provider === LLAMACPP_PROVIDER).map((option) => option.value),
			),
		};
	}, [data, status, t]);
}
