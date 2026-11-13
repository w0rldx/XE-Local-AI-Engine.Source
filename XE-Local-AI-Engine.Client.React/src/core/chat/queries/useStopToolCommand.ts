import { useMutation } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { stopNodeChatToolCommandMutation } from "@/core/api/generated/@tanstack/react-query.gen";
import { toast } from "@/core/ui/notifications/Toast";

/**
 * Stops the sandbox command a running chat tool call is executing. The tool result then arrives on the stream as
 * usual, so a success needs no message; a 404 means the command had already ended.
 */
export function useStopToolCommand() {
	const { t } = useTranslation();
	return useMutation({
		...stopNodeChatToolCommandMutation(),
		onError: (error) => {
			if (error instanceof ApiError && error.statusCode === 404) {
				toast.info(t("chat.toolCall.stopNothingRunning", "The command had already finished."));
				return;
			}
			toast.error(apiErrorMessage(error, t("chat.toolCall.stopFailed", "Could not stop the command.")));
		},
	});
}
