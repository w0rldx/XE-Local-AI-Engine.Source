import { Badge, Button, Group, Loader, Progress, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { toast } from "@/core/ui/notifications/Toast";
import {
	useEjectTranscriptionRuntime,
	useTranscriptionModels,
	useTranscriptionRuntimeStatus,
} from "@/features/transcription/queries/useTranscriptionQueries";

/**
 * The whisper runtime at a glance: process state, the effective model, VAD and the managed build, with an eject.
 *
 * `backend`, `binarySource` and `binaryVersion` are null until a daemon has actually spawned, so a null renders as
 * "not started" — reading one as "not installed" would tell an operator to reinstall a runtime that is fine.
 */
export function TranscriptionRuntimeCard() {
	const { t } = useTranslation();
	const runtimeQuery = useTranscriptionRuntimeStatus();
	const runtime = runtimeQuery.data;
	const modelsQuery = useTranscriptionModels();
	const ejectMutation = useEjectTranscriptionRuntime();

	if (runtimeQuery.isError) {
		return (
			<SectionCard title={t("pages.transcription.runtime.title")}>
				<InlineErrorAlert
					message={apiErrorMessage(runtimeQuery.error, t("pages.transcription.runtime.loadFailed"))}
					variant="light"
					data-testid="transcription-runtime-error"
				/>
			</SectionCard>
		);
	}

	const download = modelsQuery.data?.models.find((model) => model.downloadPhase === "running") ?? null;

	// Every line below states a FACT about the runtime, so none of them may render before the status has loaded:
	// an undefined `runtime` would otherwise report "voice-activity detection is not installed" and an empty model
	// name, which reads as a broken install rather than as a card that has not loaded yet.
	if (runtime === undefined) {
		return (
			<SectionCard title={t("pages.transcription.runtime.title")} data-testid="transcription-runtime-card">
				<Group gap="xs">
					<Loader size="sm" />
					<Text size="sm" c="dimmed" data-testid="transcription-runtime-loading">
						{t("common.loading")}
					</Text>
				</Group>
			</SectionCard>
		);
	}

	return (
		<SectionCard title={t("pages.transcription.runtime.title")} data-testid="transcription-runtime-card">
			<Group gap="xs">
				<Badge variant="light" data-testid="transcription-runtime-state">
					{t(`pages.transcription.runtime.state.${runtime.state}`)}
				</Badge>
				<Text size="sm" c="dimmed">
					{runtime.backend === null
						? t("pages.transcription.runtime.backendNotStarted")
						: t("pages.transcription.runtime.backend", { backend: runtime.backend })}
				</Text>
			</Group>
			<Stack gap={2}>
				<Text size="sm">
					{t("pages.transcription.runtime.model", { model: runtime.selectedModelId ?? runtime.recommendedModelId })}
				</Text>
				{runtime.selectedModelId === null ? (
					<Text size="xs" c="dimmed">
						{t("pages.transcription.runtime.modelRecommended")}
					</Text>
				) : null}
				<Text size="xs" c="dimmed">
					{runtime.vadInstalled ? t("pages.transcription.runtime.vadInstalled") : t("pages.transcription.runtime.vadMissing")}
				</Text>
				{runtime.managedRuntimeValidity === null ? null : (
					<Text size="xs" c="dimmed" data-testid="transcription-runtime-managed">
						{t(`pages.transcription.runtime.managed.${runtime.managedRuntimeValidity}`)}
					</Text>
				)}
			</Stack>
			{download === null ? null : (
				<Stack gap={4} data-testid="transcription-runtime-download">
					<Text size="xs">{t("pages.transcription.runtime.downloading", { model: download.id })}</Text>
					<Progress value={download.downloadPercent ?? 0} />
				</Stack>
			)}
			<Group justify="flex-end">
				<Button
					size="xs"
					variant="light"
					disabled={runtime.state !== "ready"}
					loading={ejectMutation.isPending}
					onClick={() => {
						ejectMutation.mutate(undefined, {
							onError: (error) => toast.error(apiErrorMessage(error, t("pages.transcription.runtime.ejectFailed"))),
						});
					}}
					data-testid="transcription-runtime-eject"
				>
					{t("pages.transcription.runtime.eject")}
				</Button>
			</Group>
		</SectionCard>
	);
}
