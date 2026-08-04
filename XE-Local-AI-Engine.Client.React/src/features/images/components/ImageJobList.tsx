import { Loader, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { TablePaginationFooter } from "@/core/ui/components/TablePagination/TablePaginationFooter";
import { useTablePagination } from "@/core/ui/components/TablePagination/useTablePagination";
import type { ImageJobView } from "@/features/images/models/ImageModels";
import { ImageJobCard } from "@/features/images/components/ImageJobCard";

interface ImageJobListProps {
	jobs: readonly ImageJobView[];
	isLoading: boolean;
	cancellingJobId: string | null;
	onCancel: (jobId: string) => void;
}

// Deliberately smaller than the app-wide default of 25: every succeeded card fetches, decrypts and then holds its own
// PNG, so a page of this list costs real network and memory rather than a few table rows.
const JobsPerPage = 10;

// The generation history / live queue. Server-state comes from TanStack Query (invalidated by the hub on each coarse
// status push) — nothing is mirrored into a store. Renders newest-first cards; an empty history shows a hint.
//
// Paginated because the history is unbounded and each succeeded card eagerly fetches its decrypted image, which
// useImageObjectUrl then caches for the session. Rendering every persisted job therefore downloaded, decrypted and
// retained the ENTIRE image history on every visit to this page, growing without limit as the node was used.
export function ImageJobList({ jobs, isLoading, cancellingJobId, onCancel }: ImageJobListProps) {
	const { t } = useTranslation();
	// Hook order must not depend on the early returns below, so pagination is computed before them.
	const pagination = useTablePagination(jobs, {
		initialPageSize: JobsPerPage,
		storageKey: "images.jobs",
	});

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
			{pagination.pageItems.map((job) => (
				<ImageJobCard key={job.id} job={job} isCancelling={cancellingJobId === job.id} onCancel={onCancel} />
			))}
			<TablePaginationFooter
				page={pagination.page}
				pageCount={pagination.pageCount}
				pageSize={pagination.pageSize}
				totalItems={pagination.totalItems}
				firstItemIndex={pagination.firstItemIndex}
				lastItemIndex={pagination.lastItemIndex}
				pageSizeOptions={pagination.pageSizeOptions}
				onPageChange={pagination.setPage}
				onPageSizeChange={pagination.setPageSize}
				data-testid="image-job-list-pagination"
			/>
		</Stack>
	);
}
