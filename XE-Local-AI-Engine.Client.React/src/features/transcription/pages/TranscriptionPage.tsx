import { Alert, Button, Skeleton, Stack, Text } from "@mantine/core";
import { IconMicrophone, IconPlus } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { toast } from "@/core/ui/notifications/Toast";
import {
	NewTranscriptionSessionDialog,
	type NewTranscriptionSessionValues,
} from "@/features/transcription/components/NewTranscriptionSessionDialog";
import { TranscriptionRuntimeCard } from "@/features/transcription/components/TranscriptionRuntimeCard";
import { TranscriptionSessionList } from "@/features/transcription/components/TranscriptionSessionList";
import { type TranscriptionSessionView, unsupportedContainerDetail } from "@/features/transcription/models/TranscriptionModels";
import {
	useCreateTranscriptionSession,
	useDeleteTranscriptionSession,
	useTranscriptionSessions,
} from "@/features/transcription/queries/useTranscriptionQueries";
import { useTranscriptionUpload } from "@/features/transcription/queries/useTranscriptionUpload";

/**
 * Transcription session history and the entry point for a new one.
 *
 * A file session is two calls: create the row (which carries the title, because the upload endpoint never sets one),
 * then POST the audio. The upload answers 200 for every finished outcome, so the resulting session's own status is
 * what says whether it worked — only 400 / 404 / 415 reject, and the 415 carries the readable-container list.
 */
/**
 * Resolves the operator-facing message for an upload the node refused to read, or null when the failure was
 * something else.
 *
 * The two 415 cases need different sentences because they need different actions: a container the node can never
 * read means "use one of these formats", while a container it could read with ffmpeg means "install ffmpeg" — the
 * engine transcodes it once the binary is on PATH. One shared sentence would leave the ffmpeg case telling the
 * operator to re-export a file that did not need re-exporting.
 */
function useUnsupportedMessage(): (error: unknown) => string | null {
	const { t } = useTranslation();
	return (error: unknown) => {
		const unsupported = unsupportedContainerDetail(error);
		if (unsupported === null) {
			return null;
		}
		const containers = unsupported.supportedContainers.join(", ");
		return unsupported.ffmpegRequired
			? t("pages.transcription.dialog.unsupportedContainerFfmpeg", { containers })
			: t("pages.transcription.dialog.unsupportedContainer", { containers });
	};
}

export function TranscriptionPage() {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const { confirm } = useConfirm();
	const unsupportedMessage = useUnsupportedMessage();
	const [dialogOpened, setDialogOpened] = useState(false);
	const [submitError, setSubmitError] = useState<string | undefined>(undefined);
	const [deletingSessionId, setDeletingSessionId] = useState<string | null>(null);

	const sessionsQuery = useTranscriptionSessions();
	const createMutation = useCreateTranscriptionSession();
	const deleteMutation = useDeleteTranscriptionSession();
	const upload = useTranscriptionUpload();

	const sessions = sessionsQuery.data?.items ?? [];
	const isSubmitting = createMutation.isPending || upload.isUploading;

	const handleSubmit = (values: NewTranscriptionSessionValues): void => {
		setSubmitError(undefined);
		createMutation.mutate(
			{
				title: values.title,
				sourceKind: values.sourceKind,
				languageMode: values.languageMode,
				languageOverride: values.languageOverride,
				translate: values.translate,
				maxWindowSeconds: values.maxWindowSeconds,
				channelAttribution: values.channelAttribution,
			},
			{
				onSuccess: (detail) => {
					const sessionId = detail?.session.id;
					if (sessionId === undefined) {
						setSubmitError(t("pages.transcription.dialog.createFailed"));
						return;
					}
					upload
						.uploadAudio({ sessionId, file: values.file })
						.then(() => {
							setDialogOpened(false);
							navigate({ to: nodeRoutePaths.transcriptionSession, params: { sessionId } });
						})
						.catch((error: unknown) => {
							setSubmitError(unsupportedMessage(error) ?? apiErrorMessage(error, t("pages.transcription.dialog.uploadFailed")));
						});
				},
				onError: (error) => setSubmitError(apiErrorMessage(error, t("pages.transcription.dialog.createFailed"))),
			},
		);
	};

	// Deleting a session destroys the only copy of the transcript — the audio was never kept — so it is confirmed
	// first, like every other destructive list action on this node.
	const handleDelete = (session: TranscriptionSessionView): void => {
		confirm({
			title: t("pages.transcription.list.deleteTitle"),
			description: t("pages.transcription.list.deleteDescription", {
				name: session.title ?? t("pages.transcription.list.untitled"),
			}),
			confirmationText: t("common.delete"),
			cancellationText: t("common.cancel"),
		})
			.then((confirmed) => {
				if (!confirmed) {
					return;
				}
				setDeletingSessionId(session.id);
				deleteMutation.mutate(session.id, {
					onError: (error) => toast.error(apiErrorMessage(error, t("pages.transcription.list.deleteFailed"))),
					onSettled: () => setDeletingSessionId(null),
				});
			})
			.catch(() => undefined);
	};

	return (
		<PageShell data-testid="transcription-page">
			<PageHeader
				icon={<IconMicrophone size={24} />}
				title={t("pages.transcription.title")}
				subtitle={t("pages.transcription.subtitle")}
				actions={
					<Button leftSection={<IconPlus size={16} />} onClick={() => setDialogOpened(true)} data-testid="transcription-create">
						{t("pages.transcription.create.open")}
					</Button>
				}
			/>

			<TranscriptionRuntimeCard />

			<SectionCard title={t("pages.transcription.list.title")} gap="sm">
				{sessionsQuery.isPending ? (
					<Stack gap="sm" data-testid="transcription-sessions-loading">
						<Skeleton height={64} radius="md" />
						<Skeleton height={64} radius="md" />
					</Stack>
				) : sessionsQuery.isError ? (
					<InlineErrorAlert
						message={apiErrorMessage(sessionsQuery.error, t("pages.transcription.loadFailed"))}
						variant="light"
						data-testid="transcription-sessions-error"
					>
						<Button
							size="xs"
							variant="light"
							onClick={() => {
								sessionsQuery.refetch().catch(() => undefined);
							}}
							data-testid="transcription-sessions-retry"
						>
							{t("pages.transcription.retry")}
						</Button>
					</InlineErrorAlert>
				) : sessions.length === 0 ? (
					<Alert color="blue" variant="light" data-testid="transcription-sessions-empty">
						<Stack gap="sm" align="flex-start">
							<Text size="sm">{t("pages.transcription.empty")}</Text>
							<Button size="xs" onClick={() => setDialogOpened(true)} data-testid="transcription-empty-create">
								{t("pages.transcription.create.first")}
							</Button>
						</Stack>
					</Alert>
				) : (
					<TranscriptionSessionList
						sessions={sessions}
						deletingSessionId={deletingSessionId}
						onOpen={(sessionId) => {
							navigate({ to: nodeRoutePaths.transcriptionSession, params: { sessionId } });
						}}
						onDelete={handleDelete}
					/>
				)}
			</SectionCard>

			<NewTranscriptionSessionDialog
				opened={dialogOpened}
				isSubmitting={isSubmitting}
				uploadProgressPercent={upload.progressPercent}
				errorMessage={submitError}
				onClose={() => {
					setSubmitError(undefined);
					setDialogOpened(false);
				}}
				onSubmit={handleSubmit}
			/>
		</PageShell>
	);
}
