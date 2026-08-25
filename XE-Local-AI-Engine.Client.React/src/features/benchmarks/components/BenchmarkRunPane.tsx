import { Alert, Button, Card, Divider, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconPlugConnected, IconPlugConnectedX, IconSquare, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { BenchmarkJudgePanel } from "@/features/benchmarks/components/BenchmarkJudgePanel";
import { BenchmarkLaunchBadges } from "@/features/benchmarks/components/BenchmarkLaunchBadges";
import { BenchmarkLaunchEvidencePanel } from "@/features/benchmarks/components/BenchmarkLaunchEvidencePanel";
import { BenchmarkScorePicker } from "@/features/benchmarks/components/BenchmarkScorePicker";
import {
	BenchmarkIncompleteBadge,
	BenchmarkReasoningExhaustedBadge,
	BenchmarkStatusBadge,
	BenchmarkTruncatedBadge,
} from "@/features/benchmarks/components/BenchmarkStatusBadge";
import type { BenchmarkOutputPart, BenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkModels";
import {
	isBenchmarkRunIncomplete,
	isBenchmarkRunReasoningExhausted,
	isBenchmarkRunTruncated,
	isPrimaryActive,
	isRunTerminal,
	toChatMessageParts,
} from "@/features/benchmarks/models/BenchmarkModels";
import { formatLatencyMs, formatTokensPerSecond, hasThroughputBreakdown } from "@/features/benchmarks/models/BenchmarkThroughput";
import { MessageParts } from "@/features/chat/components/MessageParts";

interface BenchmarkRunPaneProps {
	run: BenchmarkRunDetail;
	parts: BenchmarkOutputPart[];
	isConnected: boolean;
	isReconnecting: boolean;
	isCancelling?: boolean;
	isScoring?: boolean;
	isJudgeBusy?: boolean;
	isDeleting?: boolean;
	onCancel: (target: "Primary" | "Judge") => void;
	onScore: (score: number) => void;
	onClearScore: () => void;
	onRejudge: () => void;
	onDelete: () => void;
}

// The split behind the single tok/s figure above. Rendered only when the runtime actually reported it — a cloud run has
// no prefill/decode timings, and a row of dashes would read as a measurement of zero rather than as no measurement.
function ThroughputBreakdown({ run }: { run: BenchmarkRunDetail }) {
	const { t } = useTranslation();
	const { ttftMs, promptTokens, promptTokensPerSecond, generationTokens, generationTokensPerSecond, cachedPromptTokens, segmentCount } =
		run.throughput;
	if (!hasThroughputBreakdown(run.throughput)) {
		return null;
	}
	const rows: { key: string; label: string; value: string }[] = [
		{ key: "ttft", label: t("pages.benchmarks.metrics.ttft", "Time to first token"), value: formatLatencyMs(ttftMs) },
		{
			key: "pp",
			label: t("pages.benchmarks.metrics.pp", "Prompt processing (pp)"),
			value: `${formatTokensPerSecond(promptTokensPerSecond)}${promptTokens === null ? "" : ` · ${promptTokens} tok`}`,
		},
		{
			key: "tg",
			label: t("pages.benchmarks.metrics.tg", "Generation (tg)"),
			value: `${formatTokensPerSecond(generationTokensPerSecond)}${generationTokens === null ? "" : ` · ${generationTokens} tok`}`,
		},
	];
	return (
		<Stack gap={2} data-testid="benchmark-throughput-breakdown">
			<Group gap="lg">
				{rows.map((row) => (
					<Text key={row.key} size="sm" data-testid={`benchmark-throughput-${row.key}`}>
						{row.label}: <b>{row.value}</b>
					</Text>
				))}
			</Group>
			{segmentCount !== null && segmentCount > 1 ? (
				<Text size="xs" c="dimmed" data-testid="benchmark-throughput-segments">
					{t("pages.benchmarks.metrics.segments", "Summed over {{count}} model requests — the agent called tools, and every round prefilled again.", {
						count: segmentCount,
					})}
				</Text>
			) : null}
			{cachedPromptTokens !== null && cachedPromptTokens > 0 ? (
				<Text size="xs" c="dimmed" data-testid="benchmark-throughput-cached">
					{t(
						"pages.benchmarks.metrics.cached",
						"{{tokens}} prompt tokens were reused from the KV cache — the prompt speed is not a cold prefill.",
						{ tokens: cachedPromptTokens },
					)}
				</Text>
			) : null}
		</Stack>
	);
}

function formatDuration(durationMs: number | null): string {
	return durationMs === null ? "—" : `${(durationMs / 1000).toFixed(1)}s`;
}

export function BenchmarkRunPane({
	run,
	parts,
	isConnected,
	isReconnecting,
	isCancelling,
	isScoring,
	isJudgeBusy,
	isDeleting,
	onCancel,
	onScore,
	onClearScore,
	onRejudge,
	onDelete,
}: BenchmarkRunPaneProps) {
	const { t } = useTranslation();
	const active = isPrimaryActive(run.primaryStatus);
	const truncated = isBenchmarkRunTruncated(run);
	const messageParts = toChatMessageParts(parts);
	return (
		<Card withBorder={true} radius="md" padding="lg" data-testid={`benchmark-run-${run.id}`}>
			<Stack gap="md">
				<Group justify="space-between" align="flex-start">
					<Stack gap={2}>
						<Title order={4}>{run.primaryModelName}</Title>
						<Text size="xs" c="dimmed">
							{t(`pages.benchmarks.origin.${run.primaryModelOrigin ?? "legacy"}`, run.primaryModelOrigin ?? "Legacy / Unknown")}
						</Text>
					</Stack>
					<Group gap="xs">
						<BenchmarkStatusBadge status={run.primaryStatus} />
						{isBenchmarkRunReasoningExhausted(run) ? (
							<BenchmarkReasoningExhaustedBadge />
						) : truncated ? (
							<BenchmarkTruncatedBadge />
						) : null}
						{isBenchmarkRunIncomplete(run) ? <BenchmarkIncompleteBadge /> : null}
					</Group>
				</Group>
				<BenchmarkLaunchBadges launch={run.primaryLaunch} data-testid="benchmark-run-launch" />
				<Group gap="lg">
					<Text size="sm">
						{t("pages.benchmarks.metrics.duration", "Duration")}: <b>{formatDuration(run.durationMs)}</b>
					</Text>
					<Text size="sm">
						{t("pages.benchmarks.metrics.tokens", "Tokens")}: <b>{run.totalTokens ?? "—"}</b>
					</Text>
					<Text size="sm">
						{t("pages.benchmarks.metrics.speed", "tok/s")}: <b>{run.tokensPerSecond?.toFixed(1) ?? "—"}</b>
					</Text>
					<Text size="sm">
						{t("pages.benchmarks.rank.quality", "Quality")}: <b>{run.qualityScore ?? "—"}</b>
					</Text>
				</Group>
				{/* What this run actually sampled with. In throughput mode it is the proof the repeats were deterministic;
				    in answer-variance mode the seed is the only thing that says which of the repeats this one is. */}
				<Group gap="lg" data-testid="benchmark-run-sampling">
					<Text size="sm">
						{t("pages.benchmarks.run.repeatMode", "Repeat mode")}:{" "}
						<b>{t(`pages.benchmarks.run.repeatModes.${run.repeatMode}`, run.repeatMode)}</b>
					</Text>
					<Text size="sm">
						{t("pages.benchmarks.run.samplingTemperature", "Temperature")}: <b>{run.samplingTemperature ?? "—"}</b>
					</Text>
					<Text size="sm">
						{t("pages.benchmarks.run.samplingSeed", "Seed")}: <b>{run.samplingSeed ?? "—"}</b>
					</Text>
				</Group>
				<ThroughputBreakdown run={run} />
				<Group gap="xs" c={isConnected ? "green" : "dimmed"}>
					{isConnected ? <IconPlugConnected size={16} /> : <IconPlugConnectedX size={16} />}
					<Text size="xs">
						{isReconnecting
							? t("pages.benchmarks.connection.reconnecting", "Reconnecting and reconciling…")
							: isConnected
								? t("pages.benchmarks.connection.live", "Live updates connected")
								: t("pages.benchmarks.connection.http", "Using durable HTTP state")}
					</Text>
				</Group>
				<Divider />
				{active && messageParts.length === 0 ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.benchmarks.run.waiting", "Waiting for model output…")}</Text>
					</Group>
				) : null}
				<MessageParts parts={messageParts} isStreaming={run.primaryStatus === "Running"} />
				{run.primaryStatus === "Failed" && run.primaryErrorMessage ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{run.primaryErrorMessage}
					</Alert>
				) : null}
				{active ? (
					<Button
						variant="light"
						color="red"
						leftSection={<IconSquare size={14} />}
						loading={isCancelling}
						onClick={() => onCancel("Primary")}
					>
						{t("pages.benchmarks.run.cancel", "Cancel run")}
					</Button>
				) : null}
				<BenchmarkScorePicker
					value={run.userScore}
					disabled={run.primaryStatus !== "Succeeded"}
					isSaving={isScoring}
					onChange={onScore}
					onClear={onClearScore}
				/>
				<BenchmarkJudgePanel
					judge={run.judge}
					primaryTruncated={truncated}
					// The DECODED token count, not run.totalTokens — that one is prompt + output + reasoning, so a long
					// question would inflate the answer length the judge is counterweighted against. Null on a run whose
					// runtime reported no timings, which renders nothing rather than an overstated number.
					outputTokens={run.throughput.generationTokens}
					canRejudge={run.primaryStatus === "Succeeded"}
					isBusy={isJudgeBusy}
					onCancel={() => onCancel("Judge")}
					onRejudge={onRejudge}
				/>
				<BenchmarkLaunchEvidencePanel run={run} />
				{isRunTerminal(run) ? (
					<Button variant="subtle" color="red" leftSection={<IconTrash size={14} />} loading={isDeleting} onClick={onDelete}>
						{t("pages.benchmarks.run.delete", "Delete terminal run")}
					</Button>
				) : null}
			</Stack>
		</Card>
	);
}
