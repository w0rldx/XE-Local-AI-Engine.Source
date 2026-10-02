import { Loader, SimpleGrid, Stack, Text } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { TablePaginationFooter } from "@/core/ui/components/TablePagination/TablePaginationFooter";
import { useServerTablePagination } from "@/core/ui/components/TablePagination/useTablePagination";
import { UploadedImageCard } from "@/features/images/components/UploadedImageCard";
import { type ImageEditSource, imageJobPageSizeOptions, imageJobsPerPage } from "@/features/images/models/ImageModels";
import { useUploadedImages } from "@/features/images/queries/useImageQueries";

interface UploadedImageListProps {
	/** Opens the generation form on an upload; absent when no installed model can edit. */
	onEdit?: (source: ImageEditSource) => void;
}

/**
 * The operator's uploads, paged by the node like the job list (each card fetches and holds its own image, so the job
 * list's page sizes apply). The empty-state invitation is shown only once the list has loaded: a failed list request
 * must read as a failure, not as "you have no uploads yet".
 */
export function UploadedImageList({ onEdit }: UploadedImageListProps) {
	const { t } = useTranslation();
	const [page, setPage] = useState(1);
	const [pageSize, setPageSize] = useState(imageJobsPerPage);
	const uploadsQuery = useUploadedImages(pageSize, (page - 1) * pageSize);
	const totalCount = uploadsQuery.data?.totalCount ?? 0;
	// Before the early returns (hook order); also what moves off a page that a delete emptied.
	const pagination = useServerTablePagination({
		page,
		pageSize,
		totalItems: totalCount,
		pageSizeOptions: imageJobPageSizeOptions,
		onPageChange: setPage,
		onPageSizeChange: (next) => {
			setPageSize(next);
			setPage(1);
		},
	});

	if (uploadsQuery.isLoading) {
		return <Loader size="sm" data-testid="uploaded-images-loading" />;
	}
	if (uploadsQuery.isError) {
		return (
			<InlineErrorAlert
				data-testid="uploaded-images-error"
				message={apiErrorMessage(uploadsQuery.error, t("pages.images.uploads.listError", "Could not load the uploaded images."))}
			/>
		);
	}
	if (totalCount === 0) {
		return (
			<Text size="sm" c="dimmed" data-testid="uploaded-images-empty">
				{t("pages.images.uploads.empty", "Upload a PNG or JPEG to start an edit from your own picture.")}
			</Text>
		);
	}
	return (
		<Stack gap="sm">
			<SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm" data-testid="uploaded-images">
				{(uploadsQuery.data?.items ?? []).map((image) => (
					<UploadedImageCard key={image.imageId} image={image} onEdit={onEdit} />
				))}
			</SimpleGrid>
			<TablePaginationFooter {...pagination} data-testid="uploaded-images-pagination" />
		</Stack>
	);
}
