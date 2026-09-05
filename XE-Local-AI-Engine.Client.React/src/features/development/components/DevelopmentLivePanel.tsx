import { Alert, Badge, Code, Divider, Group, Paper, ScrollArea, SimpleGrid, Stack, Tabs, Text } from "@mantine/core";
import {
	IconAlertTriangle,
	IconCode,
	IconFile,
	IconInfoCircle,
	IconListCheck,
	IconTerminal2,
	IconTool,
} from "@tabler/icons-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import {
	ArtifactContentView,
	ArtifactViewButton,
	Metric,
	ValidationReportView,
} from "@/features/development/components/DevelopmentLivePresenters";
import type { DevelopmentAttemptLiveState } from "@/features/development/hooks/useDevelopmentAttemptHub";
import type { DevelopmentArtifact, DevelopmentAttempt, DevelopmentEvent } from "@/features/development/models/DevelopmentModels";

interface DevelopmentLivePanelProps {
	readonly attempt: DevelopmentAttempt | null;
	readonly live: DevelopmentAttemptLiveState;
	readonly artifacts: readonly DevelopmentArtifact[];
	readonly events: readonly DevelopmentEvent[];
}

export function DevelopmentLivePanel({ attempt, live, artifacts, events }: DevelopmentLivePanelProps) {
	const { t } = useTranslation();
	const [openArtifactId, setOpenArtifactId] = useState<string | null>(null);
	const openArtifact = artifacts.find((artifact) => artifact.id === openArtifactId) ?? null;
	const toggleArtifact = (artifact: DevelopmentArtifact): void =>
		setOpenArtifactId((current) => (current === artifact.id ? null : (artifact.id ?? null)));
	const latest = live.latest;
	const warnings = live.updates.filter((update) => update.kind === "Warning");
	const tools = live.updates.filter((update) => update.kind === "Tool");
	const commands = live.updates.filter((update) => update.kind === "Command");
	const output = live.updates.filter((update) => update.kind === "Output" || update.kind === "Activity");
	const validationArtifacts = artifacts.filter(
		(artifact) => artifact.kind === "ValidationReport" || artifact.kind === "ReviewReport",
	);
	// A prompt is neither a changed file nor evidence, so it belongs in neither curated tab. It sits in details, next
	// to the other facts about the attempt, because what the model was TOLD is that kind of fact.
	const promptArtifacts = artifacts.filter((artifact) => artifact.kind === "Prompt");
	// The newest validation report, WHATEVER its validity. Selecting on `isValid` was the defect: a failed gate
	// invalidates the approval evidence, so every failed report was dropped here and the panel fell through to "no
	// deterministic validation has run for this task yet" — the report that existed, was fetchable, and named the
	// fault. Currency is a presentation decision made in ValidationReportView from the report's own verdict; it is
	// not a reason to refuse to look at the report.
	const latestValidationReport = useMemo(() => {
		let latest: DevelopmentArtifact | null = null;
		for (const artifact of artifacts) {
			if (artifact.kind !== "ValidationReport") {
				continue;
			}
			if (latest === null || (artifact.createdAtUtc ?? 0) >= (latest.createdAtUtc ?? 0)) {
				latest = artifact;
			}
		}

		return latest;
	}, [artifacts]);

	return (
		<Stack gap="md" data-testid="development-live-panel">
			<Group justify="space-between">
				<Group gap="xs">
					<Badge variant="light">{attempt?.role ?? t("pages.development.live.noAttempt", "No active attempt")}</Badge>
					<Badge color={attempt?.status === "Running" ? "green" : "gray"}>
						{attempt?.status ?? t("pages.development.live.idle", "Idle")}
					</Badge>
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
					title={warning.warningCategory ?? t("pages.development.live.warning", "Warning")}
				>
					{warning.warningMessage}
				</Alert>
			))}

			<Tabs defaultValue="live">
				<Tabs.List>
					<Tabs.Tab value="live" leftSection={<IconCode size={14} />}>
						{t("pages.development.live.tabs.live", "Live")}
					</Tabs.Tab>
					<Tabs.Tab value="tools" leftSection={<IconTool size={14} />}>
						{t("pages.development.live.tabs.tools", "Tools")}
					</Tabs.Tab>
					<Tabs.Tab value="commands" leftSection={<IconTerminal2 size={14} />}>
						{t("pages.development.live.tabs.commands", "Commands")}
					</Tabs.Tab>
					<Tabs.Tab value="files" leftSection={<IconFile size={14} />}>
						{t("pages.development.live.tabs.files", "Files")}
					</Tabs.Tab>
					<Tabs.Tab value="validation" leftSection={<IconListCheck size={14} />}>
						{t("pages.development.live.tabs.validation", "Validation")}
					</Tabs.Tab>
					<Tabs.Tab value="details" leftSection={<IconInfoCircle size={14} />}>
						{t("pages.development.live.tabs.details", "Details")}
					</Tabs.Tab>
				</Tabs.List>

				<Tabs.Panel value="live" pt="md">
					{/*
					 * Viewport-relative rather than a flat 260px: on a short window (a phone in landscape, a split
					 * desktop pane) a fixed 260 left the live output taller than the space the panel had, so the page
					 * grew a second scrollbar around a region that already scrolls. `min()` keeps the desktop height
					 * exactly as it was.
					 */}
					<ScrollArea h="min(260px, 40dvh)">
						<Stack gap="xs">
							{output.length === 0 ? <Text c="dimmed">{t("pages.development.live.noOutput", "No live output yet.")}</Text> : null}
							{output.map((update) => (
								<Paper key={update.sequence} withBorder={true} p="xs">
									<Text size="xs" c="dimmed">
										#{update.sequence} · {update.kind}
									</Text>
									<Text>
										{update.outputDelta ??
											update.currentActivity ??
											t("pages.development.live.activityUpdated", "Activity updated")}
									</Text>
								</Paper>
							))}
						</Stack>
					</ScrollArea>
				</Tabs.Panel>
				<Tabs.Panel value="tools" pt="md">
					<Stack gap="xs">
						{tools.length === 0 ? <Text c="dimmed">{t("pages.development.live.noTools", "No tool activity yet.")}</Text> : null}
						{tools.map((update) => (
							<Code key={update.sequence}>
								{update.currentToolId ?? "tool"} · {update.currentActivity ?? update.status}
							</Code>
						))}
					</Stack>
				</Tabs.Panel>
				<Tabs.Panel value="commands" pt="md">
					<Stack gap="xs">
						{commands.length === 0 ? (
							<Text c="dimmed">{t("pages.development.live.noCommands", "No command activity yet.")}</Text>
						) : null}
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
							{t("pages.development.live.filesSummary", "{{files}} file(s) changed; patch size {{bytes}} bytes.", {
								files: latest?.changedFileCount ?? 0,
								bytes: latest?.patchByteCount ?? 0,
							})}
						</Text>
						{artifacts
							.filter((artifact) => artifact.kind === "ChangedFilesManifest" || artifact.kind === "Patch")
							.map((artifact) => (
								<Group key={artifact.id} justify="space-between" wrap="nowrap">
									<Code>
										{artifact.kind} · {artifact.contentHash?.slice(0, 12)} · {artifact.byteCount ?? 0} bytes
									</Code>
									<ArtifactViewButton artifact={artifact} open={openArtifactId === artifact.id} onToggle={toggleArtifact} />
								</Group>
							))}
						{openArtifact && (openArtifact.kind === "ChangedFilesManifest" || openArtifact.kind === "Patch") ? (
							<ArtifactContentView artifact={openArtifact} />
						) : null}
					</Stack>
				</Tabs.Panel>
				<Tabs.Panel value="validation" pt="md">
					<Stack gap="md" data-testid="development-validation-panel">
						<ValidationReportView artifact={latestValidationReport} />
						<Divider />
						<Stack gap="xs">
							<Text size="sm" fw={600}>
								{t("pages.development.validation.evidence", "Stored evidence")}
							</Text>
							{validationArtifacts.length === 0 ? (
								<Text c="dimmed">{t("pages.development.validation.noEvidence", "No validation or review evidence yet.")}</Text>
							) : null}
							{validationArtifacts.map((artifact) => (
								<Group key={artifact.id} justify="space-between">
									<Text>{artifact.kind}</Text>
									<Group gap="xs">
										<Badge color={artifact.isValid ? "green" : "red"}>
											{artifact.isValid
												? t("pages.development.validation.current", "Current")
												: t("pages.development.validation.invalidated", "Invalidated")}
										</Badge>
										<ArtifactViewButton artifact={artifact} open={openArtifactId === artifact.id} onToggle={toggleArtifact} />
									</Group>
								</Group>
							))}
							{openArtifact && validationArtifacts.includes(openArtifact) ? (
								<ArtifactContentView artifact={openArtifact} />
							) : null}
						</Stack>
					</Stack>
				</Tabs.Panel>
				<Tabs.Panel value="details" pt="md">
					<Stack gap="xs">
						<Text size="sm">
							{t("pages.development.live.details.attempt", "Attempt")}: {attempt?.id ?? "—"}
						</Text>
						<Text size="sm">
							{t("pages.development.live.details.predecessor", "Predecessor")}: {attempt?.predecessorAttemptId ?? "—"}
						</Text>
						<Text size="sm">
							{t("pages.development.live.details.activity", "Current activity")}: {latest?.currentActivity ?? "—"}
						</Text>
						<Text size="sm">
							{t("pages.development.live.details.subject", "Subject")}: {latest?.subjectHash?.slice(0, 16) ?? "—"}
						</Text>
						<Text size="sm">
							{t("pages.development.live.details.events", "Durable events")}: {events.length}
						</Text>
						<Divider />
						<Stack gap="xs" data-testid="development-prompt-artifacts">
							<Text size="sm" fw={600}>
								{t("pages.development.live.details.prompts", "Prompts")}
							</Text>
							{promptArtifacts.length === 0 ? (
								<Text c="dimmed">{t("pages.development.live.details.noPrompts", "No prompt was recorded for this task yet.")}</Text>
							) : null}
							{promptArtifacts.map((artifact) => (
								<Group key={artifact.id} justify="space-between">
									<Text>{artifact.kind}</Text>
									<ArtifactViewButton artifact={artifact} open={openArtifactId === artifact.id} onToggle={toggleArtifact} />
								</Group>
							))}
							{openArtifact && promptArtifacts.includes(openArtifact) ? <ArtifactContentView artifact={openArtifact} /> : null}
						</Stack>
					</Stack>
				</Tabs.Panel>
			</Tabs>
		</Stack>
	);
}
