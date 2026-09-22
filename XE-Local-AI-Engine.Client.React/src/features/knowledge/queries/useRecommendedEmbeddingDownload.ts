import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { downloadRecommendedEmbeddingMutation, listLocalModelsQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toast } from "@/core/ui/notifications/Toast";
import { knowledgeInvalidationKey, knowledgeQueryIds } from "@/features/knowledge/queries/useKnowledgeDocuments";

/**
 * Starts the one-click recommended-embedding-model download from the knowledge base, the same endpoint the Node
 * Settings knowledge card drives. It deliberately does NOT reuse that card's hook: `useRecommendedModelDownloads`
 * lives in the node-settings feature and reaches into the models feature for its progress feed, so importing it
 * from here is a cross-feature edge the architecture gate rejects. The shared part — the endpoint and its toasts —
 * is small enough to state twice; the progress feed is not needed here at all, because the acquisition that
 * finishes invalidates the installed-model list from `GgufDownloadPoller`, mounted once in `App.tsx` for every
 * route. That invalidation is what makes the "no embedding model" alert disappear on its own.
 *
 * The response invalidates the installed-model list AND the knowledge document list, so an `alreadyInstalled` answer
 * (the model landed while the page was open) clears the alert at once. A download that actually runs clears it when
 * the document list next reports the model resolved — that list self-polls while the gate is closed.
 */
export function useRecommendedEmbeddingDownload() {
	const { t } = useTranslation();
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(downloadRecommendedEmbeddingMutation()),
		onSuccess: async (response) => {
			await Promise.all([
				queryClient.invalidateQueries({ queryKey: listLocalModelsQueryKey() }),
				queryClient.invalidateQueries({ queryKey: knowledgeInvalidationKey(knowledgeQueryIds.listDocuments) }),
			]);
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
}
