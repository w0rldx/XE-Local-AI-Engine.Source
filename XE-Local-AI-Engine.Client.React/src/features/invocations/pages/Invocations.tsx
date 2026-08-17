import {
	ActionIcon,
	Alert,
	Badge,
	Button,
	CopyButton,
	Group,
	Loader,
	SimpleGrid,
	Stack,
	Table,
	Text,
	Title,
	Tooltip,
} from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconCopy, IconHistory, IconPlayerPlay, IconRefresh } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { getInvocationMonitorOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { toInvocationMonitor } from "@/features/invocations/models/InvocationMonitorMappers";
import type { InvocationCurrentDto, InvocationHistoryDto } from "@/features/invocations/models/InvocationMonitorModel";
import {
	buildInvocationSummary,
	formatInvocationDuration,
	formatInvocationText,
	formatInvocationTimestamp,
	getInvocationStatusColor,
	isInvocationActive,
	sortInvocationHistory,
} from "@/features/invocations/models/InvocationMonitorModel";

// Routed through the canonical helper rather than reading `error.message` directly: a request that never reached the
// node arrives as a NetworkError whose message is deliberately empty, so the raw read rendered a BLANK alert where it
// used to at least say something. apiErrorMessage answers that case with the localized "can't reach the node"
// sentence and keeps the server's own text for every other failure.
function errorMessage(error: unknown, t: TFunction): string {
	return apiErrorMessage(error, t("pages.invocations.monitor.loadError", "Invocation monitor data could not be loaded."));
}

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

function CurrentInvocation({ current }: { readonly current: InvocationCurrentDto | null }) {
	const { t } = useTranslation();
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
					<Title order={3}>{t("pages.invocations.monitor.current.title", "Current invocation")}</Title>
					<Text size="sm" c="dimmed" style={{ wordBreak: "break-all" }}>
						{current.invocationId}
					</Text>
					<TraceIdLine traceId={current.traceId} />
				</Stack>
				<Badge color={getInvocationStatusColor(current.status)}>{current.status}</Badge>
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
			{current.error ? (
				<Alert color="red" icon={<IconAlertTriangle size={16} />}>
					{current.error}
				</Alert>
			) : null}
		</SectionCard>
	);
}

function HistoryRows({ history }: { readonly history: InvocationHistoryDto[] }) {
	const { t } = useTranslation();
	if (history.length === 0) {
		return (
			<Table.Tr>
				<Table.Td colSpan={8}>
					<EmptyState message={t("pages.invocations.monitor.history.empty", "No completed invocations recorded yet.")} />
				</Table.Td>
			</Table.Tr>
		);
	}

	return history.map((entry) => (
		<Table.Tr key={entry.invocationId}>
			<Table.Td>
				<Text size="sm" style={{ wordBreak: "break-all" }}>
					{entry.invocationId}
				</Text>
				<TraceIdLine traceId={entry.traceId} />
			</Table.Td>
			<Table.Td>
				<Badge color={getInvocationStatusColor(entry.status)}>{entry.status}</Badge>
			</Table.Td>
			<Table.Td>{formatInvocationText(entry.modelUsed)}</Table.Td>
			<Table.Td>{formatInvocationTimestamp(entry.completedAt)}</Table.Td>
			<Table.Td>{formatInvocationDuration(entry.durationMs)}</Table.Td>
			<Table.Td>{entry.streamedChunkCount}</Table.Td>
			<Table.Td>{entry.streamedThinkingChunkCount}</Table.Td>
			<Table.Td>{formatInvocationText(entry.error ?? entry.failureCategory)}</Table.Td>
		</Table.Tr>
	));
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
		...withResponseValidation(getInvocationMonitorOptions()),
		refetchInterval: 5000,
		select: toInvocationMonitor,
	});
	const history = useMemo(() => sortInvocationHistory(monitor?.history ?? []), [monitor]);
	const active = isInvocationActive(monitor?.current?.status);

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
				<Alert color="red" icon={<IconAlertTriangle size={16} />}>
					{errorMessage(monitorError, t)}
				</Alert>
			) : null}

			<CurrentInvocation current={monitor?.current ?? null} />

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
				<Table.ScrollContainer minWidth={980}>
					<Table striped={true} highlightOnHover={true} verticalSpacing="sm">
						<Table.Thead>
							<Table.Tr>
								<Table.Th>{t("pages.invocations.monitor.history.columns.invocation", "Invocation")}</Table.Th>
								<Table.Th>{t("pages.invocations.monitor.history.columns.status", "Status")}</Table.Th>
								<Table.Th>{t("pages.invocations.monitor.history.columns.model", "Model")}</Table.Th>
								<Table.Th>{t("pages.invocations.monitor.history.columns.completed", "Completed")}</Table.Th>
								<Table.Th>{t("pages.invocations.monitor.history.columns.duration", "Duration")}</Table.Th>
								<Table.Th>{t("pages.invocations.monitor.history.columns.chunks", "Chunks")}</Table.Th>
								<Table.Th>{t("pages.invocations.monitor.history.columns.thinking", "Thinking")}</Table.Th>
								<Table.Th>{t("pages.invocations.monitor.history.columns.result", "Result")}</Table.Th>
							</Table.Tr>
						</Table.Thead>
						<Table.Tbody>
							<HistoryRows history={history} />
						</Table.Tbody>
					</Table>
				</Table.ScrollContainer>
			</SectionCard>
		</PageShell>
	);
}
