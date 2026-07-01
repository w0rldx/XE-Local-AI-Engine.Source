import { ActionIcon, Alert, Badge, Card, Group, Loader, Stack, Text, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconSearch } from "@tabler/icons-react";
import { type FormEvent, useCallback, useState } from "react";
import { useTranslation } from "react-i18next";

import { knowledgeErrorMessage } from "@/features/knowledge/queries/KnowledgeErrorMessage";
import type { UseKnowledgeSearchResult } from "@/features/knowledge/queries/useKnowledgeSearch";

interface KnowledgeSearchPanelProps {
	readonly search: UseKnowledgeSearchResult;
}

// Semantic-search probe: a query box + ranked hit list (title · section · score · snippet). Distinguishes the
// three empty states — nothing searched yet, an in-flight search, and a resolved search with no hits. The parent
// owns the search hook (useKnowledgeSearch); this panel is presentational over its result bag.
export function KnowledgeSearchPanel({ search }: KnowledgeSearchPanelProps) {
	const { t } = useTranslation();
	const [query, setQuery] = useState("");

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
										<Text fw={600} truncate="end">
											{hit.title}
										</Text>
										{hit.section ? (
											<Text size="xs" c="dimmed" truncate="end">
												{hit.section}
											</Text>
										) : null}
									</Stack>
									<Badge variant="light" color="primary" style={{ flexShrink: 0 }}>
										{t("pages.knowledgeBase.search.score", "Score {{score}}", { score: hit.score.toFixed(2) })}
									</Badge>
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
