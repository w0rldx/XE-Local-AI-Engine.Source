import { useQueryClient } from "@tanstack/react-query";
import { useCallback } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { exportNodeChatConversations, importNodeChatConversations } from "@/core/api/generated";
import { saveBlob } from "@/core/api/utils/DownloadBlob";
import { nodeChatQueryKeys } from "@/core/chat/queries/NodeChatQueryKeys";
import { toast } from "@/core/ui/notifications/Toast";
import {
	CHAT_IMPORT_READ_LIMIT_BYTES,
	type ChatExportFormat,
	chatExportFileName,
	chatExportMarkdown,
	parseChatExportFile,
} from "@/features/chat/models/ChatExportFile";

function saveJson(value: unknown, fileName: string): void {
	saveBlob(new Blob([`${JSON.stringify(value, null, 2)}\n`], { type: "application/json" }), fileName);
}

/** The chat list's export (one chat as Markdown or JSON, every chat as JSON) and JSON import, each reporting by toast. */
export function useChatExportImport() {
	const { t } = useTranslation();
	const queryClient = useQueryClient();

	const exportFailed = useCallback(
		(error: unknown) => toast.error(apiErrorMessage(error, t("chat.exportImport.exportFailed", "Could not export."))),
		[t],
	);

	const exportConversation = useCallback(
		(conversationId: string, format: ChatExportFormat): void => {
			exportNodeChatConversations({ query: { conversationId } })
				.then(({ data }) => {
					const conversation = data.conversations[0];
					if (conversation === undefined) {
						return;
					}
					if (format === "markdown") {
						saveBlob(
							new Blob([chatExportMarkdown(conversation, t)], { type: "text/markdown" }),
							chatExportFileName(conversation.title, "md"),
						);
					} else {
						saveJson(data, chatExportFileName(conversation.title, "json"));
					}
				})
				.catch(exportFailed);
		},
		[exportFailed, t],
	);

	const exportAll = useCallback((): void => {
		exportNodeChatConversations({ query: {} })
			.then(({ data }) => saveJson(data, chatExportFileName("chats", "json")))
			.catch(exportFailed);
	}, [exportFailed]);

	const importFile = useCallback(
		(file: File): void => {
			if (file.size > CHAT_IMPORT_READ_LIMIT_BYTES) {
				toast.error(t("chat.exportImport.tooLarge", "This file is larger than the 64 MB an import accepts."));
				return;
			}
			file
				.text()
				.then(async (text) => {
					const parsed = parseChatExportFile(text);
					if (!parsed.ok) {
						toast.error(
							parsed.reason === "notJson"
								? t("chat.exportImport.notJson", "This file is not JSON.")
								: parsed.reason === "unsupportedVersion"
									? t("chat.exportImport.unsupportedVersion", "This chat export has a format version this app cannot read.")
									: t("chat.exportImport.wrongFormat", "This file is not a chat export."),
						);
						return;
					}
					const { data } = await importNodeChatConversations({ body: parsed.document });
					await queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversationLists() });
					toast.success(t("chat.exportImport.imported", { count: data.conversations.length }));
				})
				.catch((error: unknown) =>
					toast.error(apiErrorMessage(error, t("chat.exportImport.importFailed", "Could not import the file."))),
				);
		},
		[queryClient, t],
	);

	return { exportConversation, exportAll, importFile };
}
