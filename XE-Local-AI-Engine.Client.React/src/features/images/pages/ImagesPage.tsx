import { Card, Group, SimpleGrid, Stack, Text, Title } from "@mantine/core";
import { IconPhoto } from "@tabler/icons-react";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import { toast } from "@/core/ui/notifications/Toast";
import { ImageGenerationForm } from "@/features/images/components/ImageGenerationForm";
import { ImageJobList } from "@/features/images/components/ImageJobList";
import { ImageModelManager } from "@/features/images/components/ImageModelManager";
import { useImageJobHub } from "@/features/images/hooks/useImageJobHub";
import { type ImageGenerationFormValues, isTerminalStatus } from "@/features/images/models/ImageModels";
import { useCancelImageJob, useCreateImageJob, useImageJobs, useImageModels } from "@/features/images/queries/useImageQueries";

// Local image-generation page (text-to-image). Server-state is TanStack Query; live coarse status arrives over the
// image SignalR hub, which invalidates the jobs cache on each transition (job state is never mirrored into a store).
// Left column: generation form + minimal model manager. Right column: the job queue/history.
export function ImagesPage() {
	const { t } = useTranslation();

	// While a detached model download is in flight (the manager owns that state), poll listImageModels so the freshly
	// downloaded model surfaces on completion — the backend exposes no download-progress hub yet.
	const [modelDownloadPending, setModelDownloadPending] = useState(false);
	const modelsQuery = useImageModels(modelDownloadPending);
	const jobsQuery = useImageJobs();
	const createMutation = useCreateImageJob();
	const cancelMutation = useCancelImageJob();

	const [submitError, setSubmitError] = useState<string | undefined>(undefined);
	const [cancellingJobId, setCancellingJobId] = useState<string | null>(null);

	const models = useMemo(() => modelsQuery.data ?? [], [modelsQuery.data]);
	const jobs = useMemo(() => jobsQuery.data ?? [], [jobsQuery.data]);

	// Subscribe the hub only to jobs that can still transition (queued / generating) — a terminal job needs no push.
	const activeJobIds = useMemo(() => jobs.filter((job) => !isTerminalStatus(job.status)).map((job) => job.id), [jobs]);
	useImageJobHub(activeJobIds);

	const handleGenerate = useCallback(
		(values: ImageGenerationFormValues) => {
			setSubmitError(undefined);
			createMutation.mutate(
				{
					modelName: values.modelName,
					prompt: values.prompt,
					negativePrompt: values.negativePrompt ?? null,
					// The seed rides the wire as a precision-safe string (the form keeps it as a bounded number).
					seed: String(values.seed),
					width: values.width,
					height: values.height,
					steps: values.steps,
					sampler: values.sampler,
					cfgScale: values.cfgScale,
				},
				{
					onError: (error) => {
						const message =
							error instanceof ApiError && error.message
								? error.message
								: t("pages.images.form.error", "Could not start generation.");
						setSubmitError(message);
					},
				},
			);
		},
		[createMutation, t],
	);

	const handleCancel = useCallback(
		(jobId: string) => {
			setCancellingJobId(jobId);
			cancelMutation.mutate(jobId, {
				onError: (error) => {
					const message =
						error instanceof ApiError && error.message ? error.message : t("pages.images.jobs.cancelError", "Could not cancel the job.");
					toast.error(message);
				},
				onSettled: () => setCancellingJobId(null),
			});
		},
		[cancelMutation, t],
	);

	return (
		<Stack gap="lg" px="md" py="lg">
			<Group justify="space-between" align="flex-start">
				<Stack gap={4}>
					<Text size="sm" tt="uppercase" fw={700} c="dimmed">
						{t("pages.images.eyebrow", "Worker Node")}
					</Text>
					<Group gap="xs" align="center">
						<IconPhoto size={24} />
						<Title order={2}>{t("pages.images.title", "Image Generation")}</Title>
					</Group>
					<Text c="dimmed">
						{t(
							"pages.images.subtitle",
							"Generate images locally with stable-diffusion.cpp. Jobs run one at a time and stream coarse status as they progress.",
						)}
					</Text>
				</Stack>
			</Group>

			<SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
				<Stack gap="lg">
					<Card withBorder={true} padding="lg" radius="md">
						<ImageGenerationForm
							models={models}
							isSubmitting={createMutation.isPending}
							submitError={submitError}
							onSubmit={handleGenerate}
						/>
					</Card>
					<Card withBorder={true} padding="lg" radius="md">
						<ImageModelManager
							models={models}
							isLoading={modelsQuery.isLoading}
							onPendingDownloadChange={setModelDownloadPending}
						/>
					</Card>
				</Stack>
				<Stack gap="sm">
					<Text fw={600}>{t("pages.images.jobs.title", "Jobs")}</Text>
					<ImageJobList jobs={jobs} isLoading={jobsQuery.isLoading} cancellingJobId={cancellingJobId} onCancel={handleCancel} />
				</Stack>
			</SimpleGrid>
		</Stack>
	);
}
