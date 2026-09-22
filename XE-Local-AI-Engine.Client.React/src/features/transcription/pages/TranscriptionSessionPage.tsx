import { Badge, Button, Group, Skeleton, Stack, Text } from "@mantine/core";
import { IconMessage, IconMicrophone } from "@tabler/icons-react";
import { useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useEffect } from "react";
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
import { type LiveCaptureRequest, useLiveCapture } from "@/features/transcription/capture/useLiveCapture";
import { CaptureControls } from "@/features/transcription/components/CaptureControls";
import { LiveTranscriptPanel } from "@/features/transcription/components/LiveTranscriptPanel";
import { TranscriptSegmentList } from "@/features/transcription/components/TranscriptSegmentList";
import { useLiveTranscript } from "@/features/transcription/hooks/useTranscriptionHub";
import type { TranscriptionSourceKind } from "@/features/transcription/models/TranscriptionModels";
import {
	invalidate,
	transcriptionQueryIds,
	useCancelTranscriptionSession,
	useTranscriptionSession,
} from "@/features/transcription/queries/useTranscriptionQueries";
import { useTranscriptionCaptureStore } from "@/features/transcription/stores/TranscriptionCaptureStore";

interface TranscriptionSessionPageProps {
	readonly sessionId: string;
}

// The statuses the hub pushes when a live session is over. `Abandoned` and `Overloaded` exist only on the wire — the
// row itself is persisted as Cancelled, Completed or Failed — which is why this is a set of strings rather than the
// persisted status union.
const liveTerminalStatuses: ReadonlySet<string> = new Set(["Completed", "Cancelled", "Abandoned", "Overloaded", "Failed"]);

/**
 * Whether this source kind runs as a live session at all — hub-fed panel plus capture controls.
 *
 * Deliberately an allow-list rather than "everything but File": `ApplicationProcess` is live even though the browser
 * builds no capture request for it (the node records the application itself), and `Dictation` is not creatable from
 * any surface yet, so it stays off this list until the slice that ships it puts it there on purpose.
 */
function isLiveSourceKind(sourceKind: TranscriptionSourceKind): boolean {
	return (
		sourceKind === "Microphone" ||
		sourceKind === "SystemAudio" ||
		sourceKind === "MicrophoneAndSystem" ||
		sourceKind === "ApplicationProcess"
	);
}

/** The capture request one source kind asks for, or null for a session that is not captured in this browser. */
function toCaptureRequest(sourceKind: TranscriptionSourceKind, deviceId: string | null): LiveCaptureRequest | null {
	const microphone = deviceId === null ? undefined : deviceId;
	switch (sourceKind) {
		case "Microphone":
			return { kind: "microphone", deviceId: microphone };
		case "SystemAudio":
			return { kind: "systemAudio" };
		case "MicrophoneAndSystem":
			return { kind: "both", deviceId: microphone };
		default:
			return null;
	}
}

/**
 * One transcription session: its status, its configuration and the transcript.
 *
 * A live session that has not finished renders the hub-fed panel and the capture controls; every finished session —
 * and every file session — renders the persisted rows the detail endpoint returns. The switch is the session's own
 * status, so a session that ends while it is on screen changes over by itself.
 *
 * A failed run is still a 200 on the upload, so the failure lives on the row — `errorCode` / `errorMessage` are
 * rendered here rather than inferred from an HTTP status anywhere.
 */
export function TranscriptionSessionPage({ sessionId }: TranscriptionSessionPageProps) {
	// Capture errors belong to this session; route changes must not retain the previous hook state.
	return <TranscriptionSessionContent key={sessionId} sessionId={sessionId} />;
}

function TranscriptionSessionContent({ sessionId }: TranscriptionSessionPageProps) {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const queryClient = useQueryClient();
	const detailQuery = useTranscriptionSession(sessionId);
	const cancelMutation = useCancelTranscriptionSession();
	const setPendingComposerText = usePendingComposerTextStore((state) => state.actions.setPendingText);
	// This session's own microphone, not a global preference: a session created on a USB microphone must not capture
	// from the default just because a later session was created on it.
	const deviceId = useTranscriptionCaptureStore((state) => state.deviceIdBySession[sessionId] ?? null);
	// The application this session was created against. The node needs it to attach capture, and the create dialog is
	// the only place it was ever chosen, so a session whose entry is gone cannot be started at all.
	const processId = useTranscriptionCaptureStore((state) => state.processIdBySession[sessionId] ?? null);

	const detail = detailQuery.data;
	const isTranscribing = detail?.session.status === "Transcribing";
	const transcript = (detail?.segments ?? []).map((segment) => segment.text.trim()).join(" ");

	const sourceKind = detail?.session.sourceKind ?? "File";
	const captureRequest = toCaptureRequest(sourceKind, deviceId);
	// Live only until the row reaches a terminal status; after that the persisted transcript is the whole truth and the
	// hub has nothing left to say. Live is a property of the SOURCE KIND, not of whether the browser has a request to
	// build: an ApplicationProcess session is captured entirely on the node, so `captureRequest` is null for it and it
	// is live all the same — reading `captureRequest !== null` here left it rendering as a finished session.
	const isLive =
		isLiveSourceKind(sourceKind) && (detail?.session.status === "Created" || detail?.session.status === "Transcribing");
	// The one live source that can be un-startable: its pid lives only in this browser's store, so a cleared store
	// leaves nothing to capture. Reported rather than started against a process id the operator never chose.
	const processMissing = sourceKind === "ApplicationProcess" && processId === null;
	const capture = useLiveCapture(isLive ? sessionId : null);
	const liveView = useLiveTranscript(sessionId);
	const liveStatus = liveView?.status ?? "";

	// The node persisted the last rows as it ended the session, so the REST view has to be re-read before it takes over
	// from the panel — otherwise the finished session renders the transcript as it was when the page first loaded.
	useEffect(() => {
		if (!liveTerminalStatuses.has(liveStatus)) {
			return;
		}
		invalidate(queryClient, transcriptionQueryIds.session).catch(() => undefined);
		invalidate(queryClient, transcriptionQueryIds.sessions).catch(() => undefined);
	}, [liveStatus, queryClient]);

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

					{!isLive && isLiveSourceKind(sourceKind) && capture.error !== null ? (
						<InlineErrorAlert
							message={t(`pages.transcription.capture.error.${capture.error}`)}
							variant="light"
							data-testid="transcription-capture-error"
						/>
					) : null}

					{isLive && processMissing ? (
						<InlineErrorAlert
							message={t("pages.transcription.session.processMissing")}
							variant="light"
							data-testid="transcription-session-process-missing"
						/>
					) : null}

					{isLive && !processMissing ? (
						<CaptureControls
							sourceKind={sourceKind}
							state={capture.state}
							error={capture.error}
							replayStalled={capture.replayStalled}
							connected={capture.connected}
							subscribeFailed={capture.subscribeFailed}
							elapsedMs={(liveView?.committed ?? []).reduce((latest, segment) => Math.max(latest, segment.endMs), 0)}
							// R39a: start is called straight out of the click, with nothing awaited in between, or the browser
							// refuses to open the screen-share picker.
							onStart={() => {
								// An ApplicationProcess session has no browser-side request: the node is told which process to
								// record, and the hook posts capture/process once live/start has resolved.
								const request: LiveCaptureRequest | null =
									sourceKind === "ApplicationProcess" && processId !== null ? { kind: "process", processId } : captureRequest;
								if (request === null) {
									return;
								}
								capture.start(request).catch(() => undefined);
							}}
							onStop={() => {
								capture.stop().catch(() => undefined);
							}}
							onCancel={() => {
								capture.cancel().catch(() => undefined);
							}}
						/>
					) : null}

					<SectionCard title={t("pages.transcription.session.transcript")} gap="sm">
						{isLive ? (
							<LiveTranscriptPanel view={liveView} />
						) : detail.segments.length === 0 ? (
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
