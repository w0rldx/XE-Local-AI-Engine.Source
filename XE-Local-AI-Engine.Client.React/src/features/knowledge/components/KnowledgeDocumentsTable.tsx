import { ActionIcon, Badge, Group, Stack, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconEye, IconRefresh, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { ResponsiveTable, type ResponsiveTableColumn } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
import { TablePaginationFooter } from "@/core/ui/components/TablePagination/TablePaginationFooter";
import { useTablePagination } from "@/core/ui/components/TablePagination/useTablePagination";
import { KnowledgeLastKnownGoodBadge } from "@/features/knowledge/components/KnowledgeLastKnownGoodBadge";
import { KnowledgeStatusBadge } from "@/features/knowledge/components/KnowledgeStatusBadge";
import {
	formatKnowledgeBytes,
	formatKnowledgeTimestamp,
	type KnowledgeDocument,
	knowledgeFailureReasonKey,
} from "@/features/knowledge/models/KnowledgeModels";

interface KnowledgeDocumentsTableProps {
	readonly documents: readonly KnowledgeDocument[];
	readonly isActionPending: boolean;
	onOpenDetail(documentId: string): void;
	onReindex(documentId: string): void;
	onDelete(document: KnowledgeDocument): void;
}

// Persisted page-size preference (its own key so the KB table remembers a separate size from other tables).
const KNOWLEDGE_PAGE_SIZE_STORAGE_KEY = "knowledge-documents";

// Paginated documents table: name (+ failure reason inline for failed rows), status pill, chunk count, embedding
// model (+ a "stale — reindex" badge when the embedding model has moved on), size, created, and row actions
// (view / reindex / delete). Client-side pagination over the already-loaded list via useTablePagination.
export function KnowledgeDocumentsTable({
	documents,
	isActionPending,
	onOpenDetail,
	onReindex,
	onDelete,
}: KnowledgeDocumentsTableProps) {
	const { t } = useTranslation();
	const pagination = useTablePagination(documents, { storageKey: KNOWLEDGE_PAGE_SIZE_STORAGE_KEY });

	const columns: readonly ResponsiveTableColumn<KnowledgeDocument>[] = [
		{
			key: "name",
			header: t("pages.knowledgeBase.table.name", "Name"),
			primary: true,
			cell: (document) => (
				<Stack gap={2} style={{ minWidth: 0 }}>
					<Text fw={500} truncate="end">
						{document.displayName}
					</Text>
					{document.status === "Failed" && document.failureReason ? (
						<Group gap={4} wrap="nowrap" c="red">
							<IconAlertTriangle size={13} />
							<Text size="xs" c="red" truncate="end" data-testid={`knowledge-failure-${document.documentId}`}>
								{document.failureReasonCode
									? t(knowledgeFailureReasonKey(document.failureReasonCode), { defaultValue: document.failureReason })
									: document.failureReason}
							</Text>
						</Group>
					) : null}
				</Stack>
			),
		},
		{
			key: "status",
			header: t("pages.knowledgeBase.table.status", "Status"),
			cell: (document) => (
				<Group gap={6} wrap="nowrap">
					<KnowledgeStatusBadge status={document.status} />
					{document.status !== "Indexed" && document.chunkCount > 0 ? <KnowledgeLastKnownGoodBadge /> : null}
				</Group>
			),
		},
		{
			key: "source",
			header: t("pages.knowledgeBase.table.source", "Source"),
			cell: (document) => (
				<Stack gap={1} style={{ minWidth: 0 }} data-testid={`knowledge-source-${document.documentId}`}>
					{document.sourcePath ? (
						<Text size="sm" truncate="end">
							{document.sourcePath}
						</Text>
					) : null}
					<Text size={document.sourcePath ? "xs" : "sm"} c={document.sourcePath ? "dimmed" : undefined}>
						{document.sourceKind}
					</Text>
				</Stack>
			),
		},
		{ key: "chunks", header: t("pages.knowledgeBase.table.chunks", "Chunks"), cell: (document) => document.chunkCount },
		{
			key: "embeddingModel",
			header: t("pages.knowledgeBase.table.embeddingModel", "Embedding model"),
			cell: (document) => (
				<Group gap={6} wrap="nowrap">
					{/* Only embedded chunks carry a model: an unembedded or failed row would otherwise show the configured default. */}
					<Text size="sm" data-testid={`knowledge-embedding-model-${document.documentId}`}>
						{document.chunkCount > 0 && document.embeddingModel ? document.embeddingModel : "—"}
					</Text>
					{document.chunkCount > 0 && document.staleModel ? (
						<Tooltip
							label={t(
								"pages.knowledgeBase.table.staleTooltip",
								"Indexed with an older model or parser version — reindex to refresh.",
							)}
							withArrow={true}
						>
							{/* The max-content floor stops the table's auto layout from clipping the badge to "ST…". */}
							<Badge color="orange" variant="light" size="xs" style={{ minWidth: "max-content" }}>
								{t("pages.knowledgeBase.table.stale", "Stale")}
							</Badge>
						</Tooltip>
					) : null}
				</Group>
			),
		},
		{
			key: "size",
			header: t("pages.knowledgeBase.table.size", "Size"),
			cell: (document) => formatKnowledgeBytes(document.sizeBytes),
		},
		{
			key: "created",
			header: t("pages.knowledgeBase.table.created", "Created"),
			cell: (document) => formatKnowledgeTimestamp(document.createdAtUtc),
		},
	];

	return (
		<Stack gap="sm">
			<ResponsiveTable
				rows={pagination.pageItems}
				columns={columns}
				rowKey={(document) => document.documentId}
				rowTestId={(document) => `knowledge-row-${document.documentId}`}
				minWidth={980}
				data-testid="knowledge-documents-table"
				actionsHeader={t("pages.knowledgeBase.table.actions", "Actions")}
				actions={(document) => (
					<>
						<Tooltip label={t("pages.knowledgeBase.actions.view", "View details")} withArrow={true}>
							<ActionIcon
								aria-label={t("pages.knowledgeBase.actions.viewAria", "View {{name}} details", {
									name: document.displayName,
								})}
								variant="subtle"
								onClick={() => onOpenDetail(document.documentId)}
								data-testid={`knowledge-view-${document.documentId}`}
							>
								<IconEye size={16} />
							</ActionIcon>
						</Tooltip>
						<Tooltip label={t("pages.knowledgeBase.actions.reindex", "Reindex")} withArrow={true}>
							<ActionIcon
								aria-label={t("pages.knowledgeBase.actions.reindexAria", "Reindex {{name}}", {
									name: document.displayName,
								})}
								variant="subtle"
								color="blue"
								disabled={isActionPending}
								onClick={() => onReindex(document.documentId)}
								data-testid={`knowledge-reindex-${document.documentId}`}
							>
								<IconRefresh size={16} />
							</ActionIcon>
						</Tooltip>
						<Tooltip label={t("pages.knowledgeBase.actions.delete", "Delete")} withArrow={true}>
							<ActionIcon
								aria-label={t("pages.knowledgeBase.actions.deleteAria", "Delete {{name}}", {
									name: document.displayName,
								})}
								variant="subtle"
								color="red"
								disabled={isActionPending}
								onClick={() => onDelete(document)}
								data-testid={`knowledge-delete-${document.documentId}`}
							>
								<IconTrash size={16} />
							</ActionIcon>
						</Tooltip>
					</>
				)}
			/>

			{documents.length > 0 ? (
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
					data-testid="knowledge-pagination"
				/>
			) : null}
		</Stack>
	);
}
