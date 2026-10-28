import { ActionIcon, Badge, Button, CopyButton, Group, Loader, SimpleGrid, Stack, Text, Title, Tooltip } from "@mantine/core";
import { IconCheck, IconCopy, IconHistory, IconPlayerPlay, IconRefresh } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { getInvocationMonitorOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { ResponsiveTable } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { toInvocationMonitor } from "@/features/invocations/models/InvocationMonitorMappers";
import type { InvocationCurrentDto, InvocationHistoryDto } from "@/features/invocations/models/InvocationMonitorModel";
import {
	buildInvocationSummary,
	formatInvocationDuration,
	formatInvocationText,
	formatInvocationTimestamp,
	getInvocationErrorText,
	getInvocationStatusBadge,
	isInvocationActive,
	sortInvocationHistory,
} from "@/features/invocations/models/InvocationMonitorModel";

// Copyable W3C trace id so a failed run's "See local logs" row correlates with the exported trace. Renders
// nothing when no trace id was captured (legacy/platform runs). Uses the app's idiomatic CopyButton affordance.
function TraceIdLine({ traceId }: { readonly traceId: string | null }) {
	const { t } = useTranslation();
	if (!traceId) {
		return null;
	}

	return (
		<Group gap={4} align="center" wrap="nowrap" data-testid="invocation-trace-id">
			<Text size="xs" c="dimmed" style={{ wordBreak: "break-all" }}>
				{t("pages.invocations.monitor.traceId.label", "Trace: {{traceId}}", { traceId })}
			</Text>
			<CopyButton value={traceId} timeout={2000}>
				{({ copied, copy }) => (
					<Tooltip
						label={
							copied
								? t("pages.invocations.monitor.traceId.copied", "Copied")
								: t("pages.invocations.monitor.traceId.copy", "Copy trace id")
						}
						withArrow={true}
					>
						<ActionIcon
							color={copied ? "teal" : "gray"}
							variant="subtle"
							size="sm"
							onClick={copy}
							aria-label={t("pages.invocations.monitor.traceId.copy", "Copy trace id")}
							data-testid="invocation-trace-id-copy"
						>
							{copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
						</ActionIcon>
					</Tooltip>
				)}
			</CopyButton>
		</Group>
	);
}

function CurrentInvocation({ current, title }: { readonly current: InvocationCurrentDto | null; readonly title?: string }) {
	const { t } = useTranslation();
	const heading = title ?? t("pages.invocations.monitor.current.title", "Current invocation");
	if (!current) {
		return (
			<SectionCard
				title={t("pages.invocations.monitor.current.title", "Current invocation")}
				icon={<IconPlayerPlay size={22} />}
				gap="sm"
			>
				<EmptyState message={t("pages.invocations.monitor.current.empty", "No invocation is currently assigned or running.")} />
			</SectionCard>
		);
	}

	return (
		<SectionCard>
			<Group justify="space-between" align="flex-start">
				<Stack gap={4}>
					<Title order={2} size="h3">
						{heading}
					</Title>
					<Text size="sm" c="dimmed" style={{ wordBreak: "break-all" }}>
						{current.invocationId}
					</Text>
					<TraceIdLine traceId={current.traceId} />
				</Stack>
				<InvocationStatusBadge run={current} />
			</Group>
			<Text fw={500} data-testid="invocation-summary">
				{buildInvocationSummary(current, t)}
			</Text>
			<SimpleGrid cols={{ base: 1, md: 2 }} spacing="sm">
				<Text>
					{t("pages.invocations.monitor.current.model", "Model: {{value}}", { value: formatInvocationText(current.modelUsed) })}
				</Text>
				<Text>
					{t("pages.invocations.monitor.current.conversation", "Conversation: {{value}}", { value: current.conversationId })}
				</Text>
				<Text>
					{t("pages.invocations.monitor.current.started", "Started: {{value}}", {
						value: formatInvocationTimestamp(current.startedAt),
					})}
				</Text>
				<Text>
					{t("pages.invocations.monitor.current.updated", "Updated: {{value}}", {
						value: formatInvocationTimestamp(current.lastUpdatedAt),
					})}
				</Text>
				<Text>
					{t("pages.invocations.monitor.current.outputChunks", "Output chunks: {{value}}", {
						value: current.streamedChunkCount,
					})}
				</Text>
				<Text>
					{t("pages.invocations.monitor.current.thinkingChunks", "Thinking chunks: {{value}}", {
						value: current.streamedThinkingChunkCount,
					})}
				</Text>
				<Text>
					{t("pages.invocations.monitor.current.pendingToolCalls", "Pending tool calls: {{value}}", {
						value: current.pendingToolCallCount,
					})}
				</Text>
				<Text>
					{t("pages.invocations.monitor.current.pendingApproval", "Pending approval: {{value}}", {
						value: current.hasPendingApproval
							? t("pages.invocations.monitor.current.yes", "Yes")
							: t("pages.invocations.monitor.current.no", "No"),
					})}
				</Text>
				<Text>
					{t("pages.invocations.monitor.current.pendingQuestion", "Pending question: {{value}}", {
						value: current.hasPendingQuestion
							? t("pages.invocations.monitor.current.yes", "Yes")
							: t("pages.invocations.monitor.current.no", "No"),
					})}
				</Text>
			</SimpleGrid>
			{current.error ? <InlineErrorAlert message={getInvocationErrorText(current, t) ?? current.error} /> : null}
		</SectionCard>
	);
}

function HistoryTable({ history }: { readonly history: InvocationHistoryDto[] }) {
	const { t } = useTranslation();

	return (
		<ResponsiveTable
			rows={history}
			rowKey={(entry) => entry.invocationId}
			minWidth={980}
			empty={<EmptyState message={t("pages.invocations.monitor.history.empty", "No completed invocations recorded yet.")} />}
			columns={[
				{
					key: "invocation",
					header: t("pages.invocations.monitor.history.columns.invocation", "Invocation"),
					primary: true,
					cell: (entry) => (
						<>
							<Text size="sm" style={{ wordBreak: "break-all" }}>
								{entry.invocationId}
							</Text>
							<TraceIdLine traceId={entry.traceId} />
						</>
					),
				},
				{
					key: "status",
					header: t("pages.invocations.monitor.history.columns.status", "Status"),
					cell: (entry) => <InvocationStatusBadge run={entry} />,
				},
				{
					key: "model",
					header: t("pages.invocations.monitor.history.columns.model", "Model"),
					cell: (entry) => formatInvocationText(entry.modelUsed),
				},
				{
					key: "completed",
					header: t("pages.invocations.monitor.history.columns.completed", "Completed"),
					cell: (entry) => formatInvocationTimestamp(entry.completedAt),
				},
				{
					key: "duration",
					header: t("pages.invocations.monitor.history.columns.duration", "Duration"),
					cell: (entry) => formatInvocationDuration(entry.durationMs),
				},
				{
					key: "chunks",
					header: t("pages.invocations.monitor.history.columns.chunks", "Chunks"),
					cell: (entry) => entry.streamedChunkCount,
				},
				{
					key: "thinking",
					header: t("pages.invocations.monitor.history.columns.thinking", "Thinking"),
					cell: (entry) => entry.streamedThinkingChunkCount,
				},
				{
					key: "result",
					header: t("pages.invocations.monitor.history.columns.result", "Result"),
					cell: (entry) => formatInvocationText(getInvocationErrorText(entry, t)),
				},
			]}
		/>
	);
}

function InvocationStatusBadge({ run }: { run: InvocationCurrentDto | InvocationHistoryDto }) {
	const { t } = useTranslation();
	const badge = getInvocationStatusBadge(run, t);
	return <Badge color={badge.color}>{badge.label}</Badge>;
}

export function Invocations() {
	const { t } = useTranslation();
	const {
		data: monitor,
		isLoading: monitorIsLoading,
		error: monitorError,
		refetch: monitorRefetch,
		isFetching: monitorIsFetching,
	} = useQuery({
		...getInvocationMonitorOptions(),
		// Case (d): chat in another tab, the scheduler, integrations and agents start invocations outside this page, and
		// no hub announces them, so an idle monitor cannot tell that nothing will start.
		refetchInterval: 5000,
		select: toInvocationMonitor,
	});
	const history = useMemo(() => sortInvocationHistory(monitor?.history ?? []), [monitor]);
	const active = isInvocationActive(monitor?.current?.status);
	// Live turns besides the current one, such as a turn parked on an approval or a question.
	const otherLive = (monitor?.live ?? []).filter((row) => row.invocationId !== monitor?.current?.invocationId);

	return (
		<PageShell>
			<PageHeader
				icon={<IconPlayerPlay size={24} />}
				title={t("pages.invocations.monitor.title", "Invocation monitor")}
				subtitle={t(
					"pages.invocations.monitor.subtitle",
					"Inspect the active invocation and the local in-memory history retained by the worker.",
				)}
				actions={
					<>
						<Badge color={active ? "blue" : "gray"}>
							{active ? t("pages.invocations.monitor.active", "Active") : t("pages.invocations.monitor.idle", "Idle")}
						</Badge>
						<Button
							variant="subtle"
							leftSection={<IconRefresh size={16} />}
							onClick={() => monitorRefetch()}
							disabled={monitorIsFetching}
						>
							{t("common.refresh", "Refresh")}
						</Button>
					</>
				}
			/>

			{monitorIsLoading ? (
				<Group gap="sm">
					<Loader size="sm" />
					<Text c="dimmed">{t("pages.invocations.monitor.loading", "Loading invocation monitor…")}</Text>
				</Group>
			) : null}

			{monitorError ? (
				<InlineErrorAlert
					message={apiErrorMessage(
						monitorError,
						t("pages.invocations.monitor.loadError", "Invocation monitor data could not be loaded."),
					)}
				/>
			) : null}

			<CurrentInvocation current={monitor?.current ?? null} />
			{otherLive.map((row) => (
				<CurrentInvocation
					key={row.invocationId}
					current={row}
					title={t("pages.invocations.monitor.live.title", "Other live invocation")}
				/>
			))}

			<SectionCard
				title={t("pages.invocations.monitor.history.title", "Invocation history")}
				icon={<IconHistory size={22} />}
				gap="sm"
			>
				<Text size="sm" c="dimmed">
					{t("pages.invocations.monitor.history.subtitle", "Showing up to {{count}} most recent terminal invocations.", {
						count: monitor?.historyCapacity ?? 0,
					})}
				</Text>
				<HistoryTable history={history} />
			</SectionCard>
		</PageShell>
	);
}
