import { Alert, Group, Progress, Stack, Text } from "@mantine/core";
import { IconInfoCircle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { useVoicePreferencesStore } from "@/features/voice/VoicePreferencesStore";
import { useVoiceRuntime } from "@/features/voice/VoiceRuntimeContext";

// Voice status banners: the one-time model-download progress/error (dismissible) and a
// capability/fallback notice (e.g. "using browser speech — WebGPU unavailable"). Self-gates on the runtime context;
// renders nothing when voice is off. The AudioContext gesture-unlock still happens in the runtime, but it needs no
// banner — the user starts playback themselves. NOTE: in the default delivery path the Kokoro worker downloads
// weights through Transformers.js' own cache, so the ModelCache-driven progress bar is the seam for the
// self-hosted/prefetch path and only shows when ModelCache actually fetches.

function formatMb(bytes: number): string {
	return (bytes / (1024 * 1024)).toFixed(0);
}

export function VoiceStatusNotice() {
	const { t } = useTranslation();
	const { enabled, capabilities, downloadProgress, downloadError, dismissDownloadNotice } = useVoiceRuntime();
	const voiceEnabled = useVoicePreferencesStore((state) => state.voiceEnabled);

	if (!enabled || !voiceEnabled) {
		return null;
	}

	const downloadComplete =
		downloadProgress !== undefined && downloadProgress.total > 0 && downloadProgress.loaded >= downloadProgress.total;
	const showProgress = downloadProgress !== undefined && !downloadComplete;
	const progressPercent =
		downloadProgress && downloadProgress.total > 0
			? Math.min(100, Math.round((downloadProgress.loaded / downloadProgress.total) * 100))
			: 0;

	// WebGPU unavailable → synthesis runs on the WASM/Web-Speech rungs; surface that the browser path is in use.
	const showFallbackNotice = capabilities !== undefined && !capabilities.webgpu;

	if (!showProgress && downloadError === undefined && !showFallbackNotice) {
		return null;
	}

	return (
		<Stack gap={6} mb="xs" data-testid="voice-status-notice">
			{showProgress ? (
				<Alert color="blue" variant="light" p="xs" withCloseButton={true} onClose={dismissDownloadNotice}>
					<Stack gap={4}>
						<Group justify="space-between" gap="xs">
							<Text size="xs">{t("voice.notice.downloading")}</Text>
							<Text size="xs" c="dimmed">
								{t("voice.notice.downloadProgress", {
									loaded: formatMb(downloadProgress.loaded),
									total: formatMb(downloadProgress.total),
								})}
							</Text>
						</Group>
						<Progress value={progressPercent} size="sm" />
					</Stack>
				</Alert>
			) : null}
			{downloadError ? (
				<Alert color="red" variant="light" p="xs" withCloseButton={true} onClose={dismissDownloadNotice}>
					<Text size="xs">{t("voice.notice.downloadError")}</Text>
				</Alert>
			) : null}
			{showFallbackNotice ? (
				<Alert color="gray" variant="light" icon={<IconInfoCircle size={16} />} p="xs">
					<Text size="xs">{t("voice.notice.webSpeechFallback")}</Text>
				</Alert>
			) : null}
		</Stack>
	);
}
