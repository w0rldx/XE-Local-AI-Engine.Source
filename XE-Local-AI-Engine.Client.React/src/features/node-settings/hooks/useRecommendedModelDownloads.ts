import { useMutation } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	downloadRecommendedEmbeddingMutation,
	downloadRecommendedRerankerMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toast } from "@/core/ui/notifications/Toast";
import { useActiveGgufDownloads, useCancelGgufDownload } from "@/features/models/queries/useGgufDownload";
import { useGgufBrowseStore } from "@/features/models/stores/GgufBrowseStore";

/** One-click "download the recommended reranker / embedding model" actions and their shared progress feed. */
export function useRecommendedModelDownloads() {
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
