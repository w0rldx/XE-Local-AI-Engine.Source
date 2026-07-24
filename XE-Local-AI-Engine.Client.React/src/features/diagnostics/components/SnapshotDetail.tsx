// Snapshot detail view.
//
// Shows a single snapshot's error, breadcrumb timeline, network log, environment and redacted state.
// The raw-JSON inspector and the rrweb DOM-replay viewer are gated behind Developer Mode:
// the redacted state JSON is shown to everyone, but the full raw dump + DOM replay are dev-only.

import { Badge, Button, Card, Code, Group, ScrollArea, Stack, Text, Title } from "@mantine/core";
import { IconArrowLeft } from "@tabler/icons-react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import type { Snapshot } from "@/core/diagnostics/Diagnostics";
import { BreadcrumbTimeline } from "@/features/diagnostics/components/BreadcrumbTimeline";
import { NetworkLog } from "@/features/diagnostics/components/NetworkLog";
import { RrwebReplay } from "@/features/diagnostics/components/RrwebReplay";

export interface SnapshotDetailProps {
	readonly snapshot: Snapshot;
	readonly onBack: () => void;
}

function Section({ title, children }: { title: string; children: ReactNode }) {
	return (
		<Card withBorder={true} radius="md" padding="md">
			<Stack gap="sm">
				<Title order={5}>{title}</Title>
				{children}
			</Stack>
		</Card>
	);
}

export function SnapshotDetail({ snapshot, onBack }: SnapshotDetailProps) {
	const { t } = useTranslation();
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const hasState = snapshot.state !== undefined && Object.keys(snapshot.state).length > 0;
	const hasRrweb = snapshot.rrweb !== undefined && snapshot.rrweb.length > 0;

	return (
		<Stack gap="md">
			<Group justify="space-between">
				<Group gap="sm">
					<Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={onBack}>
						{t("diagnostics.actions.back")}
					</Button>
					<Title order={4}>{t("diagnostics.detail.title")}</Title>
					<Badge color={snapshot.kind === "error" ? "red" : "blue"} variant="light">
						{t(`diagnostics.kind.${snapshot.kind}`)}
					</Badge>
				</Group>
				<Text c="dimmed" size="sm">
					{t("diagnostics.detail.capturedAt", { time: new Date(snapshot.createdAt).toLocaleString() })}
				</Text>
			</Group>

			<Section title={t("diagnostics.detail.error")}>
				{snapshot.error ? (
					<Stack gap="xs">
						<Text size="sm">
							<strong>{t("diagnostics.detail.message")}:</strong> {snapshot.error.message}
						</Text>
						<Text size="sm">
							<strong>{t("diagnostics.detail.source")}:</strong> {snapshot.error.source}
						</Text>
						{snapshot.error.stack && (
							<Code block={true} fz="xs">
								{snapshot.error.stack}
							</Code>
						)}
						{snapshot.error.componentStack && (
							<>
								<Text size="sm" fw={600}>
									{t("diagnostics.detail.componentStack")}
								</Text>
								<Code block={true} fz="xs">
									{snapshot.error.componentStack}
								</Code>
							</>
						)}
					</Stack>
				) : (
					<Text c="dimmed" size="sm">
						{t("diagnostics.detail.noError")}
					</Text>
				)}
			</Section>

			<Section title={t("diagnostics.detail.breadcrumbs")}>
				<BreadcrumbTimeline breadcrumbs={snapshot.breadcrumbs} />
			</Section>

			<Section title={t("diagnostics.detail.network")}>
				<NetworkLog entries={snapshot.network} />
			</Section>

			<Section title={t("diagnostics.detail.environment")}>
				<Stack gap={4}>
					<Text size="sm">
						<strong>{t("diagnostics.detail.route")}:</strong> {snapshot.env.route}
					</Text>
					<Text size="sm">
						<strong>{t("diagnostics.detail.appVersion")}:</strong> {snapshot.env.appVersion}
					</Text>
					<Text size="sm">
						<strong>{t("diagnostics.detail.locale")}:</strong> {snapshot.env.locale}
					</Text>
					<Text size="sm">
						<strong>{t("diagnostics.detail.viewport")}:</strong> {snapshot.env.viewport.width}×{snapshot.env.viewport.height}
					</Text>
					<Text size="sm" style={{ wordBreak: "break-word" }}>
						<strong>{t("diagnostics.detail.userAgent")}:</strong> {snapshot.env.userAgent}
					</Text>
				</Stack>
			</Section>

			<Section title={t("diagnostics.detail.state")}>
				{hasState ? (
					<ScrollArea.Autosize mah={320}>
						<Code block={true} fz="xs">
							{JSON.stringify(snapshot.state, null, 2)}
						</Code>
					</ScrollArea.Autosize>
				) : (
					<Text c="dimmed" size="sm">
						{t("diagnostics.detail.noState")}
					</Text>
				)}
			</Section>

			{developerMode && hasRrweb && snapshot.rrweb && (
				<Section title={t("diagnostics.replay.title")}>
					<RrwebReplay events={snapshot.rrweb} />
				</Section>
			)}

			{developerMode && (
				<Section title={t("diagnostics.detail.rawJson")}>
					<ScrollArea.Autosize mah={360}>
						<Code block={true} fz="xs">
							{JSON.stringify(snapshot, null, 2)}
						</Code>
					</ScrollArea.Autosize>
				</Section>
			)}
		</Stack>
	);
}
