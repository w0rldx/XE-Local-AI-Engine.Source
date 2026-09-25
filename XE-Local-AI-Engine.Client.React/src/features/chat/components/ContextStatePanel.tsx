import { ActionIcon, Alert, Badge, Code, Drawer, Group, Loader, ScrollArea, Stack, Text, Title, Tooltip } from "@mantine/core";
import { IconListDetails } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientEndpointsLocalChatV1NodeChatConversationContextStateEntryResponse as ContextStateEntry } from "@/core/api/generated";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { useConversationContextState } from "@/features/chat/queries/useConversationContextState";
import { useNodeChatPreferencesStore } from "@/features/chat/stores/NodeChatPreferencesStore";

// Most actionable first: what the user wants, what they corrected, what is still open, then the settled record.
const CATEGORY_ORDER = ["Goal", "Correction", "OpenQuestion", "Decision", "Constraint", "Fact", "ToolOutcome", "CompletedWork"];

interface ContextStateButtonProps {
	disabled?: boolean;
}

// Read-only window onto what compaction distilled from the active conversation (plan decision D4: no edit controls).
// Sits beside CompactButton and reads the selected conversation from the same store, so it needs no prop threading.
export function ContextStateButton({ disabled = false }: ContextStateButtonProps) {
	const { t } = useTranslation();
	const conversationId = useNodeChatPreferencesStore((state) => state.selectedConversationId);
	const [opened, setOpened] = useState(false);
	const label = t("pages.chat.contextState.aria", "Show conversation context state");

	return (
		<>
			<Tooltip label={t("pages.chat.contextState.tooltip", "Show what the conversation remembers")} withArrow={true}>
				<ActionIcon
					variant="subtle"
					size="sm"
					disabled={disabled || !conversationId}
					onClick={() => setOpened(true)}
					aria-label={label}
					data-testid="context-state-button"
				>
					<IconListDetails size={16} />
				</ActionIcon>
			</Tooltip>
			<Drawer
				opened={opened}
				onClose={() => setOpened(false)}
				position="right"
				title={t("pages.chat.contextState.title", "Conversation context")}
				attributes={{ content: { "data-testid": "context-state-drawer" } }}
			>
				{/* Drawer content unmounts while closed, so the query only runs (and refetches) while the panel is open. */}
				<ContextStatePanel conversationId={conversationId} />
			</Drawer>
		</>
	);
}

function ContextStatePanel({ conversationId }: { conversationId: string }) {
	const { t } = useTranslation();
	const { data, isLoading, isError } = useConversationContextState(conversationId, { enabled: true });

	if (isLoading) {
		return <Loader size="sm" />;
	}
	if (isError) {
		return <Alert color="red">{t("pages.chat.contextState.error", "Couldn't load the context state.")}</Alert>;
	}

	const entries = data?.entries ?? [];
	const synopsis = data?.synopsis?.trim() ?? "";
	if (!data || (entries.length === 0 && synopsis.length === 0)) {
		return (
			<Text c="dimmed" size="sm" data-testid="context-state-empty">
				{t("pages.chat.contextState.empty", "Nothing distilled yet. Compact the conversation to build its context state.")}
			</Text>
		);
	}

	const live = entries.filter((entry) => entry.isLive !== false);
	const inactive = entries.filter((entry) => entry.isLive === false);
	// Known categories in display order, then any category a newer backend adds.
	const categories = [...new Set([...CATEGORY_ORDER, ...live.map((entry) => entry.category)])];
	const notCompacted = t("pages.chat.contextState.notCompacted", "Not compacted yet");

	return (
		<Stack gap="md">
			<Stack gap={2}>
				<Text size="xs" c="dimmed">
					{data.stateCoversToSequence == null
						? notCompacted
						: t("pages.chat.contextState.stateCoverage", "State covers up to sequence {{sequence}}, updated {{updatedAt}}", {
								sequence: data.stateCoversToSequence,
								updatedAt: formatTimestamp(data.stateUpdatedAtUtc),
							})}
				</Text>
				<Text size="xs" c="dimmed">
					{data.synopsisCoversToSequence == null
						? notCompacted
						: t(
								"pages.chat.contextState.synopsisCoverage",
								"Synopsis covers up to sequence {{sequence}}, updated {{updatedAt}}",
								{
									sequence: data.synopsisCoversToSequence,
									updatedAt: formatTimestamp(data.synopsisUpdatedAtUtc),
								},
							)}
				</Text>
			</Stack>
			{categories.map((category) => {
				const group = live.filter((entry) => entry.category === category);
				return group.length === 0 ? null : (
					<Stack key={category} gap="xs">
						<Group gap="xs">
							<Title order={6}>{t(`pages.chat.contextState.categories.${category}`, category)}</Title>
							<Badge size="sm" variant="light">
								{group.length}
							</Badge>
						</Group>
						{group.map((entry) => (
							<EntryRow key={entry.id} entry={entry} />
						))}
					</Stack>
				);
			})}
			{inactive.length > 0 ? (
				<details data-testid="context-state-inactive">
					<summary>
						<Text component="span" size="sm">
							{t("pages.chat.contextState.inactive", "Superseded and retired ({{count}})", { count: inactive.length })}
						</Text>
					</summary>
					<Stack gap="xs" mt="xs">
						{inactive.map((entry) => (
							<EntryRow key={entry.id} entry={entry} />
						))}
					</Stack>
				</details>
			) : null}
			{synopsis.length > 0 ? (
				<Stack gap="xs">
					<Title order={6}>{t("pages.chat.contextState.synopsis", "Synopsis")}</Title>
					<ScrollArea.Autosize mah={320}>
						<Text size="sm" style={{ whiteSpace: "pre-wrap" }} data-testid="context-state-synopsis">
							{synopsis}
						</Text>
					</ScrollArea.Autosize>
				</Stack>
			) : null}
		</Stack>
	);
}

function EntryRow({ entry }: { entry: ContextStateEntry }) {
	const { t } = useTranslation();
	const isLive = entry.isLive !== false;
	return (
		<Stack gap={2}>
			<Group gap="xs" wrap="nowrap" align="flex-start">
				<Code>{entry.id}</Code>
				<Text size="sm" td={isLive ? undefined : "line-through"} style={{ overflowWrap: "anywhere" }}>
					{entry.value}
				</Text>
			</Group>
			<Text size="xs" c="dimmed">
				{t("pages.chat.contextState.sources", "From sequences {{sequences}}", { sequences: entry.sourceSequences.join(", ") })}
				{entry.supersededById
					? ` · ${t("pages.chat.contextState.replacedBy", "replaced by {{id}}", { id: entry.supersededById })}`
					: null}
				{entry.retiredAtSequence != null
					? ` · ${t("pages.chat.contextState.retiredAt", "retired at sequence {{sequence}}", { sequence: entry.retiredAtSequence })}`
					: null}
			</Text>
		</Stack>
	);
}
