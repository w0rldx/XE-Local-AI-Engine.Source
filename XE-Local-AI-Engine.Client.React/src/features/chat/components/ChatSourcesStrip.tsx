import { Badge, Collapse, Group, Paper, Stack, Text, UnstyledButton } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconBooks, IconChevronRight } from "@tabler/icons-react";
import { memo, useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { KnowledgeScoreBadge } from "@/core/ui/components/KnowledgeScoreBadge/KnowledgeScoreBadge";
import type { ChatMessageSource } from "@/features/chat/models/ChatModels";
import { KnowledgeDocumentDrawer } from "@/features/knowledge/components/KnowledgeDocumentDrawer";
import { useKnowledgeDocumentDetail, useKnowledgeDocuments } from "@/features/knowledge/queries/useKnowledgeDocuments";

interface ChatSourcesStripProps {
	sources: readonly ChatMessageSource[];
}

// Formats a legacy source's score (persisted before its kind was recorded, so its scale is unknown): a compact
// 2-decimal figure, labeled neutrally. Sources that carry a kind render KnowledgeScoreBadge instead.
function formatScore(score: number): string {
	return Number.isFinite(score) ? score.toFixed(2) : "";
}

/**
 * A collapsible "Sources" strip rendered under a grounded assistant turn. Lists the knowledge-base excerpts
 * that were inlined into the turn (document title + heading section + score kind), reusing the knowledge-hit card
 * shape. Collapsed by default so it never crowds the answer; the header shows the count. Renders nothing when there are
 * no sources, so a non-grounded turn stays visually unchanged.
 *
 * Each source card is a button that opens the shared knowledge document drawer for that source's document,
 * highlighting the chunk whose heading section matches the source. Exact chunk-id scroll is intentionally deferred —
 * the chunk view exposes chunkIndex, not chunkId, so id matching would need a backend DTO change.
 */
export const ChatSourcesStrip = memo(function ChatSourcesStrip({ sources }: ChatSourcesStripProps) {
	const { t } = useTranslation();
	const [opened, { toggle }] = useDisclosure(false);

	// The source whose document drawer is open (also the only document whose detail endpoint is fetched).
	const [activeSource, setActiveSource] = useState<ChatMessageSource | undefined>();
	const [drawerOpened, { open: openDrawer, close: closeDrawer }] = useDisclosure(false);
	const { data: detail, isFetching: detailIsFetching } = useKnowledgeDocumentDetail(activeSource?.documentId ?? "", drawerOpened);

	// A source's server title is the non-sensitive storage reference (a GUID file name) when its chunk has no heading
	// trail; label it with the decrypted display name the operator's own document list carries, as the KB page does.
	const { data: documents } = useKnowledgeDocuments(sources.length > 0);
	const documentNames = useMemo(
		() => new Map((documents ?? []).map((document) => [document.documentId, document.displayName])),
		[documents],
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
			{/* 24px tall so the strip's only control is still a fingertip-sized target on a phone. */}
			<UnstyledButton
				onClick={toggle}
				aria-expanded={opened}
				style={{ display: "flex", alignItems: "center", minHeight: 24 }}
				data-testid="chat-sources-toggle"
			>
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
							aria-label={t("pages.chat.sources.open", "Open source: {{title}}", {
								title: documentNames.get(source.documentId) ?? source.title,
							})}
							data-testid="chat-source-card"
							style={{ width: "100%", display: "block" }}
						>
							<Paper withBorder={true} p="xs" radius="sm">
								<Group gap={8} justify="space-between" align="flex-start" wrap="nowrap">
									<Stack gap={2} style={{ minWidth: 0 }}>
										<Text size="xs" fw={500} ta="left" style={{ overflowWrap: "anywhere" }} data-testid="chat-source-title">
											{documentNames.get(source.documentId) ?? source.title}
										</Text>
										{source.section ? (
											<Text size="xs" c="dimmed" ta="left" style={{ overflowWrap: "anywhere" }}>
												{source.section}
											</Text>
										) : null}
									</Stack>
									{source.scoreKind ? (
										<KnowledgeScoreBadge score={source.score} scoreKind={source.scoreKind} size="xs" />
									) : (
										<Badge size="xs" variant="light" color="gray" style={{ flexShrink: 0 }}>
											{t("pages.chat.sources.score", "score {{value}}", { value: formatScore(source.score) })}
										</Badge>
									)}
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
