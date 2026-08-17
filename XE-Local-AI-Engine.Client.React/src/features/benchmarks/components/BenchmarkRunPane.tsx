import { Alert, Button, Card, Divider, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconPlugConnected, IconPlugConnectedX, IconSquare, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { BenchmarkJudgePanel } from "@/features/benchmarks/components/BenchmarkJudgePanel";
import { BenchmarkLaunchBadges } from "@/features/benchmarks/components/BenchmarkLaunchBadges";
import { BenchmarkLaunchEvidencePanel } from "@/features/benchmarks/components/BenchmarkLaunchEvidencePanel";
import { BenchmarkScorePicker } from "@/features/benchmarks/components/BenchmarkScorePicker";
import { BenchmarkStatusBadge, BenchmarkTruncatedBadge } from "@/features/benchmarks/components/BenchmarkStatusBadge";
import type { BenchmarkOutputPart, BenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkModels";
import { isBenchmarkRunTruncated, isPrimaryActive, isRunTerminal, toChatMessageParts } from "@/features/benchmarks/models/BenchmarkModels";
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
						{truncated ? <BenchmarkTruncatedBadge /> : null}
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
