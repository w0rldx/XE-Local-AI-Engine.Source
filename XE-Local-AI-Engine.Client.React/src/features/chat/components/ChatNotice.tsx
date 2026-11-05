import { Anchor, Progress, Stack, Text } from "@mantine/core";
import { Link } from "@tanstack/react-router";
import type { TFunction } from "i18next";
import type { ReactNode } from "react";

import { chatErrorMessage, remoteViewOnlyMessage } from "@/features/chat/models/ChatErrorMessage";

/** The first-run model download the route feeds in; the download feed itself belongs to the models feature. */
export interface ChatSetupDownload {
	readonly modelName: string;
	readonly pct: number | undefined;
}

interface ChatNoticeInput {
	streamError: string | undefined;
	conversationsIsError: boolean;
	conversationsError: unknown;
	isRemoteConversation: boolean;
	showNoModelGuidance: boolean;
	setupDownload: ChatSetupDownload | undefined;
}

/**
 * The alert body above the chat pane, or undefined for none. A plain function, not a component: ChatDisplayShell renders
 * its alert when `disabledNotice` is truthy, and a component's element always is.
 */
export function chatNotice(
	{
		streamError,
		conversationsIsError,
		conversationsError,
		isRemoteConversation,
		showNoModelGuidance,
		setupDownload,
	}: ChatNoticeInput,
	t: TFunction,
): ReactNode {
	if (streamError) {
		return (
			<Stack gap={2}>
				<Text fw={700}>{t("pages.chat.streamFailedTitle", "Local chat stream failed.")}</Text>
				<Text size="sm">{streamError}</Text>
			</Stack>
		);
	}
	if (conversationsIsError) {
		return (
			<Stack gap={2}>
				<Text fw={700}>{t("pages.chat.historyLoadFailedTitle", "Unable to load local chat history.")}</Text>
				<Text size="sm">{chatErrorMessage(conversationsError)}</Text>
			</Stack>
		);
	}
	if (isRemoteConversation) {
		return (
			<Stack gap={2}>
				<Text fw={700}>{t("pages.chat.remoteViewOnlyTitle", "Remote conversation")}</Text>
				<Text size="sm">{remoteViewOnlyMessage(t)}</Text>
			</Stack>
		);
	}
	if (!showNoModelGuidance) {
		return undefined;
	}
	if (setupDownload !== undefined) {
		// First-run provisioning is downloading the default model: say so instead of "install one".
		return (
			<Stack gap={4} data-testid="chat-first-model-setup">
				<Text fw={700}>{t("pages.chat.noModelGuidance.setupTitle", "Setting up your first model…")}</Text>
				<Text size="sm" data-testid="chat-first-model-setup-model">
					{setupDownload.pct === undefined
						? setupDownload.modelName
						: t("pages.chat.noModelGuidance.setupProgress", "{{model}} · {{pct}}%", {
								model: setupDownload.modelName,
								pct: setupDownload.pct,
							})}
				</Text>
				<Progress
					value={setupDownload.pct ?? 100}
					animated={setupDownload.pct === undefined}
					aria-label={t("pages.chat.noModelGuidance.setupTitle", "Setting up your first model…")}
				/>
				<Anchor component={Link} to="/models" size="sm" data-testid="chat-no-model-guidance-models-link">
					{t("pages.chat.noModelGuidance.goToModels", "Go to Models")}
				</Anchor>
			</Stack>
		);
	}
	// Advisory, not blocking — a Codex/Azure sign-in later still routes around this via the picker's
	// cloud sections, and a send attempted anyway still falls through to ChatMessage's ModelNotInstalled Alert.
	return (
		<Stack gap={2}>
			<Text fw={700}>{t("pages.chat.noModelGuidance.title", "No chat model installed yet")}</Text>
			<Text size="sm">{t("pages.chat.noModelGuidance.body", "Install a GGUF model to start chatting locally.")}</Text>
			<Anchor component={Link} to="/models" size="sm" data-testid="chat-no-model-guidance-models-link">
				{t("pages.chat.noModelGuidance.goToModels", "Go to Models")}
			</Anchor>
		</Stack>
	);
}
