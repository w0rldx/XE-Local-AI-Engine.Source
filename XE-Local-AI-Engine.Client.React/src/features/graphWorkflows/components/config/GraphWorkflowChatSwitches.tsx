// The chat flags under a node's body, for the kinds that carry them.

import { Switch } from "@mantine/core";
import { useTranslation } from "react-i18next";

import {
	type GraphWorkflowCanvasNodeData,
	type GraphWorkflowGraphSettings,
	graphWorkflowChatDefaults,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";

/**
 * `publishToChat` (Agent, LlmCall, End) and `includeAttachments` (Agent, LlmCall). Shown on a Chat graph, and on any
 * graph where the flag is already on — the parser refuses that outside a Chat graph, so the operator needs the switch
 * to turn it off. An absent flag shows the parser's default: `true` for an End in a Chat graph, `false` otherwise.
 */
export function GraphWorkflowChatSwitches({
	node,
	graphSettings,
	onChange,
	readOnly,
}: {
	readonly node: GraphWorkflowCanvasNodeData;
	readonly graphSettings: GraphWorkflowGraphSettings;
	readonly onChange: (patch: Partial<GraphWorkflowCanvasNodeData>) => void;
	readonly readOnly: boolean;
}) {
	const { t } = useTranslation();
	if (node.kind !== "Agent" && node.kind !== "LlmCall" && node.kind !== "End") {
		return null;
	}
	const isChat = graphSettings.kind === "Chat";
	const acceptsAttachments = isChat && (graphSettings.chat?.acceptsAttachments ?? graphWorkflowChatDefaults.acceptsAttachments);
	const publishDefault = node.kind === "End" && isChat;
	const publish = node.publishToChat ?? publishDefault;
	const include = node.kind === "End" ? undefined : node.includeAttachments;
	return (
		<>
			{isChat || node.publishToChat === true ? (
				<Switch
					label={t("pages.graphWorkflows.config.publishToChat", "Post the result to the chat")}
					checked={publish}
					disabled={readOnly}
					// The parser default is written as ABSENT, so an End toggled off and on again saves byte for byte.
					onChange={(event) =>
						onChange({ publishToChat: event.currentTarget.checked === publishDefault ? undefined : event.currentTarget.checked })
					}
					data-testid="gw-node-config-publish-to-chat"
				/>
			) : null}
			{node.kind !== "End" && (isChat || include === true) ? (
				<Switch
					label={t("pages.graphWorkflows.config.includeAttachments", "Include the chat attachments")}
					description={
						acceptsAttachments
							? undefined
							: t(
									"pages.graphWorkflows.config.includeAttachmentsDisabled",
									"Turn on “Accepts attachments” in the workflow settings first.",
								)
					}
					checked={include ?? false}
					// Enabled while ON even when the graph refuses attachments, so a refused flag can still be turned off.
					disabled={readOnly || (!acceptsAttachments && include !== true)}
					onChange={(event) => onChange({ includeAttachments: event.currentTarget.checked ? true : undefined })}
					data-testid="gw-node-config-include-attachments"
				/>
			) : null}
		</>
	);
}
