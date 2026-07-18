import { Progress, Tooltip } from "@mantine/core";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";

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

function tooltip({ isAuthoritative, maxTokens, modelLabel, nodeLabel, usedTokens }: ContextUsageModel, t: TFunction): string {
	const usedOrigin =
		usedTokens === undefined
			? t("pages.chat.contextUsage.usedPending", "Used context will update after the assistant response completes")
			: isAuthoritative
				? t("pages.chat.contextUsage.usedAuthoritative", "Used context reported by model")
				: t("pages.chat.contextUsage.usedNotAuthoritative", "Used context not yet authoritative");
	const maxOrigin =
		maxTokens === undefined
			? t("pages.chat.contextUsage.maxUnknown", "Max context unknown for this model on this node")
			: t("pages.chat.contextUsage.maxKnown", "Max context reported by selected node");

	return t("pages.chat.contextUsage.tooltip", "{{usedOrigin}}. {{maxOrigin}}. Model: {{model}}. Node: {{node}}.", {
		usedOrigin,
		maxOrigin,
		model: modelLabel || t("pages.chat.contextUsage.unknownModel", "Unknown"),
		node: nodeLabel || t("pages.chat.contextUsage.localNode", "Local node"),
	});
}

export function ContextUsageBadge(props: ContextUsageModel) {
	const { t } = useTranslation();
	const { usedTokens, maxTokens } = props;
	const percent =
		usedTokens !== undefined && maxTokens !== undefined && maxTokens > 0 ? (usedTokens / maxTokens) * 100 : undefined;
	const color = percent === undefined ? "gray" : percent >= 90 ? "red" : percent >= 70 ? "yellow" : "green";
	const label = `${formatTokenCount(usedTokens)}/${formatTokenCount(maxTokens)}${percent === undefined ? "" : ` ${Math.round(percent)}%`}`;
	const accessibleText =
		percent === undefined
			? t("pages.chat.contextUsage.srUnknown", "Context window usage unknown, {{used}} of {{max}} tokens.", {
					used: formatTokenCount(usedTokens),
					max: formatTokenCount(maxTokens),
				})
			: t("pages.chat.contextUsage.srUsed", "Context window {{percent}} percent used, {{used}} of {{max}} tokens.", {
					percent: Math.round(percent),
					used: usedTokens,
					max: maxTokens,
				});

	return (
		<Tooltip label={tooltip(props, t)} multiline={true} withArrow={true}>
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
