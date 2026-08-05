import { ActionIcon, Tooltip } from "@mantine/core";
import { IconArrowsMinimize } from "@tabler/icons-react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";

import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import type { ChatCompactionResult } from "@/features/chat/models/ChatModels";
import { toNodeChatRequestModel } from "@/features/chat/models/NodeChatModelSelection";
import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";
import { useNodeChatPreferencesStore } from "@/features/chat/stores/NodeChatPreferencesStore";

interface CompactButtonProps {
	// Current context-window fill (0–100). Only used to tint the icon as it approaches full, so the control reads as a
	// remedy for a filling window. Undefined leaves it in the neutral subtle style.
	percentUsed?: number;
	disabled?: boolean;
}

// Manual, non-destructive compaction control placed beside the ContextUsageBadge. Summarizes the older turns of the
// active conversation into a synopsis (local model) that is sent in their place on later turns; the original messages
// are never deleted. Reads the selected conversation from the preferences store so it needs no threading through the
// input-area props, and confirms before acting.
export function CompactButton({ percentUsed, disabled = false }: CompactButtonProps) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const queryClient = useQueryClient();
	const conversationId = useNodeChatPreferencesStore((state) => state.selectedConversationId);
	// Summarize with the model the user is chatting with; the backend honors it when it's an installed local chat model
	// and otherwise falls back to a node-local default (a cloud selection never sends conversation content off-node).
	const selectedModel = useNodeChatPreferencesStore((state) => state.selectedModel);

	const compactMutation = useMutation({
		// Map through the same helper normal sends use so the "Local runtime default" sentinel becomes undefined (→ the
		// node default on the backend) instead of being forwarded as a literal model id that never matches.
		mutationFn: (id: string) => nodeChatAdapter.compactConversation(id, toNodeChatRequestModel(selectedModel)),
		onSuccess: async (result, id) => {
			// Refetch the conversation (now carrying the synopsis) and the lists so any preview/state stays in sync.
			await queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversation(id), exact: true });
			await queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversationLists() });
			showOutcomeToast(result);
		},
		onError: () => {
			toast.error(t("pages.chat.compact.error", "Couldn't compact the conversation. Please try again."));
		},
	});

	function showOutcomeToast(result: ChatCompactionResult): void {
		switch (result.outcome) {
			case "Compacted":
				// A cloud/unknown selection was summarized on a node-local model instead — tell the user rather than
				// silently swapping their chosen model.
				if (result.usedFallbackModel) {
					toast.info(
						t(
							"pages.chat.compact.compactedLocalFallback",
							"Compacted {{count}} older message(s) locally with {{model}} to keep your chat on-device. Your originals are kept.",
							{ count: result.messagesFolded, model: result.modelUsed ?? t("pages.chat.compact.aLocalModel", "a local model") },
						),
					);
				} else {
					toast.success(
						t("pages.chat.compact.compacted", "Compacted {{count}} older message(s) into a summary. Your originals are kept.", {
							count: result.messagesFolded,
						}),
					);
				}
				break;
			case "NothingToCompact":
				toast.info(t("pages.chat.compact.nothing", "Nothing to compact yet — the recent messages still fit the context window."));
				break;
			case "NoLocalModel":
				toast.warn(t("pages.chat.compact.noModel", "No local chat model is installed to summarize with, so compaction can't run on-node."));
				break;
			default:
				toast.warn(t("pages.chat.compact.noSummary", "Compaction didn't produce a summary. Please try again."));
		}
	}

	async function handleClick(): Promise<void> {
		if (!conversationId) {
			return;
		}

		const confirmed = await confirm({
			title: t("pages.chat.compact.confirmTitle", "Compact conversation"),
			description: t(
				"pages.chat.compact.confirmDescription",
				"Summarize the older messages into a compact synopsis to free up the context window. Your original messages are kept and stay visible in the conversation.",
			),
			confirmationText: t("pages.chat.compact.confirm", "Compact"),
			cancellationText: t("common.cancel", "Cancel"),
		});
		if (!confirmed) {
			return;
		}

		compactMutation.mutate(conversationId);
	}

	const isDisabled = disabled || !conversationId || compactMutation.isPending;
	const color = percentUsed === undefined ? undefined : percentUsed >= 90 ? "red" : percentUsed >= 70 ? "yellow" : undefined;

	return (
		<Tooltip label={t("pages.chat.compact.tooltip", "Compact older messages into a summary to free up context")} withArrow={true}>
			<ActionIcon
				variant="subtle"
				color={color}
				size="sm"
				disabled={isDisabled}
				loading={compactMutation.isPending}
				onClick={handleClick}
				aria-label={t("pages.chat.compact.aria", "Compact conversation")}
				data-testid="compact-conversation-button"
			>
				<IconArrowsMinimize size={16} />
			</ActionIcon>
		</Tooltip>
	);
}
