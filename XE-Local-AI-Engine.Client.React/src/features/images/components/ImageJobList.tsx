import { Loader, Stack } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { TablePaginationFooter } from "@/core/ui/components/TablePagination/TablePaginationFooter";
import { useServerTablePagination } from "@/core/ui/components/TablePagination/useTablePagination";
import { ImageJobCard } from "@/features/images/components/ImageJobCard";
import { type ImageJobView, imageJobPageSizeOptions } from "@/features/images/models/ImageModels";

interface ImageJobListProps {
	/** The CURRENT page of jobs, as the server ordered them. Never re-sorted or re-sliced here. */
	jobs: readonly ImageJobView[];
	/** Jobs the node holds in total, which is what the footer numbers the pages from. */
	totalCount: number;
	page: number;
	pageSize: number;
	isLoading: boolean;
	cancellingJobId: string | null;
	onCancel: (jobId: string) => void;
	onPageChange: (page: number) => void;
	onPageSizeChange: (pageSize: number) => void;
}

// The generation history / live queue. Server-state comes from TanStack Query (invalidated by the hub on each coarse
// status push) — nothing is mirrored into a store. Renders newest-first cards; an empty history shows a hint.
//
// Paged BY THE SERVER. It used to fetch every job and paginate in the browser, which bounded what was rendered but not
// what was sent: every row carries a decrypted prompt, so the payload grew without limit as the node was used. The
// footer's limit/offset now ride with the request, and `totalCount` is the node's own figure.
export function ImageJobList({
	jobs,
	totalCount,
	page,
	pageSize,
	isLoading,
	cancellingJobId,
	onCancel,
	onPageChange,
	onPageSizeChange,
}: ImageJobListProps) {
	const { t } = useTranslation();
	// Hook order must not depend on the early returns below, so pagination is computed before them. This is also what
	// recovers from deleting the last row of the last page: the total shrinks, the active page no longer exists, and
	// the hook asks for the last one that does rather than leaving the operator on an empty page.
	const pagination = useServerTablePagination({
		page,
		pageSize,
		totalItems: totalCount,
		pageSizeOptions: imageJobPageSizeOptions,
		onPageChange,
		onPageSizeChange,
	});

	if (isLoading) {
		return <Loader data-testid="image-job-list-loading" />;
	}

	if (totalCount === 0) {
		return (
			<EmptyState
				message={t("pages.images.jobs.empty", "No image jobs yet. Generate one to see it here.")}
				data-testid="image-job-list-empty"
			/>
		);
	}

	return (
		<Stack gap="sm" data-testid="image-job-list">
			{jobs.map((job) => (
				<ImageJobCard key={job.id} job={job} isCancelling={cancellingJobId === job.id} onCancel={onCancel} />
			))}
			<TablePaginationFooter {...pagination} data-testid="image-job-list-pagination" />
		</Stack>
	);
}
