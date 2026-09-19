import { Alert, Button, Divider, Group, Loader, Stack, Text } from "@mantine/core";
import { IconDownload } from "@tabler/icons-react";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { humanizeBytes } from "@/core/formatting/BytesFormatting";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { toast } from "@/core/ui/notifications/Toast";
import { TranscriptionModelRow } from "@/features/transcription/components/TranscriptionModelRow";
import {
	invalidate,
	transcriptionQueryIds,
	useCancelTranscriptionModelDownload,
	useSelectTranscriptionModel,
	useStartTranscriptionModelDownload,
	useTranscriptionModels,
	useTranscriptionRecommendation,
} from "@/features/transcription/queries/useTranscriptionQueries";

interface MutationHandlers {
	readonly onError: (error: unknown) => void;
	readonly onSettled: () => void;
}

/**
 * The whisper weight catalogue and the only affordance this node has for acquiring one.
 *
 * A node ships with zero models: nothing seeds one at first run and nothing downloads one implicitly, so without
 * these controls the first transcription on every fresh install fails with "the selected transcription model is not
 * installed" and no screen in the app can fix it. Hence the first-run alert leads with the recommendation rather
 * than leaving the operator to pick from seven ids they have no basis to choose between.
 *
 * Progress arrives by polling, not over the hub: `useTranscriptionModels` re-reads every five seconds while any row
 * is running, and the download mutation invalidates the list so that the first running row is seen at once.
 */
export function TranscriptionModelManager() {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const modelsQuery = useTranscriptionModels();
	const recommendationQuery = useTranscriptionRecommendation();
	const startDownload = useStartTranscriptionModelDownload();
	const cancelDownload = useCancelTranscriptionModelDownload();
	const selectModel = useSelectTranscriptionModel();
	// UI-only: which row's button should spin. Server state stays in the query cache; this is the click, not the data.
	const [pendingModelId, setPendingModelId] = useState<string | null>(null);

	const models = modelsQuery.data?.models ?? [];
	const selectedModelId = modelsQuery.data?.selectedModelId ?? null;
	const recommendedModelId = modelsQuery.data?.recommendedModelId ?? null;
	const recommendation = recommendationQuery.data;

	// A finished transfer changes the RUNTIME status too — part one of every model download is the Silero VAD file, so
	// the card's "Voice-activity detection is not installed." line becomes a lie the moment the first download lands.
	// No mutation observes that: the transfer outlives the request that started it and its end is seen only by the
	// five-second list poll. The clean hook for it is the poll's own stop condition — the running → not-running edge,
	// which is exactly once per transfer. Keying the effect on the boolean is what keeps it off every other poll tick.
	const anyDownloadRunning = models.some((model) => model.downloadPhase === "running");
	const wasDownloadRunning = useRef(false);
	useEffect(() => {
		if (wasDownloadRunning.current && !anyDownloadRunning) {
			invalidate(queryClient, transcriptionQueryIds.runtime).catch(() => undefined);
		}
		wasDownloadRunning.current = anyDownloadRunning;
	}, [anyDownloadRunning, queryClient]);

	// `pendingId` is the row whose button spins; `variables` is what the endpoint is called with. They differ for
	// exactly one action — clearing the pin spins the selected row but sends a null.
	const runMutation = <TVariables,>(
		pendingId: string,
		variables: TVariables,
		mutate: (value: TVariables, handlers: MutationHandlers) => void,
		fallbackMessage: string,
	): void => {
		setPendingModelId(pendingId);
		mutate(variables, {
			onError: (error: unknown) => toast.error(apiErrorMessage(error, fallbackMessage)),
			onSettled: () => setPendingModelId(null),
		});
	};

	const handleDownload = (modelId: string): void => {
		runMutation(modelId, modelId, startDownload.mutate, t("pages.transcription.runtime.models.downloadStartFailed"));
	};

	const handleCancel = (modelId: string): void => {
		runMutation(modelId, modelId, cancelDownload.mutate, t("pages.transcription.runtime.models.cancelFailed"));
	};

	const handleSelect = (modelId: string): void => {
		runMutation(modelId, modelId, selectModel.mutate, t("pages.transcription.runtime.models.selectFailed"));
	};

	// Clearing the pin: the node falls back to the hardware recommendation, which is what a `null` model id means.
	const handleClearSelection = (pinnedModelId: string): void => {
		runMutation(pinnedModelId, null, selectModel.mutate, t("pages.transcription.runtime.models.selectFailed"));
	};

	if (modelsQuery.isError) {
		return (
			<InlineErrorAlert
				message={apiErrorMessage(modelsQuery.error, t("pages.transcription.runtime.models.loadFailed"))}
				variant="light"
				data-testid="transcription-models-error"
			/>
		);
	}

	if (modelsQuery.isPending) {
		return (
			<Group gap="xs" data-testid="transcription-models-loading">
				<Loader size="sm" />
				<Text size="sm" c="dimmed">
					{t("common.loading")}
				</Text>
			</Group>
		);
	}

	const hasInstalled = models.some((model) => model.installed);
	// The recommendation's own id is authoritative for the hint; the list's `recommendedModelId` is the same answer
	// from the same service and is what the rows are badged against, so the hint falls back to it while the
	// recommendation endpoint is still answering.
	const hintModelId = recommendation?.recommendedModelId ?? recommendedModelId;
	const recommendedModel = models.find((model) => model.id === hintModelId) ?? null;

	return (
		<Stack gap="sm" data-testid="transcription-models">
			<Divider />
			<Text size="sm" fw={500}>
				{t("pages.transcription.runtime.models.title")}
			</Text>

			{hasInstalled ? null : (
				<Alert color="blue" variant="light" data-testid="transcription-models-empty">
					<Stack gap="sm" align="flex-start">
						<Text size="sm">
							{hintModelId === null
								? t("pages.transcription.runtime.models.emptyNoRecommendation")
								: t("pages.transcription.runtime.models.empty", {
										model: hintModelId,
										size: humanizeBytes(recommendedModel?.sizeBytes ?? 0),
									})}
						</Text>
						{hintModelId === null ? null : (
							<Button
								size="xs"
								leftSection={<IconDownload size={14} />}
								loading={pendingModelId === hintModelId}
								disabled={pendingModelId !== null}
								onClick={() => handleDownload(hintModelId)}
								data-testid="transcription-models-empty-download"
							>
								{t("pages.transcription.runtime.models.downloadRecommended", { model: hintModelId })}
							</Button>
						)}
					</Stack>
				</Alert>
			)}

			{recommendation === undefined ? null : (
				<Text size="xs" c="dimmed" data-testid="transcription-models-recommendation">
					{t("pages.transcription.runtime.models.recommendationHint", {
						model: recommendation.recommendedModelId,
						backend: t(`pages.transcription.runtime.models.backend.${recommendation.backend}`),
						footprint: humanizeBytes(
							recommendation.backend === "cuda" ? recommendation.approximateVramBytes : recommendation.approximateRamBytes,
						),
					})}
				</Text>
			)}

			<Stack gap="sm" data-testid="transcription-models-list">
				{models.map((model) => (
					<TranscriptionModelRow
						key={model.id}
						model={model}
						isSelected={model.id === selectedModelId}
						isRecommended={model.id === recommendedModelId}
						isBusy={pendingModelId === model.id}
						isOtherBusy={pendingModelId !== null && pendingModelId !== model.id}
						onDownload={handleDownload}
						onCancel={handleCancel}
						onSelect={handleSelect}
					/>
				))}
			</Stack>

			{selectedModelId === null ? null : (
				<Group justify="flex-end">
					<Button
						size="xs"
						variant="subtle"
						loading={pendingModelId === selectedModelId}
						disabled={pendingModelId !== null}
						onClick={() => handleClearSelection(selectedModelId)}
						data-testid="transcription-models-clear-selection"
					>
						{t("pages.transcription.runtime.models.clearSelection")}
					</Button>
				</Group>
			)}
		</Stack>
	);
}
