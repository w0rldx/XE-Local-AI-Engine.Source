import { Anchor, Group, Loader, Text } from "@mantine/core";
import { Link, useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import type { XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowRunResponse as GraphWorkflowRunResponse } from "@/core/api/generated";
import { usePendingChatConversationStore } from "@/core/ui/stores/PendingChatConversationStore";
import type { IntentRefusal } from "@/features/command/api/CommandApi";
import { isTerminalRunStatus, useCommandRunStatus } from "@/features/command/hooks/useCommandRunStatus";

/** One answer to one Enter, kept in the page's memory only. */
export type CommandResult =
	| { readonly id: string; readonly utterance: string; readonly kind: "pending" }
	| {
			readonly id: string;
			readonly utterance: string;
			readonly kind: "run";
			readonly definitionName: string;
			readonly definitionId: string;
			readonly run: GraphWorkflowRunResponse;
	  }
	| {
			readonly id: string;
			readonly utterance: string;
			readonly kind: "reply";
			readonly conversationId: string | null;
			readonly text: string;
			readonly done: boolean;
			readonly failed: boolean;
	  }
	| { readonly id: string; readonly utterance: string; readonly kind: "refused"; readonly refusal: IntentRefusal };

interface CommandResultRowProps {
	readonly result: CommandResult;
	/** History rows are one dimmed line; the latest answer shows in full. */
	readonly compact: boolean;
}

export function CommandResultRow({ result, compact }: CommandResultRowProps) {
	const { t } = useTranslation();
	const lineClamp = compact ? 1 : undefined;
	const color = compact ? "dimmed" : undefined;

	switch (result.kind) {
		case "pending":
			return (
				<Group gap="xs" wrap="nowrap">
					<Loader size="xs" />
					<Text size="sm" c="dimmed" lineClamp={1}>
						{result.utterance}
					</Text>
				</Group>
			);
		case "run":
			return <RunResultRow result={result} lineClamp={lineClamp} color={color} />;
		case "reply":
			return <ReplyResultRow result={result} lineClamp={lineClamp} color={color} />;
		default:
			return (
				<Text size="sm" c={compact ? "dimmed" : "red"} lineClamp={lineClamp} data-testid="command-refusal">
					{t(`pages.command.refusal.${result.refusal}`)}
				</Text>
			);
	}
}

interface RowStyle {
	readonly lineClamp: number | undefined;
	readonly color: string | undefined;
}

function RunResultRow({ result, lineClamp, color }: RowStyle & { readonly result: Extract<CommandResult, { kind: "run" }> }) {
	const { t } = useTranslation();
	const { status, failure } = useCommandRunStatus(result.run);
	const running = !isTerminalRunStatus(status);

	return (
		<Group gap="xs" wrap="nowrap" data-testid="command-run">
			{running ? <Loader size="xs" /> : null}
			<Text size="sm" c={color} lineClamp={lineClamp} style={{ flex: 1 }}>
				{t(running ? "pages.command.run.running" : "pages.command.run.finished", { name: result.definitionName })}
			</Text>
			<Text size="sm" fw={600} c={color} data-testid="command-run-status">
				{t(`pages.graphWorkflows.runStatus.${status}`, status)}
			</Text>
			{failure?.reason ? (
				<Text size="sm" c={color ?? "red"} lineClamp={lineClamp} data-testid="command-run-failure">
					{t("pages.command.run.failure", { nodeKey: failure.nodeKey, reason: failure.reason })}
				</Text>
			) : null}
			{/* `Link` directly, not `Anchor component={Link}`: the polymorphic prop erases the router's search typing. */}
			<Link to={nodeRoutePaths.graphWorkflows} search={{ definitionId: result.definitionId, runId: result.run.run.id }}>
				<Anchor component="span" size="sm">
					{t("pages.command.run.open")}
				</Anchor>
			</Link>
		</Group>
	);
}

function ReplyResultRow({ result, lineClamp, color }: RowStyle & { readonly result: Extract<CommandResult, { kind: "reply" }> }) {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const setPendingConversationId = usePendingChatConversationStore((state) => state.actions.setPendingConversationId);
	const { conversationId } = result;

	return (
		<Group gap="xs" wrap="nowrap" align="flex-start" data-testid="command-reply">
			{result.done ? null : <Loader size="xs" />}
			<Text size="sm" c={result.failed ? "red" : color} lineClamp={lineClamp} style={{ flex: 1, whiteSpace: "pre-wrap" }}>
				{result.failed ? t("pages.command.reply.failed") : result.text || t("pages.command.reply.waiting")}
			</Text>
			{conversationId === null ? null : (
				<Anchor
					component="button"
					type="button"
					size="sm"
					onClick={() => {
						// `/chat` reads its thread from its own preferences; this core hand-off store is how another page asks for one.
						setPendingConversationId(conversationId);
						navigate({ to: nodeRoutePaths.chat }).catch(() => undefined);
					}}
				>
					{t("pages.command.reply.openInChat")}
				</Anchor>
			)}
		</Group>
	);
}
