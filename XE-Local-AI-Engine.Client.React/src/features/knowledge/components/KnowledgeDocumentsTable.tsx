import { ActionIcon, Badge, Group, Stack, Table, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconEye, IconRefresh, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { TablePaginationFooter } from "@/core/ui/components/TablePagination/TablePaginationFooter";
import { useTablePagination } from "@/core/ui/components/TablePagination/useTablePagination";
import { KnowledgeLastKnownGoodBadge } from "@/features/knowledge/components/KnowledgeLastKnownGoodBadge";
import { KnowledgeStatusBadge } from "@/features/knowledge/components/KnowledgeStatusBadge";
import { type KnowledgeDocument, formatKnowledgeBytes, formatKnowledgeTimestamp } from "@/features/knowledge/models/KnowledgeModels";

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

	return (
		<Stack gap="sm">
			<Table.ScrollContainer minWidth={860}>
				<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="knowledge-documents-table">
					<Table.Thead>
						<Table.Tr>
							<Table.Th>{t("pages.knowledgeBase.table.name", "Name")}</Table.Th>
							<Table.Th>{t("pages.knowledgeBase.table.status", "Status")}</Table.Th>
							<Table.Th>{t("pages.knowledgeBase.table.chunks", "Chunks")}</Table.Th>
							<Table.Th>{t("pages.knowledgeBase.table.embeddingModel", "Embedding model")}</Table.Th>
							<Table.Th>{t("pages.knowledgeBase.table.size", "Size")}</Table.Th>
							<Table.Th>{t("pages.knowledgeBase.table.created", "Created")}</Table.Th>
							<Table.Th>{t("pages.knowledgeBase.table.actions", "Actions")}</Table.Th>
						</Table.Tr>
					</Table.Thead>
					<Table.Tbody>
						{pagination.pageItems.map((document) => (
							<Table.Tr key={document.documentId} data-testid={`knowledge-row-${document.documentId}`}>
								<Table.Td>
									<Stack gap={2} style={{ minWidth: 0 }}>
										<Text fw={500} truncate="end">
											{document.displayName}
										</Text>
										{document.status === "Failed" && document.failureReason ? (
											<Group gap={4} wrap="nowrap" c="red">
												<IconAlertTriangle size={13} />
												<Text size="xs" c="red" truncate="end">
													{document.failureReason}
												</Text>
											</Group>
										) : null}
									</Stack>
								</Table.Td>
								<Table.Td>
									<Group gap={6} wrap="nowrap">
										<KnowledgeStatusBadge status={document.status} />
										{document.status !== "Indexed" && document.chunkCount > 0 ? <KnowledgeLastKnownGoodBadge /> : null}
									</Group>
								</Table.Td>
								<Table.Td>{document.chunkCount}</Table.Td>
								<Table.Td>
									<Group gap={6} wrap="nowrap">
										<Text size="sm">{document.embeddingModel || "—"}</Text>
										{document.staleModel ? (
											<Tooltip
												label={t(
													"pages.knowledgeBase.table.staleTooltip",
													"Embedded with an older model — reindex to refresh.",
												)}
												withArrow={true}
											>
												<Badge color="orange" variant="light" size="xs">
													{t("pages.knowledgeBase.table.stale", "Stale")}
												</Badge>
											</Tooltip>
										) : null}
									</Group>
								</Table.Td>
								<Table.Td>{formatKnowledgeBytes(document.sizeBytes)}</Table.Td>
								<Table.Td>{formatKnowledgeTimestamp(document.createdAtUtc)}</Table.Td>
								<Table.Td>
									<Group gap="xs" wrap="nowrap">
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
									</Group>
								</Table.Td>
							</Table.Tr>
						))}
					</Table.Tbody>
				</Table>
			</Table.ScrollContainer>

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
