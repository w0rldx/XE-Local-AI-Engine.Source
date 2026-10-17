import { ActionIcon, Alert, Badge, Card, Group, Loader, Stack, Text, TextInput } from "@mantine/core";
import { IconInfoCircle, IconSearch } from "@tabler/icons-react";
import { type FormEvent, useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { KnowledgeScoreBadge } from "@/core/ui/components/KnowledgeScoreBadge/KnowledgeScoreBadge";
import { KnowledgeLastKnownGoodBadge } from "@/features/knowledge/components/KnowledgeLastKnownGoodBadge";
import type { KnowledgeDocument, KnowledgeSearchDegradation } from "@/features/knowledge/models/KnowledgeModels";
import { knowledgeErrorMessage } from "@/features/knowledge/queries/KnowledgeErrorMessage";
import type { UseKnowledgeSearchResult } from "@/features/knowledge/queries/useKnowledgeSearch";

interface KnowledgeSearchPanelProps {
	readonly search: UseKnowledgeSearchResult;
	readonly documents: readonly KnowledgeDocument[];
}

// One notice per degradation the last search ran under, so a cold reranker or a missing embedder never passes silently.
interface DegradationNotice {
	readonly key: string;
	readonly fallback: string;
}

const DEGRADATION_NOTICES: Record<KnowledgeSearchDegradation, DegradationNotice> = {
	LexicalOnly: {
		key: "pages.knowledgeBase.search.degradation.lexicalOnly",
		fallback: "The embedding model is unavailable, so these results come from keyword matching only.",
	},
	RerankerUnavailable: {
		key: "pages.knowledgeBase.search.degradation.rerankerUnavailable",
		fallback:
			"The reranker is not ready yet, so results are in their unreranked order. It starts in the background; search again shortly.",
	},
};

// Semantic-search probe: a query box + ranked hit list (title · section · score · snippet). Distinguishes the
// three empty states — nothing searched yet, an in-flight search, and a resolved search with no hits. The parent
// owns the search hook (useKnowledgeSearch) and the document list; this panel is presentational over both.
export function KnowledgeSearchPanel({ search, documents }: KnowledgeSearchPanelProps) {
	const { t } = useTranslation();
	const [query, setQuery] = useState("");

	// A hit's server-side `title` is deliberately non-sensitive: the original file name is encrypted at rest, so the
	// search endpoint falls back to the GUID storage reference whenever a chunk has no heading trail — which is the
	// common case for ordinary markdown, and unreadable for the operator. The already-loaded document list carries the
	// decrypted display name for the same authenticated viewer, so resolve the label here by documentId and leave the
	// API invariant untouched. Falls back to `hit.title` when the document is not in the list (still loading, deleted).
	const documentNames = useMemo(
		() => new Map(documents.map((document) => [document.documentId, document.displayName])),
		[documents],
	);

	const handleSubmit = useCallback(
		(event: FormEvent<HTMLFormElement>): void => {
			event.preventDefault();
			search.search(query);
		},
		[query, search],
	);

	return (
		<Stack gap="md">
			<form onSubmit={handleSubmit}>
				<TextInput
					value={query}
					onChange={(event) => setQuery(event.currentTarget.value)}
					placeholder={t("pages.knowledgeBase.search.placeholder", "Ask the knowledge base…")}
					aria-label={t("pages.knowledgeBase.search.aria", "Search the knowledge base")}
					leftSection={<IconSearch size={16} />}
					rightSection={
						search.isSearching ? (
							<Loader size={16} />
						) : (
							<ActionIcon
								type="submit"
								variant="subtle"
								aria-label={t("pages.knowledgeBase.search.submit", "Search")}
								disabled={query.trim().length === 0}
							>
								<IconSearch size={16} />
							</ActionIcon>
						)
					}
					data-testid="knowledge-search-input"
				/>
			</form>

			{search.error ? (
				<InlineErrorAlert
					message={knowledgeErrorMessage(
						search.error,
						t("pages.knowledgeBase.search.error", "The search could not be completed."),
					)}
				/>
			) : null}

			{!search.isSearching
				? search.degradations.map((degradation) => {
						// A value newer than this bundle has no notice text; skip it rather than throw while rendering.
						const notice = (DEGRADATION_NOTICES as Partial<Record<string, DegradationNotice>>)[degradation];
						return notice ? (
							<Alert
								key={degradation}
								color="yellow"
								variant="light"
								icon={<IconInfoCircle size={16} />}
								data-testid={`knowledge-search-degradation-${degradation}`}
							>
								{t(notice.key, notice.fallback)}
							</Alert>
						) : null;
					})
				: null}

			{search.results.length > 0 ? (
				<Stack gap="sm" data-testid="knowledge-search-results">
					{search.results.map((hit) => (
						<Card key={hit.chunkId} withBorder={true} radius="sm" p="md">
							<Stack gap={6}>
								<Group justify="space-between" wrap="nowrap" align="flex-start" gap="sm">
									<Stack gap={0} style={{ minWidth: 0 }}>
										<Text fw={600} truncate="end" data-testid="knowledge-search-hit-title">
											{documentNames.get(hit.documentId) ?? hit.title}
										</Text>
										{hit.section ? (
											<Text size="xs" c="dimmed" truncate="end">
												{hit.section}
											</Text>
										) : null}
									</Stack>
									<Group gap="xs" wrap="nowrap" style={{ flexShrink: 0 }}>
										{hit.servingLastKnownGood ? <KnowledgeLastKnownGoodBadge /> : null}
										<KnowledgeScoreBadge score={hit.score} scoreKind={hit.scoreKind} />
									</Group>
								</Group>
								<Text size="sm" c="dimmed" lineClamp={3}>
									{hit.content}
								</Text>
								<Group gap="xs">
									{hit.sourcePath ? (
										<Text size="xs" c="dimmed">
											{hit.sourcePath}
										</Text>
									) : null}
									{hit.symbol ? (
										<Badge variant="outline" color="violet" size="xs">
											{hit.symbol}
										</Badge>
									) : null}
									{hit.pageNumber ? (
										<Text size="xs" c="dimmed">
											{t("pages.knowledgeBase.detail.page", "Page {{page}}", { page: hit.pageNumber })}
										</Text>
									) : null}
								</Group>
							</Stack>
						</Card>
					))}
				</Stack>
			) : null}

			{search.hasSearched && !search.isSearching && search.results.length === 0 && !search.error ? (
				<Text c="dimmed" ta="center" py="md" data-testid="knowledge-search-empty">
					{t("pages.knowledgeBase.search.noResults", 'No indexed content matches "{{query}}".', {
						query: search.lastQuery,
					})}
				</Text>
			) : null}

			{!search.hasSearched && !search.isSearching ? (
				<Text c="dimmed" ta="center" py="md">
					{t("pages.knowledgeBase.search.hint", "Search runs over every indexed document in this knowledge base.")}
				</Text>
			) : null}
		</Stack>
	);
}
