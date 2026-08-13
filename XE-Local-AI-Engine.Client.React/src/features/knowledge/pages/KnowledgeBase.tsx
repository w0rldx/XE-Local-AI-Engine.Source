import { Alert, Badge, Button, Card, Container, Divider, Group, Loader, Stack, Text, TextInput, Title } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconAlertTriangle, IconDatabase, IconRefresh, IconRefreshAlert } from "@tabler/icons-react";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { KnowledgeDocumentDrawer } from "@/features/knowledge/components/KnowledgeDocumentDrawer";
import { KnowledgeDocumentsTable } from "@/features/knowledge/components/KnowledgeDocumentsTable";
import { KnowledgeRepositoryImportPanel } from "@/features/knowledge/components/KnowledgeRepositoryImportPanel";
import { KnowledgeSearchPanel } from "@/features/knowledge/components/KnowledgeSearchPanel";
import { KnowledgeUploadPanel } from "@/features/knowledge/components/KnowledgeUploadPanel";
import { useDevelopmentRepositories } from "@/features/development/queries/useDevelopment";
import { useKnowledgeBaseHub } from "@/features/knowledge/hooks/useKnowledgeBaseHub";
import {
	KNOWLEDGE_DEFAULT_COLLECTION_ID,
	type KnowledgeDocument,
	normalizeKnowledgeCollectionId,
} from "@/features/knowledge/models/KnowledgeModels";
import { knowledgeErrorMessage } from "@/features/knowledge/queries/KnowledgeErrorMessage";
import {
	useDeleteKnowledgeDocument,
	useKnowledgeDocumentDetail,
	useKnowledgeDocuments,
	useReindexKnowledgeCorpus,
	useReindexKnowledgeDocument,
} from "@/features/knowledge/queries/useKnowledgeDocuments";
import { useKnowledgeRepositoryImport } from "@/features/knowledge/queries/useKnowledgeRepositoryImport";
import { useKnowledgeSearch } from "@/features/knowledge/queries/useKnowledgeSearch";
import { useKnowledgeUpload } from "@/features/knowledge/queries/useKnowledgeUpload";
import { TutorialInvitation } from "@/features/onboarding/components/TutorialInvitation";

/* eslint-disable react-doctor/no-event-handler, react-doctor/no-chain-state-updates -- Mutation callbacks must coordinate the drawer selection and server-result notifications after each user action. */

// Knowledge-base management surface: ingest documents (drag-drop upload), watch them move through the
// extract→chunk→embed→index pipeline (live via the SignalR hub), search the indexed corpus, inspect a document's
// chunks, reindex (single or all-stale), and delete. Server state flows through TanStack Query; the hub layers live
// invalidation so indexing transitions refresh the table without a manual reload. Mirrors the Model-management
// page's structure (eyebrow + title + refresh header, bordered section cards).
export function KnowledgeBase() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const [collectionId, setCollectionId] = useState(KNOWLEDGE_DEFAULT_COLLECTION_ID);
	const [collectionDraft, setCollectionDraft] = useState(KNOWLEDGE_DEFAULT_COLLECTION_ID);
	const normalizedCollectionDraft = normalizeKnowledgeCollectionId(collectionDraft);

	// Live indexing-status invalidation for the lifetime of this page.
	useKnowledgeBaseHub();

	const {
		data: documents,
		isLoading,
		error,
		refetch,
		isFetching,
	} = useKnowledgeDocuments(true, collectionId);
	const documentList = useMemo<readonly KnowledgeDocument[]>(() => documents ?? [], [documents]);
	const hasStaleDocuments = useMemo(() => documentList.some((document) => document.staleModel), [documentList]);

	const upload = useKnowledgeUpload(collectionId);
	const search = useKnowledgeSearch(collectionId);
	const repositoriesQuery = useDevelopmentRepositories();

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

	const repositoryImportMutation = useKnowledgeRepositoryImport({
		onSuccess: (result) => {
			const message = t(
				"pages.knowledgeBase.repository.success",
				"Repository import queued {{count}} document(s): {{added}} added, {{updated}} updated, {{removed}} removed; {{deduplicated}} unchanged document(s) were reused.",
				{
					count: result.enqueuedDocuments,
					added: result.addedDocuments,
					updated: result.updatedDocuments,
					removed: result.removedDocuments,
					deduplicated: result.deduplicatedDocuments,
				},
			);
			if (result.queueCapacityReached) {
				toast.warning(`${message} ${t("pages.knowledgeBase.repository.queueFull", "The indexing queue is full; retry to continue.")}`);
			} else {
				toast.success(message);
			}
		},
		onError: (mutationError) =>
			toast.error(
				knowledgeErrorMessage(
					mutationError,
					t("pages.knowledgeBase.repository.error", "Failed to import the repository."),
				),
			),
	});

	const isActionPending =
		deleteMutation.isPending || reindexMutation.isPending || reindexCorpusMutation.isPending || repositoryImportMutation.isPending;

	const applyCollection = useCallback((): void => {
		if (!normalizedCollectionDraft || normalizedCollectionDraft === collectionId) {
			return;
		}
		setCollectionId(normalizedCollectionDraft);
		setCollectionDraft(normalizedCollectionDraft);
		search.reset();
		closeDrawer();
		setDetailDocumentId(undefined);
	}, [closeDrawer, collectionId, normalizedCollectionDraft, search]);

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

	const importRepository = useCallback(
		(selectedFolderId: string): void => {
			repositoryImportMutation.mutate({ body: { selectedFolderId, collectionId } });
		},
		[collectionId, repositoryImportMutation],
	);

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<TutorialInvitation tutorialId="knowledge-base-basics" />
				<Group justify="space-between" align="flex-start" data-tour="knowledge-overview">
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
					<Group align="flex-end" justify="space-between" wrap="wrap">
						<TextInput
							label={t("pages.knowledgeBase.collection.label", "Collection")}
							description={t(
								"pages.knowledgeBase.collection.description",
								"Uploads, repository imports, document lists, and searches stay inside this namespace.",
							)}
							value={collectionDraft}
							onChange={(event) => setCollectionDraft(event.currentTarget.value)}
							error={
								collectionDraft.trim().length > 0 && !normalizedCollectionDraft
									? t("pages.knowledgeBase.collection.invalid", "Use 1–128 letters, digits, dots, underscores, or hyphens.")
									: undefined
							}
							style={{ flex: "1 1 24rem" }}
							data-testid="knowledge-collection-input"
						/>
						<Group gap="sm">
							<Badge variant="light" size="lg" data-testid="knowledge-active-collection">
								{collectionId}
							</Badge>
							<Button
								variant="light"
								onClick={applyCollection}
								disabled={!normalizedCollectionDraft || normalizedCollectionDraft === collectionId}
							>
								{t("pages.knowledgeBase.collection.open", "Open collection")}
							</Button>
						</Group>
					</Group>
				</Card>

				<Card withBorder={true} radius="md" p="lg" data-tour="knowledge-upload">
					<Stack gap="md">
						<Title order={3}>{t("pages.knowledgeBase.upload.heading", "Add documents")}</Title>
						<KnowledgeUploadPanel pendingUploads={upload.pendingUploads} onUpload={upload.uploadFiles} />
						<Divider label={t("pages.knowledgeBase.repository.divider", "Or import a repository")} labelPosition="center" />
						<KnowledgeRepositoryImportPanel
							repositories={repositoriesQuery.data ?? []}
							isLoading={repositoriesQuery.isLoading}
							isImporting={repositoryImportMutation.isPending}
							onImport={importRepository}
						/>
					</Stack>
				</Card>

				<Card withBorder={true} radius="md" p="lg" data-tour="knowledge-documents">
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

				<Card withBorder={true} radius="md" p="lg" data-tour="knowledge-search">
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
