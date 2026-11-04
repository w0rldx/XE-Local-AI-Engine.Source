import { ActionIcon, Alert, Badge, Button, Group, Paper, Progress, Stack, Table, Text, Tooltip } from "@mantine/core";
import { IconTrash } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	cancelModelLabExperimentMutation,
	deleteModelLabExperimentMutation,
	getModelLabExperimentQueryKey,
	listModelLabExperimentsOptions,
	listModelLabExperimentsQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { humanizeBytes } from "@/core/formatting/BytesFormatting";
import { TraceCheckpointForm } from "@/features/modelLab/components/TraceCheckpointForm";
import { TraceExperimentView } from "@/features/modelLab/components/TraceExperimentView";
import { useModelLabExperimentHub } from "@/features/modelLab/hooks/useModelLabExperimentHub";
import { activeJobPollMs } from "@/features/modelLab/models/ModelLabTrace";

const statusColors: Record<string, string> = { Running: "blue", Succeeded: "green", Failed: "red", Cancelled: "gray" };

// Start a trace, follow the one running job, and reopen any saved one.
export function TraceExperiments() {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const [selectedId, setSelectedId] = useState<string | null>(null);
	const refreshList = useCallback(
		() => queryClient.invalidateQueries({ queryKey: listModelLabExperimentsQueryKey() }),
		[queryClient],
	);
	const refresh = useCallback(
		() =>
			Promise.all([
				refreshList(),
				selectedId &&
					queryClient.invalidateQueries({ queryKey: getModelLabExperimentQueryKey({ path: { experimentId: selectedId } }) }),
			]),
		[queryClient, refreshList, selectedId],
	);
	// Two observers of one query: the hub needs the running id, and the poll rate needs the hub's answer.
	const activeId = useQuery(listModelLabExperimentsOptions()).data?.activeExperimentId ?? null;
	const live = useModelLabExperimentHub(activeId, refresh);
	const list = useQuery({
		...listModelLabExperimentsOptions(),
		refetchInterval: (query) => (query.state.data?.activeExperimentId ? activeJobPollMs(live.isLive) : false),
	});
	const cancel = useMutation({ ...cancelModelLabExperimentMutation(), onSettled: refresh });
	const remove = useMutation({
		...deleteModelLabExperimentMutation(),
		onSuccess: (_data, variables) => {
			if (variables.path.experimentId === selectedId) {
				setSelectedId(null);
			}
			return refreshList();
		},
	});

	if (list.status === "pending") {
		return null;
	}
	if (list.status === "error") {
		return (
			<Alert color="red">
				{apiErrorMessage(list.error, t("pages.modelLab.trace.listFailed", "The experiments could not be loaded."))}
			</Alert>
		);
	}
	if (list.data.platformSupported === false) {
		return (
			<Alert color="blue" data-testid="model-lab-unsupported">
				{t("pages.modelLab.trace.unsupported", list.data.unsupportedMessage ?? "")}
			</Alert>
		);
	}

	const experiments = list.data.experiments ?? [];
	return (
		<Stack gap="md">
			<TraceCheckpointForm busy={activeId !== null} />
			{activeId && (
				<Paper withBorder={true} p="md" data-testid="model-lab-running">
					<Stack gap="xs">
						<Group justify="space-between">
							<Text size="sm" fw={600}>
								{t(`pages.modelLab.trace.phase.${live.phase ?? "starting"}`, live.phase ?? "starting")}
							</Text>
							<Button
								color="red"
								variant="light"
								size="xs"
								loading={cancel.isPending}
								onClick={() => cancel.mutate({ path: { experimentId: activeId } })}
								data-testid="model-lab-cancel"
							>
								{t("pages.modelLab.trace.cancel", "Cancel experiment")}
							</Button>
						</Group>
						{live.totalSteps ? (
							<Progress value={(100 * (live.step ?? 0)) / live.totalSteps} />
						) : (
							<Progress value={100} animated={true} />
						)}
						<Text size="xs" c="dimmed">
							{live.totalSteps &&
								(live.phase === "tracing"
									? t("pages.modelLab.trace.progress", "Token {{step}} of up to {{total}}, {{vram}} on the GPU.", {
											step: live.step ?? 0,
											total: live.totalSteps,
											vram: humanizeBytes(live.vramBytes ?? 0),
										})
									: t("pages.modelLab.steer.progress", "Prompt {{step}} of {{total}}.", {
											step: live.step ?? 0,
											total: live.totalSteps,
										}))}
						</Text>
						<Text size="sm" c="dimmed" data-testid="model-lab-chat-wait">
							{t(
								"pages.modelLab.trace.chatWait",
								"Chats that need a local model wait until this finishes, or until you cancel it.",
							)}
						</Text>
					</Stack>
				</Paper>
			)}
			{experiments.length > 0 && (
				<Stack gap="xs">
					<Text size="sm" c="dimmed">
						{t("pages.modelLab.trace.retention", "The newest {{count}} experiments are kept.", {
							count: list.data.maxRetainedExperiments,
						})}
					</Text>
					<Table highlightOnHover={true} data-testid="model-lab-experiments">
						<Table.Tbody>
							{experiments.map((experiment) => (
								<Table.Tr key={experiment.id}>
									<Table.Td>
										<Stack gap={2} align="flex-start">
											<Button variant="subtle" size="compact-sm" onClick={() => setSelectedId(experiment.id ?? null)}>
												{experiment.repoId}
											</Button>
											<Group gap={4}>
												<Badge color={statusColors[experiment.status ?? ""] ?? "gray"}>
													{t(`pages.modelLab.trace.status.${experiment.status}`, experiment.status ?? "")}
												</Badge>
												{experiment.kind === "Steering" && (
													<Badge variant="light" color="gray" data-testid="model-lab-steering-badge">
														{t("pages.modelLab.steer.jobSteering", "Steering")}
													</Badge>
												)}
											</Group>
										</Stack>
									</Table.Td>
									<Table.Td>
										<Text size="xs" c="dimmed">
											{experiment.createdAtUtc ? new Date(experiment.createdAtUtc).toLocaleString() : ""}
										</Text>
									</Table.Td>
									<Table.Td ta="right">
										<Tooltip label={t("pages.modelLab.trace.delete", "Delete")}>
											<ActionIcon
												variant="subtle"
												color="gray"
												disabled={experiment.id === activeId}
												aria-label={t("pages.modelLab.trace.delete", "Delete")}
												onClick={() => remove.mutate({ path: { experimentId: experiment.id ?? "" } })}
											>
												<IconTrash size={16} />
											</ActionIcon>
										</Tooltip>
									</Table.Td>
								</Table.Tr>
							))}
						</Table.Tbody>
					</Table>
				</Stack>
			)}
			{remove.isError && (
				<Alert color="yellow" data-testid="model-lab-delete-refused">
					{apiErrorMessage(remove.error, t("pages.modelLab.trace.deleteFailed", "The experiment could not be deleted."))}
				</Alert>
			)}
			{selectedId && <TraceExperimentView key={selectedId} experimentId={selectedId} isLive={live.isLive} />}
		</Stack>
	);
}
