// The chat export file: the export route's body, and what the import route takes back. The server is the authority on
// its content; the shape check here only refuses a file that is not a chat export at all, with a reason it can name.

import type { TFunction } from "i18next";

import type {
	XeLocalAiEngineClientEndpointsLocalChatV1NodeChatExportConversation as ChatExportConversation,
	XeLocalAiEngineClientEndpointsLocalChatV1NodeChatExportDocument as ChatExportDocument,
} from "@/core/api/generated";
import { isPlainRecord } from "@/core/models/JsonRecord";

const CHAT_EXPORT_FORMAT = "xe-chat-export";
const CHAT_EXPORT_FORMAT_VERSION = 1;

/** The import route's cap on the compact body it is posted as (`NodeChatExportFile.MaxImportBytes`). */
const CHAT_IMPORT_MAX_BYTES = 64 * 1024 * 1024;

/** An export is pretty-printed, so the file may outgrow the compact body; only a file no indentation explains is refused unread. */
export const CHAT_IMPORT_READ_LIMIT_BYTES = 2 * CHAT_IMPORT_MAX_BYTES;

export type ChatExportFormat = "markdown" | "json";

export type ChatImportFailure = "notJson" | "wrongFormat" | "unsupportedVersion";

export type ChatExportParse =
	| { readonly ok: true; readonly document: ChatExportDocument }
	| { readonly ok: false; readonly reason: ChatImportFailure };

export function parseChatExportFile(text: string): ChatExportParse {
	let value: unknown;
	try {
		value = JSON.parse(text);
	} catch {
		return { ok: false, reason: "notJson" };
	}
	if (!isPlainRecord(value) || value["format"] !== CHAT_EXPORT_FORMAT || !Array.isArray(value["conversations"])) {
		return { ok: false, reason: "wrongFormat" };
	}
	if (value["formatVersion"] !== CHAT_EXPORT_FORMAT_VERSION) {
		return { ok: false, reason: "unsupportedVersion" };
	}
	return { ok: true, document: value as unknown as ChatExportDocument };
}

/** A download name from a title: lowercase ASCII words joined by hyphens, accents folded, `chat` when nothing is left. */
export function chatExportFileName(title: string | null | undefined, extension: "md" | "json"): string {
	const slug = (title ?? "")
		.normalize("NFD")
		.replace(/[̀-ͯ]/g, "")
		.toLowerCase()
		.replace(/[^a-z0-9]+/g, "-")
		.replace(/^-+|-+$/g, "")
		.slice(0, 64);
	return `${slug || "chat"}.${extension === "md" ? "md" : "chat.json"}`;
}

/** One chat as Markdown: its selected path, with the attachments and tool calls the export leaves out named, not hidden. */
export function chatExportMarkdown(conversation: ChatExportConversation, t: TFunction): string {
	const lines = [`# ${conversation.title?.trim() || t("chat.exportImport.untitled", "Untitled conversation")}`, ""];
	const attachments = conversation.attachments ?? [];
	if (attachments.length > 0) {
		lines.push(
			`> ${t("chat.exportImport.attachmentsOmitted", "Attached files, not included: {{names}}", {
				names: attachments.map((attachment) => attachment.fileName).join(", "),
			})}`,
			"",
		);
	}
	for (const message of conversation.messages) {
		const speaker = message.role === "user" ? t("chat.exportImport.user", "User") : t("chat.exportImport.assistant", "Assistant");
		lines.push(`## ${message.model ? `${speaker} (${message.model})` : speaker}`, "", message.content, "");
		if ((message.toolCallCount ?? 0) > 0) {
			lines.push(`_${t("chat.exportImport.toolCallsOmitted", { count: message.toolCallCount })}_`, "");
		}
	}
	return lines.join("\n");
}
