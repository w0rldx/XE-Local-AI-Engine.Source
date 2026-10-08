import { Badge, Box, Group, Popover, Progress, ScrollArea, Stack, Text, UnstyledButton } from "@mantine/core";
import type { TFunction } from "i18next";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";

import { ContextUsageBadge } from "@/features/chat/components/ContextUsageBadge";
import {
	type ContextSectionKey,
	type ContextSource,
	computeContextSections,
	contextSource,
	groupTools,
	hasKnownWindow,
	isSnapshotForModel,
	remainingTokens,
	type ToolGroup,
} from "@/features/chat/components/ContextUsagePopover/ContextUsageBreakdown";
import type { ContextUsageModel } from "@/features/chat/models/ChatModels";
import { toWireSamplingOptions } from "@/features/chat/models/ChatSamplingOptions";
import type { ContextWindowSnapshot } from "@/features/chat/models/ContextWindowModels";
import { formatTokenCount } from "@/features/chat/models/TokenCountFormatting";
import { useContextEstimate } from "@/features/chat/queries/useContextEstimate";
import { useMcpServerNames } from "@/features/chat/queries/useMcpServerNames";
import { useChatSamplingPreferencesStore } from "@/features/chat/stores/ChatSamplingPreferencesStore";

const sectionColors: Record<ContextSectionKey, string> = {
	systemPrompt: "blue",
	instructions: "indigo",
	tools: "violet",
	knowledge: "teal",
	attachments: "cyan",
	compaction: "orange",
	conversation: "green",
	used: "green",
	reservedOutput: "gray.6",
	safetyMargin: "gray.4",
	free: "gray.2",
};

const sourceColors: Record<ContextSource, string> = { reported: "green", estimated: "yellow", unknown: "gray" };

function sectionLabels(t: TFunction): Record<ContextSectionKey, string> {
	return {
		systemPrompt: t("pages.chat.contextUsage.section.systemPrompt", "System prompt"),
		instructions: t("pages.chat.contextUsage.section.instructions", "Instructions"),
		tools: t("pages.chat.contextUsage.section.tools", "Tools (schema and preamble)"),
		knowledge: t("pages.chat.contextUsage.section.knowledge", "Knowledge"),
		attachments: t("pages.chat.contextUsage.section.attachments", "Attachments"),
		compaction: t("pages.chat.contextUsage.section.compaction", "Compaction summary"),
		conversation: t("pages.chat.contextUsage.section.conversation", "Conversation"),
		used: t("pages.chat.contextUsage.section.used", "Last request"),
		reservedOutput: t("pages.chat.contextUsage.section.reservedOutput", "Reserved for output"),
		safetyMargin: t("pages.chat.contextUsage.section.safetyMargin", "Safety margin"),
		free: t("pages.chat.contextUsage.section.free", "Free"),
	};
}

function sourceLabels(t: TFunction): Record<ContextSource, string> {
	return {
		reported: t("pages.chat.contextUsage.source.reported", "Reported by model"),
		estimated: t("pages.chat.contextUsage.source.estimated", "Estimated"),
		unknown: t("pages.chat.contextUsage.source.unknown", "Unknown"),
	};
}

function groupLabel(group: ToolGroup, serverNames: ReadonlyMap<string, string>, t: TFunction): string {
	const server = group.server === undefined ? undefined : (serverNames.get(group.server) ?? group.server);
	const labels: Record<ToolGroup["kind"], string> = {
		builtIn: t("pages.chat.contextUsage.toolGroup.builtIn", "Built-in"),
		mcp: t("pages.chat.contextUsage.toolGroup.mcp", "MCP: {{server}}", { server }),
		custom: t("pages.chat.contextUsage.toolGroup.custom", "Custom"),
		skills: t("pages.chat.contextUsage.toolGroup.skills", "Skills"),
	};
	return labels[group.kind];
}

function TokenRow({ label, value, testId, color }: { label: string; value: string; testId: string; color?: string }) {
	return (
		<Group justify="space-between" gap="xs" wrap="nowrap" data-testid={testId}>
			<Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
				{color ? <Box w={8} h={8} bg={color} style={{ borderRadius: 2, flexShrink: 0 }} aria-hidden="true" /> : null}
				<Text size="xs" truncate="end">
					{label}
				</Text>
			</Group>
			<Text size="xs" c="dimmed" style={{ fontVariantNumeric: "tabular-nums", flexShrink: 0 }}>
				{value}
			</Text>
		</Group>
	);
}

const noServerNames: ReadonlyMap<string, string> = new Map();

function SnapshotDetails({ snapshot }: { snapshot: ContextWindowSnapshot }) {
	const { t } = useTranslation();
	const windowKnown = hasKnownWindow(snapshot);
	const sections = computeContextSections(snapshot);
	const labels = sectionLabels(t);
	const remaining = remainingTokens(snapshot);
	const toolGroups = groupTools(snapshot.tools);
	// Display names only matter when an MCP group is listed; otherwise the server list is never asked for.
	const serverNames = useMcpServerNames({ enabled: toolGroups.some((group) => group.kind === "mcp") }).data ?? noServerNames;
	const trimmed = snapshot.trimmed;
	const unknown = t("pages.chat.contextUsage.unknownValue", "unknown");
	const tokensWithPercent = (tokens: number, percent: number) =>
		windowKnown ? `${formatTokenCount(tokens)} · ${Math.round(percent)}%` : formatTokenCount(tokens);

	return (
		<Stack gap="xs">
			<TokenRow
				label={t("pages.chat.contextUsage.window", "Context window")}
				value={windowKnown ? formatTokenCount(snapshot.windowTokens) : unknown}
				testId="context-usage-window"
			/>
			{windowKnown ? (
				<Progress.Root size="lg" data-testid="context-usage-sections">
					{sections.map((section) => (
						<Progress.Section
							key={section.key}
							value={section.percent}
							color={sectionColors[section.key]}
							aria-label={labels[section.key]}
							data-testid={`context-usage-section-${section.key}`}
						/>
					))}
				</Progress.Root>
			) : null}
			<Stack gap={2}>
				{sections.map((section) => (
					<TokenRow
						key={section.key}
						label={labels[section.key]}
						value={tokensWithPercent(section.tokens, section.percent)}
						color={sectionColors[section.key]}
						testId={`context-usage-row-${section.key}`}
					/>
				))}
				{snapshot.providerInputTokens != null && snapshot.estimated ? (
					<TokenRow
						label={t("pages.chat.contextUsage.reportedInput", "Reported input (last request)")}
						value={formatTokenCount(snapshot.providerInputTokens)}
						testId="context-usage-reported-input"
					/>
				) : null}
				<TokenRow
					label={t("pages.chat.contextUsage.remaining", "Remaining")}
					value={remaining === undefined ? unknown : formatTokenCount(remaining)}
					testId="context-usage-remaining"
				/>
			</Stack>
			{toolGroups.length > 0 ? (
				<details data-testid="context-usage-tools">
					<summary style={{ cursor: "pointer" }}>
						<Text component="span" size="xs" fw={500}>
							{t("pages.chat.contextUsage.toolsSent", "Tools sent ({{count}})", { count: snapshot.tools.length })}
						</Text>
					</summary>
					<Stack gap={6} mt={4}>
						{toolGroups.map((group) => (
							<Stack key={`${group.kind}:${group.server ?? ""}`} gap={2} data-testid={`context-usage-tool-group-${group.kind}`}>
								<TokenRow
									label={groupLabel(group, serverNames, t)}
									value={formatTokenCount(group.tokens)}
									testId="context-usage-tool-group-total"
								/>
								{group.tools.map((tool) => (
									<Box key={tool.name} pl="sm">
										<TokenRow label={tool.displayName} value={formatTokenCount(tool.tokens)} testId="context-usage-tool" />
									</Box>
								))}
							</Stack>
						))}
					</Stack>
				</details>
			) : null}
			{snapshot.toolsWithheldCount > 0 ? (
				<Text size="xs" c="dimmed" data-testid="context-usage-withheld">
					{t("pages.chat.contextUsage.withheld", "{{count}} tools withheld by relevance filter", {
						count: snapshot.toolsWithheldCount,
					})}
				</Text>
			) : null}
			{trimmed && (trimmed.messagesDropped > 0 || trimmed.toolResultsTruncated > 0 || trimmed.reasoningStripped > 0) ? (
				<Stack gap={2} data-testid="context-usage-trimmed">
					{trimmed.messagesDropped > 0 ? (
						<Text size="xs" c="dimmed">
							{t("pages.chat.contextUsage.trimmed.messagesDropped", "{{count}} older messages dropped", {
								count: trimmed.messagesDropped,
							})}
						</Text>
					) : null}
					{trimmed.toolResultsTruncated > 0 ? (
						<Text size="xs" c="dimmed">
							{t("pages.chat.contextUsage.trimmed.toolResultsTruncated", "{{count}} tool results truncated", {
								count: trimmed.toolResultsTruncated,
							})}
						</Text>
					) : null}
					{trimmed.reasoningStripped > 0 ? (
						<Text size="xs" c="dimmed">
							{t("pages.chat.contextUsage.trimmed.reasoningStripped", "{{count}} reasoning blocks stripped", {
								count: trimmed.reasoningStripped,
							})}
						</Text>
					) : null}
				</Stack>
			) : null}
		</Stack>
	);
}

interface ContextUsagePopoverProps {
	usage: ContextUsageModel;
	// The model and agent the next request resolves; they key the pre-send estimate.
	modelName: string;
	agentId?: string;
	// The composer's local-tools toggle: with it off the next request sends no local tools, so the estimate omits them.
	useLocalTools: boolean;
}

// The composer's context badge as a popover trigger: the last provider round's breakdown, or before the first response
// a labelled estimate of the fixed parts for the selected model and agent.
export function ContextUsagePopover({ usage, modelName, agentId, useLocalTools }: ContextUsagePopoverProps) {
	const { t } = useTranslation();
	const [opened, setOpened] = useState(false);
	// A last round of another model says nothing about the selected one: fall back to the selected model's estimate.
	const lastRound = usage.contextWindow && isSnapshotForModel(usage.contextWindow, modelName) ? usage.contextWindow : undefined;
	// The send path's rule: developer-mode overrides ride the turn, and the output reserve and window change the estimate.
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const samplingOptions = useChatSamplingPreferencesStore((state) => state.options);
	const wire = developerMode ? toWireSamplingOptions(samplingOptions) : undefined;
	const estimateQuery = useContextEstimate(
		{ modelName, agentId, useLocalTools, maxOutputTokens: wire?.maxOutputTokens, numCtx: wire?.numCtx },
		{ enabled: opened && lastRound === undefined },
	);
	const snapshot = lastRound ?? estimateQuery.data ?? undefined;
	const source = contextSource(snapshot);
	// The served model names the numbers; the picker label follows on the second line when it reads differently.
	const title = snapshot?.modelId || usage.modelLabel || t("pages.chat.contextUsage.unknownModel", "Unknown");
	const pickerLabel = usage.modelLabel && usage.modelLabel.toLowerCase() !== title.toLowerCase() ? usage.modelLabel : undefined;
	const subtitle = [usage.agentLabel, pickerLabel].filter(Boolean).join(" · ");

	return (
		<Popover
			opened={opened}
			onChange={setOpened}
			position="bottom-start"
			shadow="md"
			width={340}
			trapFocus={true}
			withinPortal={true}
		>
			<Popover.Target>
				<UnstyledButton
					type="button"
					aria-label={t("pages.chat.contextUsage.trigger", "Show context window usage")}
					aria-expanded={opened}
					onClick={() => setOpened((previous) => !previous)}
					data-testid="context-usage-trigger"
				>
					<ContextUsageBadge {...usage} />
				</UnstyledButton>
			</Popover.Target>
			<Popover.Dropdown data-testid="context-usage-dropdown">
				{/* An expanded tool list (up to 64 rows) must stay reachable: the body scrolls inside a viewport-bounded height. */}
				<ScrollArea.Autosize mah="min(70vh, 560px)" type="auto" data-testid="context-usage-scroll">
					<Stack gap="sm">
						<Group justify="space-between" gap="xs" wrap="nowrap">
							<Stack gap={0} style={{ minWidth: 0 }}>
								<Text size="sm" fw={500} truncate="end" data-testid="context-usage-model">
									{title}
								</Text>
								{subtitle ? (
									<Text size="xs" c="dimmed" truncate="end" data-testid="context-usage-subtitle">
										{subtitle}
									</Text>
								) : null}
							</Stack>
							<Badge size="xs" variant="light" color={sourceColors[source]} data-testid="context-usage-source">
								{sourceLabels(t)[source]}
							</Badge>
						</Group>
						{snapshot ? (
							<SnapshotDetails snapshot={snapshot} />
						) : (
							<Stack gap="xs" data-testid="context-usage-empty">
								<Text size="xs" c="dimmed">
									{estimateQuery.isError
										? t("pages.chat.contextUsage.estimateFailed", "The estimate could not be loaded.")
										: t("pages.chat.contextUsage.afterFirstResponse", "Will update after the first response.")}
								</Text>
								<TokenRow
									label={t("pages.chat.contextUsage.window", "Context window")}
									value={
										usage.maxTokens === undefined
											? t("pages.chat.contextUsage.unknownValue", "unknown")
											: formatTokenCount(usage.maxTokens)
									}
									testId="context-usage-window"
								/>
							</Stack>
						)}
						<Text size="xs" c="dimmed" data-testid="context-usage-footer">
							{source === "reported"
								? t(
										"pages.chat.contextUsage.footer.reported",
										"The total comes from the model's report for the last request. The categories are estimates.",
									)
								: t(
										"pages.chat.contextUsage.footer.estimated",
										"All values are estimates until the model reports usage for a request.",
									)}
						</Text>
					</Stack>
				</ScrollArea.Autosize>
			</Popover.Dropdown>
		</Popover>
	);
}
