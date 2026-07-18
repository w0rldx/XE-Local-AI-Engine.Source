import { Badge, Collapse, Group, Paper, Stack, Text, UnstyledButton } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconBooks, IconChevronRight } from "@tabler/icons-react";
import { memo } from "react";
import { useTranslation } from "react-i18next";

import type { ChatMessageSource } from "@/features/chat/models/ChatModels";

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
 */
export const ChatSourcesStrip = memo(function ChatSourcesStrip({ sources }: ChatSourcesStripProps) {
	const { t } = useTranslation();
	const [opened, { toggle }] = useDisclosure(false);

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
						<Paper
							key={source.chunkId || `${source.documentId}:${source.title}`}
							withBorder={true}
							p="xs"
							radius="sm"
							data-testid="chat-source-card"
						>
							<Group gap={8} justify="space-between" align="flex-start" wrap="nowrap">
								<Stack gap={2} style={{ minWidth: 0 }}>
									<Text size="xs" fw={500} style={{ overflowWrap: "anywhere" }}>
										{source.title}
									</Text>
									{source.section ? (
										<Text size="xs" c="dimmed" style={{ overflowWrap: "anywhere" }}>
											{source.section}
										</Text>
									) : null}
								</Stack>
								<Badge size="xs" variant="light" color="primary" style={{ flexShrink: 0 }}>
									{t("pages.chat.sources.score", "score {{value}}", { value: formatScore(source.score) })}
								</Badge>
							</Group>
						</Paper>
					))}
				</Stack>
			</Collapse>
		</Stack>
	);
});
