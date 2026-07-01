import { Loader, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { ImageJobView } from "@/features/images/models/ImageModels";
import { ImageJobCard } from "@/features/images/components/ImageJobCard";

interface ImageJobListProps {
	jobs: readonly ImageJobView[];
	isLoading: boolean;
	cancellingJobId: string | null;
	onCancel: (jobId: string) => void;
}

// The generation history / live queue. Server-state comes from TanStack Query (invalidated by the hub on each coarse
// status push) — nothing is mirrored into a store. Renders newest-first cards; an empty history shows a hint.
export function ImageJobList({ jobs, isLoading, cancellingJobId, onCancel }: ImageJobListProps) {
	const { t } = useTranslation();

	if (isLoading) {
		return <Loader data-testid="image-job-list-loading" />;
	}

	if (jobs.length === 0) {
		return (
			<Text c="dimmed" data-testid="image-job-list-empty">
				{t("pages.images.jobs.empty", "No image jobs yet. Generate one to see it here.")}
			</Text>
		);
	}

	return (
		<Stack gap="sm" data-testid="image-job-list">
			{jobs.map((job) => (
				<ImageJobCard key={job.id} job={job} isCancelling={cancellingJobId === job.id} onCancel={onCancel} />
			))}
		</Stack>
	);
}
