import { ActionIcon, Alert, Badge, Card, Group, Loader, Stack, Text, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconSearch } from "@tabler/icons-react";
import { type FormEvent, useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { KnowledgeLastKnownGoodBadge } from "@/features/knowledge/components/KnowledgeLastKnownGoodBadge";
import type { KnowledgeDocument } from "@/features/knowledge/models/KnowledgeModels";
import { knowledgeErrorMessage } from "@/features/knowledge/queries/KnowledgeErrorMessage";
import type { UseKnowledgeSearchResult } from "@/features/knowledge/queries/useKnowledgeSearch";

interface KnowledgeSearchPanelProps {
	readonly search: UseKnowledgeSearchResult;
	readonly documents: readonly KnowledgeDocument[];
}

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
				<Alert color="red" icon={<IconAlertTriangle size={16} />}>
					{knowledgeErrorMessage(search.error, t("pages.knowledgeBase.search.error", "The search could not be completed."))}
				</Alert>
			) : null}

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
										<Badge variant="light" color="primary" style={{ flexShrink: 0 }}>
											{t("pages.knowledgeBase.search.score", "Score {{score}}", { score: hit.score.toFixed(2) })}
										</Badge>
									</Group>
								</Group>
								<Text size="sm" c="dimmed" lineClamp={3}>
									{hit.content}
								</Text>
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
