import { Badge, Button, Card, Group, Progress, Stack, Text } from "@mantine/core";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { useImageJobProgress } from "@/features/images/hooks/useImageJobHub";
import {
	type ImageJobStatus,
	type ImageJobView,
	type ImageProgressDisplay,
	isTerminalStatus,
	toProgressDisplay,
} from "@/features/images/models/ImageModels";
import { ImageResultView } from "@/features/images/components/ImageResultView";

// Mantine badge colour per coarse status. Kept tiny + local — the only place status maps to a colour.
const statusColor: Record<ImageJobStatus, string> = {
	Queued: "gray",
	Generating: "blue",
	Succeeded: "green",
	Failed: "red",
	Cancelled: "gray",
};

// Live elapsed seconds since a generating job started, ticking once per second. Returns 0 when inactive so a
// terminal/queued card renders no timer. A single interval per active card is fine — the coordinator serializes to
// one generating job at a time.
function useElapsedSeconds(startedAtUtc: number | null, active: boolean): number {
	const [seconds, setSeconds] = useState(0);

	useEffect(() => {
		if (!active || startedAtUtc === null) {
			setSeconds(0);
			return;
		}
		const tick = (): void => setSeconds(Math.max(0, Math.floor((Date.now() - startedAtUtc) / 1000)));
		tick();
		const handle = window.setInterval(tick, 1000);
		return () => window.clearInterval(handle);
	}, [startedAtUtc, active]);

	return seconds;
}

// The live generation timeline, rendered from the phase the runtime reports rather than from the elapsed clock.
// Loading/encoding show a "preparing" line with NO countdown, sampling shows the step bar with the remaining time,
// and the decode that follows the last step shows "finishing" — never a countdown resting at zero.
function GenerationTimeline({ display }: { display: ImageProgressDisplay }) {
	const { t } = useTranslation();

	if (display.kind === "queued") {
		return display.queuePosition === null ? null : (
			<Text size="xs" c="dimmed" data-testid="image-job-queue-position">
				{t("pages.images.job.queuePosition", "Waiting — position {{position}} in the queue", { position: display.queuePosition })}
			</Text>
		);
	}

	if (display.kind === "preparing") {
		return (
			<Text size="xs" c="dimmed" data-testid="image-job-phase">
				{t("pages.images.job.preparing", "Preparing model…")}
			</Text>
		);
	}

	if (display.kind === "finishing") {
		return (
			<Stack gap={4}>
				<Progress value={100} size="sm" radius="xl" animated={true} aria-hidden={true} />
				<Text size="xs" c="dimmed" data-testid="image-job-phase">
					{t("pages.images.job.finishing", "Finishing (decoding image)…")}
				</Text>
			</Stack>
		);
	}

	if (display.kind === "sampling") {
		const percent = Math.round((display.step / display.totalSteps) * 100);
		return (
			<Stack gap={4}>
				<Progress
					value={percent}
					size="sm"
					radius="xl"
					aria-label={t("pages.images.job.stepProgressLabel", "Generation progress")}
					data-testid="image-job-step-progress"
				/>
				<Group gap="xs" justify="space-between">
					<Text size="xs" c="dimmed" data-testid="image-job-steps">
						{t("pages.images.job.steps", "Step {{step}} of {{totalSteps}}", { step: display.step, totalSteps: display.totalSteps })}
					</Text>
					{display.estimatedRemainingMs === null ? null : (
						<Text size="xs" c="dimmed" data-testid="image-job-eta">
							{t("pages.images.job.eta", "~{{seconds}}s left", { seconds: Math.max(1, Math.round(display.estimatedRemainingMs / 1000)) })}
						</Text>
					)}
				</Group>
			</Stack>
		);
	}

	return null;
}

interface ImageJobCardProps {
	job: ImageJobView;
	isCancelling: boolean;
	onCancel: (jobId: string) => void;
}

// One image job row: queued → generating → succeeded/failed/cancelled, with the live generation timeline (phase, step
// bar, estimate) underneath while the job runs. A cancellable job (queued or generating) shows a Cancel button; a
// succeeded job shows its decrypted PNG.
export function ImageJobCard({ job, isCancelling, onCancel }: ImageJobCardProps) {
	const { t } = useTranslation();
	const isGenerating = job.status === "Generating";
	const elapsed = useElapsedSeconds(job.startedAtUtc, isGenerating);
	const canCancel = !isTerminalStatus(job.status);
	const display = toProgressDisplay(useImageJobProgress(job.id));

	return (
		<Card withBorder={true} padding="md" radius="md" data-testid="image-job-card">
			<Stack gap="xs">
				<Group justify="space-between" align="flex-start" wrap="nowrap">
					<Stack gap={2} style={{ minWidth: 0 }}>
						<Group gap="xs" align="center">
							<Badge color={statusColor[job.status]} data-testid="image-job-status">
								{t(`pages.images.status.${job.status}`, job.status)}
							</Badge>
							{isGenerating ? (
								<Text size="sm" c="dimmed" data-testid="image-job-elapsed">
									{t("pages.images.job.elapsed", "{{seconds}}s", { seconds: elapsed })}
								</Text>
							) : null}
							{job.status === "Succeeded" && job.durationMs !== null ? (
								<Text size="sm" c="dimmed" data-testid="image-job-duration">
									{t("pages.images.job.duration", "{{seconds}}s", { seconds: Math.round(job.durationMs / 1000) })}
								</Text>
							) : null}
						</Group>
						<Text size="sm" lineClamp={2} data-testid="image-job-prompt">
							{job.prompt}
						</Text>
						<Text size="xs" c="dimmed">
							{t("pages.images.job.meta", "{{model}} · {{width}}×{{height}} · {{steps}} steps · {{sampler}}", {
								model: job.modelName,
								width: job.width,
								height: job.height,
								steps: job.steps,
								sampler: job.sampler,
							})}
						</Text>
					</Stack>
					{canCancel ? (
						<Button
							size="xs"
							variant="light"
							color="red"
							loading={isCancelling}
							onClick={() => onCancel(job.id)}
							data-testid="image-job-cancel"
						>
							{t("common.cancel", "Cancel")}
						</Button>
					) : null}
				</Group>

				{isTerminalStatus(job.status) ? null : <GenerationTimeline display={display} />}

				{job.status === "Failed" && job.sanitizedError ? (
					<Text size="sm" c="red" data-testid="image-job-error">
						{job.sanitizedError}
					</Text>
				) : null}

				{job.status === "Succeeded" && job.imageId ? (
					<ImageResultView job={job} imageId={job.imageId} />
				) : null}
			</Stack>
		</Card>
	);
}
