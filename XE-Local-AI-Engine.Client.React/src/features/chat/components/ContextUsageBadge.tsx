import { Progress, Tooltip } from "@mantine/core";

import type { ContextUsageModel } from "@/features/chat/models/ChatModels";

function trimNumber(value: number): string {
	return value.toFixed(1).replace(/\.0$/, "");
}

function formatTokenCount(value: number | undefined): string {
	if (value === undefined) {
		return "—";
	}

	if (value >= 1_000_000) {
		return `${trimNumber(value / 1_000_000)}m`;
	}

	if (value >= 1_000) {
		return `${trimNumber(value / 1_000)}k`;
	}

	return value.toString();
}

function tooltip({ isAuthoritative, maxTokens, modelLabel, nodeLabel, usedTokens }: ContextUsageModel): string {
	const usedOrigin =
		usedTokens === undefined
			? "Used context will update after the assistant response completes"
			: isAuthoritative
				? "Used context reported by model"
				: "Used context not yet authoritative";
	const maxOrigin =
		maxTokens === undefined ? "Max context unknown for this model on this node" : "Max context reported by selected node";

	return `${usedOrigin}. ${maxOrigin}. Model: ${modelLabel || "Unknown"}. Node: ${nodeLabel || "Local node"}.`;
}

export function ContextUsageBadge(props: ContextUsageModel) {
	const { usedTokens, maxTokens } = props;
	const percent =
		usedTokens !== undefined && maxTokens !== undefined && maxTokens > 0 ? (usedTokens / maxTokens) * 100 : undefined;
	const color = percent === undefined ? "gray" : percent >= 90 ? "red" : percent >= 70 ? "yellow" : "green";
	const label = `${formatTokenCount(usedTokens)}/${formatTokenCount(maxTokens)}${percent === undefined ? "" : ` ${Math.round(percent)}%`}`;
	const accessibleText =
		percent === undefined
			? `Context window usage unknown, ${formatTokenCount(usedTokens)} of ${formatTokenCount(maxTokens)} tokens.`
			: `Context window ${Math.round(percent)} percent used, ${usedTokens} of ${maxTokens} tokens.`;

	return (
		<Tooltip label={tooltip(props)} multiline={true} withArrow={true}>
			<output
				aria-live="polite"
				data-testid="context-usage-badge"
				style={{ display: "inline-flex", alignItems: "center", gap: 6, minWidth: 96 }}
			>
				<span data-testid="context-usage-badge-label" style={{ fontSize: 12, fontVariantNumeric: "tabular-nums" }}>
					{label}
				</span>
				{percent !== undefined ? (
					<Progress size={4} radius="xl" value={Math.min(percent, 100)} color={color} aria-hidden="true" style={{ flex: 1 }} />
				) : null}
				<span style={{ position: "absolute", width: 1, height: 1, overflow: "hidden", clip: "rect(0 0 0 0)" }}>
					{accessibleText}
				</span>
			</output>
		</Tooltip>
	);
}
