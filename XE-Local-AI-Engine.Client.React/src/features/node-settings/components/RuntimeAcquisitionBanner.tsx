import { Alert, Button, CloseButton, Group, Loader, Progress, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconCloudDownload } from "@tabler/icons-react";
import type { TFunction } from "i18next";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { useDownloadRateEstimates } from "@/features/models/hooks/useDownloadRateEstimates";
import type { RateTrackedProgress } from "@/features/models/hooks/useDownloadRateEstimates";
import { formatDownloadEta, humanizeBytes } from "@/features/models/models/DownloadRateEstimate";
import { useRuntimeAcquisitionHub } from "@/features/node-settings/hooks/useRuntimeAcquisitionHub";
import { type LlamaCppVariant, llamaCppVariants } from "@/features/node-settings/models/LocalRuntimeModels";
import { type RuntimeAcquisitionStatus, useEnsureLlamaCppBinary } from "@/features/node-settings/queries/useLocalRuntime";

// Global first-run banner for the llama.cpp runtime acquisition, mounted once in the app shell beside the update
// banner. It exists because the host downloads the runtime on a background service, off the startup path: the whole UI
// renders and looks ready while nothing about chat works yet, and on a slow connection that unexplained pause is the
// longest thing a new user experiences. This is the only surface that says what is happening.
//
// Visibility, in full — each case is deliberate:
//   Idle / Completed → hidden. Nothing to explain.
//   DetectingGpu / Downloading / Verifying / Extracting → shown, and NOT dismissible: it is the answer to "why can't
//     I chat?", so letting it be closed puts the user straight back into the silence this replaces.
//   Failed → STAYS shown, with the sanitized reason and a retry, and is dismissible only from here. Hiding on every
//     terminal phase would delete the failure UX entirely and make an offline first run look like a slow one again.

// Phase values as the backend spells them (`nameof(RuntimeAcquisitionPhase.*)`).
const IDLE_PHASE = "Idle";
const DOWNLOADING_PHASE = "Downloading";
const COMPLETED_PHASE = "Completed";
const FAILED_PHASE = "Failed";

/**
 * The line that says what the host is doing right now, per non-terminal phase. An unrecognized phase (a backend that
 * grew one ahead of this client) falls back to the generic title rather than showing a raw enum name to the user.
 */
function phaseMessage(phase: string | undefined, t: TFunction): string {
	switch (phase) {
		case "DetectingGpu":
			return t("pages.nodeSettings.llamaCpp.acquisition.detectingGpu", "Detecting your graphics hardware…");
		case DOWNLOADING_PHASE:
			return t("pages.nodeSettings.llamaCpp.acquisition.downloading", "Downloading the llama.cpp runtime…");
		case "Verifying":
			return t("pages.nodeSettings.llamaCpp.acquisition.verifying", "Verifying the downloaded runtime…");
		case "Extracting":
			return t("pages.nodeSettings.llamaCpp.acquisition.extracting", "Installing the runtime…");
		default:
			return t("pages.nodeSettings.llamaCpp.acquisition.title", "Setting up the local AI runtime");
	}
}

/**
 * Rate-window key for the single transfer this banner tracks. It carries the step index because the Windows-CUDA path
 * fetches two archives: at the step boundary the byte counter restarts from zero, and a shared window would read that
 * as a stalled transfer and report a 0 B/s rate. A per-step key retires the old window instead.
 */
function rateKeyForStep(stepIndex: number): string {
	return `runtime-acquisition:${stepIndex}`;
}

/** Resolves the variant to re-ensure on retry. A probe that failed before choosing one leaves `variant` null; cpu is the always-available fallback. */
function retryVariant(status: RuntimeAcquisitionStatus): LlamaCppVariant {
	const reported = status.variant;
	return llamaCppVariants.find((variant) => variant === reported) ?? "cpu";
}

export function RuntimeAcquisitionBanner() {
	const { t } = useTranslation();
	// Auth gate mirrors GgufDownloadPoller: neither the hydrate GET nor the hub negotiate may fire pre-login (401).
	const isAuthenticated = useNodeAuthStore((state) => Boolean(state.accessToken));
	const status = useRuntimeAcquisitionHub(isAuthenticated);
	const ensureMutation = useEnsureLlamaCppBinary();
	// Dismissal is keyed by the sequence that was dismissed, not a boolean, so a retry that fails again (a new, higher
	// sequence) re-shows the banner. Component state rather than a store: this banner is mounted once for the app's
	// lifetime, so there is no remount across which the choice would need to survive.
	const [dismissedSequence, setDismissedSequence] = useState<number | null>(null);

	const phase = status?.phase;
	const completedBytes = status?.completedBytes;
	const totalBytes = status?.totalBytes;
	const stepIndex = status?.stepIndex ?? 1;

	// UX-11 rate/ETA, derived client-side from successive pushes (no timestamps ride the wire). `Downloading` is this
	// channel's active phase — the default `Running` belongs to the GGUF channel and would drop every sample here.
	const progressByKey = useMemo<ReadonlyMap<string, RateTrackedProgress>>(() => {
		if (phase === undefined) {
			return new Map();
		}
		return new Map([[rateKeyForStep(stepIndex), { phase, completedBytes, totalBytes }]]);
	}, [phase, completedBytes, totalBytes, stepIndex]);
	const rateEstimates = useDownloadRateEstimates(progressByKey, DOWNLOADING_PHASE);

	if (status === undefined || phase === IDLE_PHASE || phase === COMPLETED_PHASE) {
		return null;
	}

	const isFailed = phase === FAILED_PHASE;
	if (isFailed && dismissedSequence === status.sequence) {
		return null;
	}

	// Determinate only while bytes are actually moving AND the total is known. `totalBytes` is legitimately absent until
	// the response headers land, and a fabricated percentage there would be worse than an honest spinner.
	const hasDeterminateProgress = phase === DOWNLOADING_PHASE && completedBytes != null && totalBytes != null && totalBytes > 0;
	const percent = hasDeterminateProgress ? Math.min(100, Math.round((completedBytes / totalBytes) * 100)) : undefined;

	// The step counter only appears on the multi-archive path; without it that path's bar visibly runs 0→100 % twice
	// with no explanation.
	const stepLabel =
		status.stepCount > 1
			? t("pages.nodeSettings.llamaCpp.acquisition.step", "Step {{index}} of {{count}}", {
					index: stepIndex,
					count: status.stepCount,
				})
			: undefined;

	let byteLabel: string | undefined;
	if (completedBytes != null) {
		byteLabel =
			totalBytes != null && totalBytes > 0
				? t("pages.nodeSettings.llamaCpp.acquisition.progress", "{{completed}} of {{total}}", {
						completed: humanizeBytes(completedBytes),
						total: humanizeBytes(totalBytes),
					})
				: t("pages.nodeSettings.llamaCpp.acquisition.progressUnknownTotal", "{{completed}} downloaded", {
						completed: humanizeBytes(completedBytes),
					});
	}

	const estimate = rateEstimates.get(rateKeyForStep(stepIndex));
	const rateLabel =
		estimate?.bytesPerSecond !== undefined
			? t("pages.nodeSettings.llamaCpp.acquisition.rate", "{{rate}}/s", { rate: humanizeBytes(estimate.bytesPerSecond) })
			: undefined;
	const etaDuration = formatDownloadEta(estimate?.etaSeconds);
	const etaLabel = etaDuration ? t("pages.nodeSettings.llamaCpp.acquisition.eta", "about {{eta}} left", { eta: etaDuration }) : undefined;
	const detailLine = [stepLabel, byteLabel, rateLabel, etaLabel].filter(Boolean).join(" · ");

	return (
		<Alert
			color={isFailed ? "red" : "primary"}
			variant="light"
			icon={isFailed ? <IconAlertTriangle size={18} /> : <IconCloudDownload size={18} />}
			radius={0}
			withCloseButton={false}
			title={
				isFailed
					? t("pages.nodeSettings.llamaCpp.acquisition.failedTitle", "The local AI runtime could not be installed")
					: t("pages.nodeSettings.llamaCpp.acquisition.title", "Setting up the local AI runtime")
			}
			data-testid="runtime-acquisition-banner"
		>
			<Stack gap="xs">
				<Group justify="space-between" align="flex-start" wrap="nowrap" gap="sm">
					<Stack gap={2}>
						<Group gap="xs" align="center" wrap="nowrap">
							{!isFailed && !hasDeterminateProgress ? <Loader size="xs" /> : null}
							<Text size="sm" data-testid="runtime-acquisition-banner-message">
								{isFailed
									? (status.sanitizedError ??
										t(
											"pages.nodeSettings.llamaCpp.acquisition.failedFallback",
											"The llama.cpp runtime could not be downloaded. Check your internet connection and try again.",
										))
									: phaseMessage(phase, t)}
							</Text>
						</Group>
						{isFailed ? null : (
							<Text size="xs" c="dimmed" data-testid="runtime-acquisition-banner-detail">
								{detailLine.length > 0
									? detailLine
									: t(
											"pages.nodeSettings.llamaCpp.acquisition.explainer",
											"Chat is unavailable until this finishes. You can keep using the rest of the app.",
										)}
							</Text>
						)}
					</Stack>

					{isFailed ? (
						<Group gap="xs" wrap="nowrap">
							<Button
								size="xs"
								variant="filled"
								color="red"
								loading={ensureMutation.isPending}
								onClick={() => ensureMutation.mutate(retryVariant(status))}
								data-testid="runtime-acquisition-banner-retry"
							>
								{ensureMutation.isPending
									? t("pages.nodeSettings.llamaCpp.acquisition.retrying", "Retrying…")
									: t("pages.nodeSettings.llamaCpp.acquisition.retry", "Try again")}
							</Button>
							<CloseButton
								aria-label={t("pages.nodeSettings.llamaCpp.acquisition.dismiss", "Dismiss")}
								onClick={() => setDismissedSequence(status.sequence)}
								data-testid="runtime-acquisition-banner-dismiss"
							/>
						</Group>
					) : null}
				</Group>

				{percent !== undefined ? (
					<Progress
						value={percent}
						size="sm"
						radius="sm"
						aria-label={t("pages.nodeSettings.llamaCpp.acquisition.downloading", "Downloading the llama.cpp runtime…")}
						data-testid="runtime-acquisition-banner-progress"
					/>
				) : null}
			</Stack>
		</Alert>
	);
}
