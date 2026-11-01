import { ActionIcon, Button, Group, Popover, Stack, Switch, Text, Textarea, Tooltip } from "@mantine/core";
import { IconPencil } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import type { ChatConversationModel } from "@/features/chat/models/ChatModels";

// The server's cap on a plain conversation's custom system prompt (NodeChatSetConversationPlainRequest).
const PLAIN_SYSTEM_PROMPT_MAX_LENGTH = 16_000;

interface PlainConversationControlsProps {
	conversation: ChatConversationModel;
	disabled?: boolean;
	onSetConversationPlain: (conversationId: string, isPlain: boolean, plainSystemPrompt: string | undefined) => void;
}

// The conversation-level plain switch and its system prompt editor. Plain turns run without an agent, tools, knowledge
// base or memory; the prompt is sent verbatim, and a blank one sends no system message at all.
export function PlainConversationControls({
	conversation,
	disabled = false,
	onSetConversationPlain,
}: PlainConversationControlsProps) {
	const { t } = useTranslation();
	const [editorOpen, setEditorOpen] = useState(false);
	const [draft, setDraft] = useState("");
	const isPlain = conversation.isPlain === true;

	const openEditor = (): void => {
		setDraft(conversation.plainSystemPrompt ?? "");
		setEditorOpen(true);
	};

	const savePrompt = (): void => {
		const trimmed = draft.trim();
		onSetConversationPlain(conversation.id, true, trimmed.length > 0 ? draft : undefined);
		setEditorOpen(false);
	};

	return (
		<Group gap={4} wrap="nowrap">
			<Tooltip
				label={t(
					"pages.chat.plainMode.tooltip",
					"Plain chat: no agent, tools, knowledge base or memory. Only your system prompt, if any, is sent.",
				)}
				withArrow={true}
				multiline={true}
				maw={260}
			>
				<Switch
					size="xs"
					labelPosition="left"
					label={t("pages.chat.plainMode.label", "Plain")}
					checked={isPlain}
					disabled={disabled}
					onChange={(event) =>
						onSetConversationPlain(conversation.id, event.currentTarget.checked, conversation.plainSystemPrompt)
					}
					data-testid="chat-plain-toggle"
				/>
			</Tooltip>
			{isPlain ? (
				<Popover opened={editorOpen} onChange={setEditorOpen} width={360} position="bottom-end" withArrow={true} trapFocus={true}>
					<Popover.Target>
						<Tooltip label={t("pages.chat.plainMode.editPrompt", "Edit system prompt")} withArrow={true}>
							<ActionIcon
								variant="subtle"
								size="sm"
								disabled={disabled}
								onClick={openEditor}
								aria-label={t("pages.chat.plainMode.editPrompt", "Edit system prompt")}
								data-testid="chat-plain-prompt-edit"
							>
								<IconPencil size={16} />
							</ActionIcon>
						</Tooltip>
					</Popover.Target>
					<Popover.Dropdown>
						<Stack gap="xs">
							<Textarea
								label={t("pages.chat.plainMode.promptLabel", "System prompt")}
								description={t(
									"pages.chat.plainMode.promptDescription",
									"Sent verbatim on every later turn. Leave it empty to send no system prompt.",
								)}
								value={draft}
								onChange={(event) => setDraft(event.currentTarget.value)}
								maxLength={PLAIN_SYSTEM_PROMPT_MAX_LENGTH}
								autosize={true}
								minRows={3}
								maxRows={12}
								data-testid="chat-plain-prompt-input"
							/>
							<Group justify="space-between">
								<Text size="xs" c="dimmed">
									{draft.length} / {PLAIN_SYSTEM_PROMPT_MAX_LENGTH}
								</Text>
								<Button size="xs" onClick={savePrompt} data-testid="chat-plain-prompt-save">
									{t("pages.chat.plainMode.save", "Save")}
								</Button>
							</Group>
						</Stack>
					</Popover.Dropdown>
				</Popover>
			) : null}
		</Group>
	);
}
