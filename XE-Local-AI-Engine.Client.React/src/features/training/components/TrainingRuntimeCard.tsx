import { Alert, Badge, Button, Code, Group, List, Loader, ScrollArea, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconX } from "@tabler/icons-react";
import { useEffect, useMemo } from "react";
import { useTranslation } from "react-i18next";

import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { useTrainingRuntimeHub } from "@/features/training/hooks/useTrainingRuntimeHub";
import { isRuntimeInstalling, mergeTrainingLogs, trainingLogEntries } from "@/features/training/models/TrainingModels";
import {
	useRemoveTrainingRuntime,
	useStartTrainingRuntimeInstall,
	useTrainingRuntimePrerequisites,
	useTrainingRuntimeStatus,
} from "@/features/training/queries/useTrainingQueries";

/**
 * The Python training runtime card: the per-item prerequisite checklist, install/remove, and the streamed install log.
 *
 * The install is a multi-gigabyte, multi-minute download with no fallback, so the log is shown rather than a spinner —
 * a silent wait of that length is indistinguishable from a hang.
 */
export function TrainingRuntimeCard() {
	const { t } = useTranslation();

	const statusQuery = useTrainingRuntimeStatus();
	const status = statusQuery.data;
	const installing = status != null && (status.isRunning || isRuntimeInstalling(status.phase));

	const pollingStatusQuery = useTrainingRuntimeStatus(installing);
	const prerequisitesQuery = useTrainingRuntimePrerequisites();
	const installMutation = useStartTrainingRuntimeInstall();
	const removeMutation = useRemoveTrainingRuntime();
	const hub = useTrainingRuntimeHub();

	const current = pollingStatusQuery.data ?? status;
	const { reset } = hub;

	// A finished install leaves the streamed log on screen; clearing it when the runtime goes back to Idle keeps a
	// removed runtime from showing the log of the install that preceded it.
	useEffect(() => {
		if (current?.phase === "Idle") {
			reset();
		}
	}, [current?.phase, reset]);

	// The server's retained ring plus whatever the hub has pushed since; merging by sequence makes the overlap
	// between the two idempotent.
	const logEntries = useMemo(
		() => mergeTrainingLogs(trainingLogEntries(current?.logStartSequence ?? 0, current?.logLines ?? []), hub.logEntries),
		[current?.logStartSequence, current?.logLines, hub.logEntries],
	);

	const phase = hub.phase ?? current?.phase ?? "Idle";
	const error = hub.error ?? current?.sanitizedError ?? null;
	const installed = current?.installed ?? null;
	const prerequisites = prerequisitesQuery.data;
	const busy = installing || installMutation.isPending || removeMutation.isPending;

	return (
		<SectionCard title={t("pages.training.runtime.title", "Python training runtime")}>
			<Stack gap="md">
				<Text c="dimmed" size="sm">
					{t(
						"pages.training.runtime.description",
						"Fine-tuning runs in a pinned, uv-managed Python environment. It is installed once and used by every training run.",
					)}
				</Text>
				<Group gap="sm">
					<Badge color={installed == null ? "gray" : "green"} variant="light">
						{t(`pages.training.runtime.phase.${phase}`, phase)}
					</Badge>
					{busy ? <Loader size="xs" /> : null}
				</Group>

				{installed == null ? null : (
					<Stack gap={4}>
						<Text size="sm">
							{t("pages.training.runtime.installedPython", "Python {{version}}", { version: installed.pythonVersion })}
						</Text>
						{installed.torchVersion == null ? null : (
							<Text size="sm" c="dimmed">
								{t("pages.training.runtime.installedTorch", "torch {{version}}", { version: installed.torchVersion })}
							</Text>
						)}
						{installed.deviceName == null ? null : (
							<Text size="sm" c="dimmed">
								{installed.deviceName}
							</Text>
						)}
					</Stack>
				)}

				{error == null ? null : (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} title={t("pages.training.runtime.failed", "Install failed")}>
						{error}
					</Alert>
				)}

				{prerequisites == null ? null : (
					<Stack gap={4}>
						<Text fw={500} size="sm">
							{t("pages.training.runtime.prerequisites", "Prerequisites")}
						</Text>
						<List spacing={4} size="sm">
							{prerequisites.items.map((item) => (
								<List.Item
									key={item.key}
									icon={item.satisfied ? <IconCheck color="var(--mantine-color-green-6)" size={16} /> : <IconX color="var(--mantine-color-red-6)" size={16} />}
								>
									{item.detail}
								</List.Item>
							))}
						</List>
					</Stack>
				)}

				{logEntries.length === 0 ? null : (
					<ScrollArea.Autosize mah={260} type="auto">
						<Code block={true}>{logEntries.map((entry) => entry.message).join("\n")}</Code>
					</ScrollArea.Autosize>
				)}

				<Group gap="sm">
					<Button
						disabled={busy || prerequisites?.canInstall !== true}
						loading={installMutation.isPending}
						onClick={() => installMutation.mutate({})}
					>
						{installed == null
							? t("pages.training.runtime.install", "Install runtime")
							: t("pages.training.runtime.reinstall", "Reinstall runtime")}
					</Button>
					<Button
						color="red"
						disabled={removeMutation.isPending || (installed == null && !installing)}
						loading={removeMutation.isPending}
						onClick={() => removeMutation.mutate({})}
						variant="light"
					>
						{installing ? t("pages.training.runtime.cancel", "Cancel install") : t("pages.training.runtime.remove", "Remove runtime")}
					</Button>
				</Group>
			</Stack>
		</SectionCard>
	);
}
