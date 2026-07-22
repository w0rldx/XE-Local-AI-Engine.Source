import { Alert, Badge, Code, Group, Paper, ScrollArea, SimpleGrid, Stack, Tabs, Text } from "@mantine/core";
import {
	IconAlertTriangle,
	IconCode,
	IconFile,
	IconInfoCircle,
	IconListCheck,
	IconTerminal2,
	IconTool,
} from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { DevelopmentArtifact, DevelopmentAttempt, DevelopmentEvent } from "@/features/development/models/DevelopmentModels";
import type { DevelopmentAttemptLiveState } from "@/features/development/hooks/useDevelopmentAttemptHub";

interface DevelopmentLivePanelProps {
	readonly attempt: DevelopmentAttempt | null;
	readonly live: DevelopmentAttemptLiveState;
	readonly artifacts: readonly DevelopmentArtifact[];
	readonly events: readonly DevelopmentEvent[];
}

function Metric({ label, value }: { readonly label: string; readonly value: string | number }) {
	return (
		<Paper withBorder={true} p="sm">
			<Text size="xs" c="dimmed">
				{label}
			</Text>
			<Text fw={600}>{value}</Text>
		</Paper>
	);
}

export function DevelopmentLivePanel({ attempt, live, artifacts, events }: DevelopmentLivePanelProps) {
	const { t } = useTranslation();
	const latest = live.latest;
	const warnings = live.updates.filter((update) => update.kind === "Warning");
	const tools = live.updates.filter((update) => update.kind === "Tool");
	const commands = live.updates.filter((update) => update.kind === "Command");
	const output = live.updates.filter((update) => update.kind === "Output" || update.kind === "Activity");
	const validationArtifacts = artifacts.filter(
		(artifact) => artifact.kind === "ValidationReport" || artifact.kind === "ReviewReport",
	);

	return (
		<Stack gap="md" data-testid="development-live-panel">
			<Group justify="space-between">
				<Group gap="xs">
					<Badge variant="light">{attempt?.role ?? t("pages.development.live.noAttempt", "No active attempt")}</Badge>
					<Badge color={attempt?.status === "Running" ? "green" : "gray"}>{attempt?.status ?? "Idle"}</Badge>
					<Badge variant="outline">{live.connectionState}</Badge>
				</Group>
				<Text size="xs" c="dimmed">
					{t("pages.development.live.watermark", "Watermark")}: {live.watermark} ·{" "}
					{t("pages.development.live.dropped", "coalesced")}: {live.droppedOrCoalescedUpdateCount}
				</Text>
			</Group>

			<SimpleGrid cols={{ base: 2, sm: 3, lg: 6 }}>
				<Metric label={t("pages.development.live.model", "Model")} value={latest?.modelId ?? attempt?.modelId ?? "—"} />
				<Metric label={t("pages.development.live.provider", "Provider")} value={latest?.provider ?? attempt?.provider ?? "—"} />
				<Metric
					label={t("pages.development.live.speed", "Output tokens/s")}
					value={latest?.outputTokensPerSecond?.toFixed(1) ?? "—"}
				/>
				<Metric label={t("pages.development.live.rounds", "Provider rounds")} value={latest?.providerRoundCount ?? 0} />
				<Metric label={t("pages.development.live.tools", "Tool calls")} value={latest?.toolCallCount ?? 0} />
				<Metric label={t("pages.development.live.commands", "Commands")} value={latest?.commandCount ?? 0} />
				<Metric label={t("pages.development.live.files", "Files changed")} value={latest?.changedFileCount ?? 0} />
				<Metric
					label={t("pages.development.live.context", "Context headroom")}
					value={latest?.contextHeadroomPercent == null ? "—" : `${latest.contextHeadroomPercent.toFixed(0)}%`}
				/>
				<Metric
					label={t("pages.development.live.noProgress", "No-progress age")}
					value={`${latest?.secondsSinceMeaningfulProgress ?? 0}s`}
				/>
				<Metric
					label={t("pages.development.live.inputTokens", "Input tokens")}
					value={latest?.inputTokens ?? attempt?.inputTokens ?? 0}
				/>
				<Metric
					label={t("pages.development.live.outputTokens", "Output tokens")}
					value={latest?.outputTokens ?? attempt?.outputTokens ?? 0}
				/>
				<Metric label={t("pages.development.live.reasoningTokens", "Reasoning tokens")} value={latest?.reasoningTokens ?? 0} />
			</SimpleGrid>

			{warnings.map((warning) => (
				<Alert
					key={warning.sequence}
					icon={<IconAlertTriangle size={16} />}
					color="yellow"
					title={warning.warningCategory ?? "Warning"}
				>
					{warning.warningMessage}
				</Alert>
			))}

			<Tabs defaultValue="live">
				<Tabs.List>
					<Tabs.Tab value="live" leftSection={<IconCode size={14} />}>
						Live
					</Tabs.Tab>
					<Tabs.Tab value="tools" leftSection={<IconTool size={14} />}>
						Tools
					</Tabs.Tab>
					<Tabs.Tab value="commands" leftSection={<IconTerminal2 size={14} />}>
						Commands
					</Tabs.Tab>
					<Tabs.Tab value="files" leftSection={<IconFile size={14} />}>
						Files
					</Tabs.Tab>
					<Tabs.Tab value="validation" leftSection={<IconListCheck size={14} />}>
						Validation
					</Tabs.Tab>
					<Tabs.Tab value="details" leftSection={<IconInfoCircle size={14} />}>
						Details
					</Tabs.Tab>
				</Tabs.List>

				<Tabs.Panel value="live" pt="md">
					<ScrollArea h={260}>
						<Stack gap="xs">
							{output.length === 0 ? <Text c="dimmed">No live output yet.</Text> : null}
							{output.map((update) => (
								<Paper key={update.sequence} withBorder={true} p="xs">
									<Text size="xs" c="dimmed">
										#{update.sequence} · {update.kind}
									</Text>
									<Text>{update.outputDelta ?? update.currentActivity ?? "Activity updated"}</Text>
								</Paper>
							))}
						</Stack>
					</ScrollArea>
				</Tabs.Panel>
				<Tabs.Panel value="tools" pt="md">
					<Stack gap="xs">
						{tools.length === 0 ? <Text c="dimmed">No tool activity yet.</Text> : null}
						{tools.map((update) => (
							<Code key={update.sequence}>
								{update.currentToolId ?? "tool"} · {update.currentActivity ?? update.status}
							</Code>
						))}
					</Stack>
				</Tabs.Panel>
				<Tabs.Panel value="commands" pt="md">
					<Stack gap="xs">
						{commands.length === 0 ? <Text c="dimmed">No command activity yet.</Text> : null}
						{commands.map((update) => (
							<Code key={update.sequence}>
								{update.currentCommandId ?? "command"} · {update.currentActivity ?? update.status}
							</Code>
						))}
					</Stack>
				</Tabs.Panel>
				<Tabs.Panel value="files" pt="md">
					<Stack gap="xs">
						<Text>
							{latest?.changedFileCount ?? 0} file(s) changed; patch size {latest?.patchByteCount ?? 0} bytes.
						</Text>
						{artifacts
							.filter((artifact) => artifact.kind === "ChangedFilesManifest" || artifact.kind === "Patch")
							.map((artifact) => (
								<Code key={artifact.id}>
									{artifact.kind} · {artifact.contentHash?.slice(0, 12)} · {artifact.byteCount ?? 0} bytes
								</Code>
							))}
					</Stack>
				</Tabs.Panel>
				<Tabs.Panel value="validation" pt="md">
					<Stack gap="xs">
						{validationArtifacts.length === 0 ? <Text c="dimmed">No validation or review evidence yet.</Text> : null}
						{validationArtifacts.map((artifact) => (
							<Group key={artifact.id} justify="space-between">
								<Text>{artifact.kind}</Text>
								<Badge color={artifact.isValid ? "green" : "red"}>{artifact.isValid ? "Current" : "Invalidated"}</Badge>
							</Group>
						))}
					</Stack>
				</Tabs.Panel>
				<Tabs.Panel value="details" pt="md">
					<Stack gap="xs">
						<Text size="sm">Attempt: {attempt?.id ?? "—"}</Text>
						<Text size="sm">Predecessor: {attempt?.predecessorAttemptId ?? "—"}</Text>
						<Text size="sm">Current activity: {latest?.currentActivity ?? "—"}</Text>
						<Text size="sm">Subject: {latest?.subjectHash?.slice(0, 16) ?? "—"}</Text>
						<Text size="sm">Durable events: {events.length}</Text>
					</Stack>
				</Tabs.Panel>
			</Tabs>
		</Stack>
	);
}
