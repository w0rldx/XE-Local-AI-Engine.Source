import { Alert, Badge, Button, Group, Text } from "@mantine/core";
import { IconPlayerRecordFilled, IconPlayerStopFilled } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import type { LiveCaptureErrorCode, LiveCaptureState } from "@/features/transcription/capture/useLiveCapture";
import type { TranscriptionSubscribeFailed } from "@/features/transcription/hooks/useTranscriptionHub";
import type { TranscriptionRuntimeView, TranscriptionSourceKind } from "@/features/transcription/models/TranscriptionModels";

interface CaptureControlsProps {
	readonly sourceKind: TranscriptionSourceKind;
	readonly state: LiveCaptureState;
	readonly error: LiveCaptureErrorCode | null;
	readonly replayStalled: boolean;
	/** False until the hub subscription is up. Start is disabled while it is, with the reason beside it. */
	readonly connected: boolean;
	/** Non-null once the node refused the subscription. */
	readonly subscribeFailed: TranscriptionSubscribeFailed | null;
	/** The end of the last committed segment — AUDIO time, which is the only clock the transcript itself keeps. */
	readonly elapsedMs: number;
	/** Audio the node has buffered but not yet transcribed; null until it has reported any. */
	readonly bufferedMs: number | null;
	/** The runtime's state while a start is pending; null when unknown. Only read while `state` is `starting`. */
	readonly runtimeState: TranscriptionRuntimeView["state"] | null;
	readonly onStart: () => void;
	readonly onStop: () => void;
	readonly onCancel: () => void;
}

/**
 * Formats captured audio as `mm:ss`.
 *
 * Wall-clock time and audio time diverge — the runtime lags, a pause produces no audio — and the transcript's own
 * clock is the honest one to show beside it: an operator comparing the counter to the last timestamp on screen should
 * see them agree.
 */
function formatElapsed(milliseconds: number): string {
	const totalSeconds = Math.floor(Math.max(milliseconds, 0) / 1000);
	const minutes = Math.floor(totalSeconds / 60);
	const seconds = totalSeconds % 60;
	return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
}

/** The lanes this session's source kind produces, as the labels the operator reads on each committed row. */
function sourceBadgeKeys(sourceKind: TranscriptionSourceKind): readonly string[] {
	return sourceKind === "MicrophoneAndSystem"
		? ["pages.transcription.channel.You", "pages.transcription.channel.Others"]
		: [`pages.transcription.source.${sourceKind}`];
}

/**
 * The refusals worth their own sentence, because each has a different thing for the operator to do. Everything else
 * the node or the transport can refuse with reads the same to them, so it shares one string.
 */
/**
 * What a pending start is waiting on. The runtime reports `starting` only once its binary is in place, so `stopped`
 * while a start is pending is the first-use binary download (or the backend probe before it) and `starting` is the
 * model load.
 */
const startStatusKeys: Readonly<Record<TranscriptionRuntimeView["state"], string>> = {
	stopped: "pages.transcription.capture.startStatus.preparing",
	starting: "pages.transcription.capture.startStatus.loadingModel",
	ready: "pages.transcription.capture.startStatus.starting",
};

const subscribeErrorKeys: Readonly<Record<string, string>> = {
	"transcription-session-not-found": "pages.transcription.capture.error.subscribeSessionNotFound",
	"transcription-disabled": "pages.transcription.capture.error.subscribeDisabled",
};

/** Start and stop for a live session, with what is being captured, how much of it, and why it stopped. */
export function CaptureControls({
	sourceKind,
	state,
	error,
	replayStalled,
	connected,
	subscribeFailed,
	elapsedMs,
	bufferedMs,
	runtimeState,
	onStart,
	onStop,
	onCancel,
}: CaptureControlsProps) {
	const { t } = useTranslation();
	// While a start is still acquiring devices the Start button stays in place and busy: offering Stop there would
	// race the acquisition it is meant to undo.
	const canStop = state === "capturing";
	const behindSeconds = bufferedMs === null ? 0 : Math.ceil(bufferedMs / 1000);

	return (
		<Group gap="sm" align="center" wrap="wrap">
			{state === "stopping" ? (
				<>
					<Button variant="light" disabled={true} data-testid="transcription-capture-finalizing">
						{behindSeconds > 0
							? t("pages.transcription.capture.finishing", { seconds: behindSeconds })
							: t("pages.transcription.capture.finalizing")}
					</Button>
					<Button variant="light" color="red" onClick={onCancel} data-testid="transcription-capture-cancel">
						{t("pages.transcription.capture.cancel")}
					</Button>
				</>
			) : canStop ? (
				<Button
					variant="light"
					color="red"
					leftSection={<IconPlayerStopFilled size={16} />}
					onClick={onStop}
					data-testid="transcription-capture-stop"
				>
					{t("pages.transcription.capture.stop")}
				</Button>
			) : (
				// Gated on the subscription, not just on the state machine: a capture begun before the hub is up
				// acquires the devices, then fails on its first frame a quarter of a second later.
				<Button
					leftSection={<IconPlayerRecordFilled size={16} />}
					loading={state === "starting"}
					disabled={state === "starting" || !connected}
					onClick={onStart}
					data-testid="transcription-capture-start"
				>
					{t("pages.transcription.capture.start")}
				</Button>
			)}

			{state === "starting" ? (
				<Text size="sm" c="dimmed" data-testid="transcription-capture-start-status">
					{t(runtimeState === null ? startStatusKeys.ready : startStatusKeys[runtimeState])}
				</Text>
			) : null}

			{state === "capturing" && behindSeconds > 0 ? (
				<Text size="sm" c="dimmed" data-testid="transcription-capture-behind">
					{t("pages.transcription.capture.behind", { seconds: behindSeconds })}
				</Text>
			) : null}

			{state === "capturing" || state === "stopping" || connected ? null : (
				<Text size="sm" c="dimmed" data-testid="transcription-capture-not-ready">
					{t("pages.transcription.capture.notConnected")}
				</Text>
			)}

			{sourceBadgeKeys(sourceKind).map((key) => (
				<Badge key={key} variant="light">
					{t(key)}
				</Badge>
			))}

			<Text size="sm" c="dimmed" ff="monospace" data-testid="transcription-capture-elapsed">
				{formatElapsed(elapsedMs)}
			</Text>

			{error === null ? null : (
				<InlineErrorAlert
					message={t(`pages.transcription.capture.error.${error}`)}
					variant="light"
					data-testid="transcription-capture-error"
				/>
			)}

			{subscribeFailed === null ? null : (
				<InlineErrorAlert
					message={t(subscribeErrorKeys[subscribeFailed.code] ?? "pages.transcription.capture.error.subscribeFailed")}
					variant="light"
					data-testid="transcription-capture-subscribe-error"
				/>
			)}

			{replayStalled ? (
				<Alert color="yellow" variant="light" data-testid="transcription-replay-stalled">
					{t("pages.transcription.capture.replayStalled")}
				</Alert>
			) : null}
		</Group>
	);
}
