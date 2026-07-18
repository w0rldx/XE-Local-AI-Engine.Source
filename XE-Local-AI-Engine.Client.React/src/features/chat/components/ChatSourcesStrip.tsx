import { Badge, Collapse, Group, Paper, Stack, Text, UnstyledButton } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconBooks, IconChevronRight } from "@tabler/icons-react";
import { memo, useCallback, useState } from "react";
import { useTranslation } from "react-i18next";

import type { ChatMessageSource } from "@/features/chat/models/ChatModels";
import { KnowledgeDocumentDrawer } from "@/features/knowledge/components/KnowledgeDocumentDrawer";
import { useKnowledgeDocumentDetail } from "@/features/knowledge/queries/useKnowledgeDocuments";

interface ChatSourcesStripProps {
	sources: readonly ChatMessageSource[];
}

// Formats a fused/rerank relevance score for display: a compact 2-decimal figure. The score is monotonic (higher is
// more relevant) but not a probability, so it is labeled as a relevance score rather than a percentage.
function formatScore(score: number): string {
	return Number.isFinite(score) ? score.toFixed(2) : "";
}

/**
 * A collapsible "Sources" strip rendered under a grounded assistant turn (UX-04). Lists the knowledge-base excerpts
 * that were inlined into the turn (document title + heading section + relevance score), reusing the knowledge-hit card
 * shape. Collapsed by default so it never crowds the answer; the header shows the count. Renders nothing when there are
 * no sources, so a non-grounded turn stays visually unchanged.
 *
 * UX-06: each source card is a button that opens the shared knowledge document drawer for that source's document,
 * highlighting the chunk whose heading section matches the source. Exact chunk-id scroll is intentionally deferred —
 * the chunk view exposes chunkIndex, not chunkId, so id matching would need a backend DTO change.
 */
export const ChatSourcesStrip = memo(function ChatSourcesStrip({ sources }: ChatSourcesStripProps) {
	const { t } = useTranslation();
	const [opened, { toggle }] = useDisclosure(false);

	// The source whose document drawer is open (also the only document whose detail endpoint is fetched).
	const [activeSource, setActiveSource] = useState<ChatMessageSource | undefined>();
	const [drawerOpened, { open: openDrawer, close: closeDrawer }] = useDisclosure(false);
	const { data: detail, isFetching: detailIsFetching } = useKnowledgeDocumentDetail(
		activeSource?.documentId ?? "",
		drawerOpened,
	);

	const openSource = useCallback(
		(source: ChatMessageSource): void => {
			setActiveSource(source);
			openDrawer();
		},
		[openDrawer],
	);

	if (sources.length === 0) {
		return null;
	}

	return (
		<Stack gap={4} data-testid="chat-sources-strip">
			<UnstyledButton onClick={toggle} aria-expanded={opened} data-testid="chat-sources-toggle">
				<Group gap={6} align="center">
					<IconChevronRight
						size={14}
						style={{ transform: opened ? "rotate(90deg)" : "none", transition: "transform 150ms ease" }}
					/>
					<IconBooks size={14} />
					<Text size="xs" c="dimmed" fw={500}>
						{t("pages.chat.sources.title", "Sources")}
					</Text>
					<Badge size="xs" variant="light" color="gray">
						{sources.length}
					</Badge>
				</Group>
			</UnstyledButton>
			<Collapse expanded={opened}>
				<Stack gap={4}>
					{sources.map((source) => (
						<UnstyledButton
							key={source.chunkId || `${source.documentId}:${source.title}`}
							onClick={() => openSource(source)}
							aria-label={t("pages.chat.sources.open", "Open source: {{title}}", { title: source.title })}
							data-testid="chat-source-card"
							style={{ width: "100%", display: "block" }}
						>
							<Paper withBorder={true} p="xs" radius="sm">
								<Group gap={8} justify="space-between" align="flex-start" wrap="nowrap">
									<Stack gap={2} style={{ minWidth: 0 }}>
										<Text size="xs" fw={500} ta="left" style={{ overflowWrap: "anywhere" }}>
											{source.title}
										</Text>
										{source.section ? (
											<Text size="xs" c="dimmed" ta="left" style={{ overflowWrap: "anywhere" }}>
												{source.section}
											</Text>
										) : null}
									</Stack>
									<Badge size="xs" variant="light" color="primary" style={{ flexShrink: 0 }}>
										{t("pages.chat.sources.score", "score {{value}}", { value: formatScore(source.score) })}
									</Badge>
								</Group>
							</Paper>
						</UnstyledButton>
					))}
				</Stack>
			</Collapse>

			<KnowledgeDocumentDrawer
				opened={drawerOpened}
				detail={detail}
				isLoading={detailIsFetching}
				highlightSection={activeSource?.section}
				onClose={closeDrawer}
			/>
		</Stack>
	);
});
