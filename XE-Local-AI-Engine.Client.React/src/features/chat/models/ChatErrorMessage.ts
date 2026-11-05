import { type TFunction, t as translate } from "i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { isNodeChatReadOnlyConflict, stripSignalRHubErrorPrefix } from "@/features/chat/api/NodeChatConflict";

// Strip SignalR's generic HubException wrapper so the bubble/toast lead with the sentence the hub deliberately
// wrote (e.g. the message-size rejection); anything not matching the wrapper passes through untouched. The regex
// lives next to isNodeChatReadOnlyConflict, which discriminates on the same stripped text.
export function chatErrorMessage(error: unknown): string {
	// Module-scoped `t` (the app's i18next instance): this helper is called from callbacks all over the chat hooks and
	// the stream controller, so threading a hook's `t` through all of them would buy nothing over the pattern
	// ApiErrorMessage and Toast already use.
	const message = apiErrorMessage(error, translate("pages.chat.unknownError", "Unknown error"));
	const stripped = stripSignalRHubErrorPrefix(message);
	return stripped.length > 0 ? stripped : message;
}

export function remoteViewOnlyMessage(t: TFunction): string {
	return t("pages.chat.remoteViewOnly", "This conversation was started from a paired client and is view-only on this node.");
}

/** A refused write's text: a remote-origin conversation's read-only conflict gets the view-only sentence. */
export function chatWriteErrorMessage(error: unknown, t: TFunction): string {
	return isNodeChatReadOnlyConflict(error) ? remoteViewOnlyMessage(t) : chatErrorMessage(error);
}
