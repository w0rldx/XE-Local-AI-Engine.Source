import { Badge, Button, Group, Skeleton, Stack, Text } from "@mantine/core";
import { IconMessage, IconMicrophone } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { toast } from "@/core/ui/notifications/Toast";
import { usePendingComposerTextStore } from "@/core/ui/stores/PendingComposerTextStore";
import { TranscriptSegmentList } from "@/features/transcription/components/TranscriptSegmentList";
import { useCancelTranscriptionSession, useTranscriptionSession } from "@/features/transcription/queries/useTranscriptionQueries";

interface TranscriptionSessionPageProps {
	readonly sessionId: string;
}

/**
 * One transcription session: its status, its configuration and the committed transcript.
 *
 * A failed run is still a 200 on the upload, so the failure lives on the row — `errorCode` / `errorMessage` are
 * rendered here rather than inferred from an HTTP status anywhere.
 */
export function TranscriptionSessionPage({ sessionId }: TranscriptionSessionPageProps) {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const detailQuery = useTranscriptionSession(sessionId);
	const cancelMutation = useCancelTranscriptionSession();
	const setPendingComposerText = usePendingComposerTextStore((state) => state.actions.setPendingText);

	const detail = detailQuery.data;
	const isTranscribing = detail?.session.status === "Transcribing";
	const transcript = (detail?.segments ?? []).map((segment) => segment.text.trim()).join(" ");

	return (
		<PageShell data-testid="transcription-session-page">
			<PageHeader
				icon={<IconMicrophone size={24} />}
				title={detail?.session.title ?? t("pages.transcription.session.untitled")}
				subtitle={t("pages.transcription.session.subtitle")}
				actions={
					<Group gap="sm">
						{transcript.length === 0 ? null : (
							<Button
								variant="light"
								leftSection={<IconMessage size={16} />}
								onClick={() => {
									// The transcript travels through the pending-composer store, not the URL: it is routinely
									// tens of kilobytes and would otherwise sit in the browser history in plaintext.
									setPendingComposerText(transcript);
									navigate({ to: nodeRoutePaths.chat });
								}}
								data-testid="transcription-session-send-to-chat"
							>
								{t("pages.transcription.session.sendToChat")}
							</Button>
						)}
						{isTranscribing ? (
							<Button
								variant="light"
								loading={cancelMutation.isPending}
								onClick={() => {
									cancelMutation.mutate(sessionId, {
										onError: (error) => toast.error(apiErrorMessage(error, t("pages.transcription.session.cancelFailed"))),
									});
								}}
								data-testid="transcription-session-cancel"
							>
								{t("pages.transcription.session.cancel")}
							</Button>
						) : null}
					</Group>
				}
			/>

			{detailQuery.isPending ? (
				<Skeleton height={200} radius="md" data-testid="transcription-session-loading" />
			) : detailQuery.isError || detail === undefined ? (
				<InlineErrorAlert
					message={apiErrorMessage(detailQuery.error, t("pages.transcription.session.loadFailed"))}
					variant="light"
					data-testid="transcription-session-error"
				/>
			) : (
				<Stack gap="md">
					<Group gap="xs">
						<Badge variant="light" data-testid="transcription-session-status">
							{t(`pages.transcription.status.${detail.session.status}`)}
						</Badge>
						<Text size="sm" c="dimmed">
							{t("pages.transcription.session.model", { model: detail.session.modelId })}
						</Text>
						{detail.session.detectedLanguage === null ? null : (
							<Text size="sm" c="dimmed" data-testid="transcription-session-language">
								{t("pages.transcription.session.detectedLanguage", { language: detail.session.detectedLanguage })}
							</Text>
						)}
					</Group>

					{detail.errorMessage === null ? null : (
						<InlineErrorAlert
							// The code is what a support conversation can search for; the message is what the operator reads.
							// Rendering only the message leaves the machine-readable half of the stored failure invisible.
							message={detail.errorMessage}
							title={detail.errorCode === null ? undefined : detail.errorCode}
							variant="light"
							data-testid="transcription-session-failure"
						/>
					)}

					<SectionCard title={t("pages.transcription.session.transcript")} gap="sm">
						{detail.segments.length === 0 ? (
							<EmptyState message={t("pages.transcription.session.noSegments")} data-testid="transcription-session-empty" />
						) : (
							<TranscriptSegmentList segments={detail.segments} />
						)}
					</SectionCard>
				</Stack>
			)}
		</PageShell>
	);
}
