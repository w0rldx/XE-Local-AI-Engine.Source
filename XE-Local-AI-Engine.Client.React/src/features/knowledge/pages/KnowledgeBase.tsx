import { Alert, Button, Card, Container, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconAlertTriangle, IconDatabase, IconRefresh, IconRefreshAlert } from "@tabler/icons-react";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { KnowledgeDocumentDrawer } from "@/features/knowledge/components/KnowledgeDocumentDrawer";
import { KnowledgeDocumentsTable } from "@/features/knowledge/components/KnowledgeDocumentsTable";
import { KnowledgeSearchPanel } from "@/features/knowledge/components/KnowledgeSearchPanel";
import { KnowledgeUploadPanel } from "@/features/knowledge/components/KnowledgeUploadPanel";
import { useKnowledgeBaseHub } from "@/features/knowledge/hooks/useKnowledgeBaseHub";
import type { KnowledgeDocument } from "@/features/knowledge/models/KnowledgeModels";
import { knowledgeErrorMessage } from "@/features/knowledge/queries/KnowledgeErrorMessage";
import {
	useDeleteKnowledgeDocument,
	useKnowledgeDocumentDetail,
	useKnowledgeDocuments,
	useReindexKnowledgeCorpus,
	useReindexKnowledgeDocument,
} from "@/features/knowledge/queries/useKnowledgeDocuments";
import { useKnowledgeSearch } from "@/features/knowledge/queries/useKnowledgeSearch";
import { useKnowledgeUpload } from "@/features/knowledge/queries/useKnowledgeUpload";

/* eslint-disable react-doctor/no-event-handler, react-doctor/no-chain-state-updates -- Mutation callbacks must coordinate the drawer selection and server-result notifications after each user action. */

// Knowledge-base management surface: ingest documents (drag-drop upload), watch them move through the
// extract→chunk→embed→index pipeline (live via the SignalR hub), search the indexed corpus, inspect a document's
// chunks, reindex (single or all-stale), and delete. Server state flows through TanStack Query; the hub layers live
// invalidation so indexing transitions refresh the table without a manual reload. Mirrors the Model-management
// page's structure (eyebrow + title + refresh header, bordered section cards).
export function KnowledgeBase() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	// Live indexing-status invalidation for the lifetime of this page.
	useKnowledgeBaseHub();

	const {
		data: documents,
		isLoading,
		error,
		refetch,
		isFetching,
	} = useKnowledgeDocuments();
	const documentList = useMemo<readonly KnowledgeDocument[]>(() => documents ?? [], [documents]);
	const hasStaleDocuments = useMemo(() => documentList.some((document) => document.staleModel), [documentList]);

	const upload = useKnowledgeUpload();
	const search = useKnowledgeSearch();

	// The document whose detail drawer is open (also the only document whose detail endpoint is fetched).
	const [detailDocumentId, setDetailDocumentId] = useState<string | undefined>();
	const [drawerOpened, { open: openDrawer, close: closeDrawer }] = useDisclosure(false);
	const { data: detail, isFetching: detailIsFetching } = useKnowledgeDocumentDetail(detailDocumentId ?? "", drawerOpened);

	const deleteMutation = useDeleteKnowledgeDocument({
		onSuccess: () => {
			toast.success(t("pages.knowledgeBase.delete.success", "Document removed from the knowledge base."));
			closeDrawer();
			setDetailDocumentId(undefined);
		},
		onError: (mutationError) =>
			toast.error(knowledgeErrorMessage(mutationError, t("pages.knowledgeBase.delete.error", "Failed to delete the document."))),
	});

	const reindexMutation = useReindexKnowledgeDocument({
		onSuccess: () => toast.success(t("pages.knowledgeBase.reindex.success", "Reindexing started.")),
		onError: (mutationError) =>
			toast.error(knowledgeErrorMessage(mutationError, t("pages.knowledgeBase.reindex.error", "Failed to start reindexing."))),
	});

	const reindexCorpusMutation = useReindexKnowledgeCorpus({
		onSuccess: (enqueuedCount) =>
			toast.success(
				t("pages.knowledgeBase.reindexCorpus.success", "Reindexing {{count}} document(s).", { count: enqueuedCount }),
			),
		onError: (mutationError) =>
			toast.error(
				knowledgeErrorMessage(mutationError, t("pages.knowledgeBase.reindexCorpus.error", "Failed to start reindexing.")),
			),
	});

	const isActionPending = deleteMutation.isPending || reindexMutation.isPending || reindexCorpusMutation.isPending;

	const openDetail = useCallback(
		(documentId: string): void => {
			setDetailDocumentId(documentId);
			openDrawer();
		},
		[openDrawer],
	);

	const handleReindex = useCallback(
		(documentId: string): void => {
			reindexMutation.mutate({ path: { documentId } });
		},
		[reindexMutation],
	);

	const confirmDelete = useCallback(
		async (document: KnowledgeDocument): Promise<void> => {
			const confirmed = await confirm({
				title: t("pages.knowledgeBase.delete.title", "Delete document"),
				description: t(
					"pages.knowledgeBase.delete.description",
					"Delete '{{name}}' and its indexed chunks from the knowledge base? This cannot be undone.",
					{ name: document.displayName },
				),
				confirmationText: t("common.delete", "Delete"),
				cancellationText: t("common.cancel", "Cancel"),
			});
			if (confirmed) {
				deleteMutation.mutate({ path: { documentId: document.documentId } });
			}
		},
		[confirm, deleteMutation, t],
	);

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Group justify="space-between" align="flex-start">
					<Stack gap={4}>
						<Text size="sm" tt="uppercase" fw={700} c="dimmed">
							{t("common.workerNode", "Worker Node")}
						</Text>
						<Title order={2}>{t("pages.knowledgeBase.title", "Knowledge base")}</Title>
						<Text c="dimmed">
							{t("pages.knowledgeBase.subtitle", "Upload documents, index them, and search grounded knowledge.")}
						</Text>
					</Stack>
					<Group gap="sm">
						{hasStaleDocuments ? (
							<Button
								variant="light"
								color="orange"
								leftSection={<IconRefreshAlert size={16} />}
								onClick={() => reindexCorpusMutation.mutate({})}
								disabled={isActionPending}
								data-testid="knowledge-reindex-stale"
							>
								{t("pages.knowledgeBase.reindexCorpus.action", "Reindex stale")}
							</Button>
						) : null}
						<Button
							variant="subtle"
							leftSection={<IconRefresh size={16} />}
							onClick={() => refetch()}
							disabled={isFetching}
						>
							{t("common.refresh", "Refresh")}
						</Button>
					</Group>
				</Group>

				{error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{knowledgeErrorMessage(error, t("pages.knowledgeBase.loadError", "Could not load the knowledge base."))}
					</Alert>
				) : null}

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						<Title order={3}>{t("pages.knowledgeBase.upload.heading", "Add documents")}</Title>
						<KnowledgeUploadPanel pendingUploads={upload.pendingUploads} onUpload={upload.uploadFiles} />
					</Stack>
				</Card>

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						<Group justify="space-between">
							<Title order={3}>{t("pages.knowledgeBase.documents.heading", "Documents")}</Title>
							<IconDatabase size={22} />
						</Group>

						{isLoading ? (
							<Group gap="sm">
								<Loader size="sm" />
								<Text c="dimmed">{t("pages.knowledgeBase.documents.loading", "Loading documents…")}</Text>
							</Group>
						) : documentList.length === 0 ? (
							<Text c="dimmed">
								{t("pages.knowledgeBase.documents.empty", "No documents yet. Upload one above to get started.")}
							</Text>
						) : (
							<KnowledgeDocumentsTable
								documents={documentList}
								isActionPending={isActionPending}
								onOpenDetail={openDetail}
								onReindex={handleReindex}
								onDelete={confirmDelete}
							/>
						)}
					</Stack>
				</Card>

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						<Title order={3}>{t("pages.knowledgeBase.search.heading", "Search")}</Title>
						<KnowledgeSearchPanel search={search} documents={documentList} />
					</Stack>
				</Card>
			</Stack>

			<KnowledgeDocumentDrawer opened={drawerOpened} detail={detail} isLoading={detailIsFetching} onClose={closeDrawer} />
		</Container>
	);
}
