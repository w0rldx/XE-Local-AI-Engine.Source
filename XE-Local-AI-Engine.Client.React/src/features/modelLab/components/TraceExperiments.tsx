import { ActionIcon, Alert, Badge, Button, Checkbox, Group, Paper, Progress, Stack, Table, Text, Tooltip } from "@mantine/core";
import { IconPin, IconPinFilled, IconTrash } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useId, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	cancelModelLabExperimentMutation,
	deleteModelLabExperimentMutation,
	getModelLabExperimentQueryKey,
	listModelLabExperimentsOptions,
	listModelLabExperimentsQueryKey,
	setModelLabExperimentPinMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { humanizeBytes } from "@/core/formatting/BytesFormatting";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { SavedExperimentComparison } from "@/features/modelLab/components/SavedExperimentComparison";
import { TraceCheckpointForm } from "@/features/modelLab/components/TraceCheckpointForm";
import { TraceExperimentView } from "@/features/modelLab/components/TraceExperimentView";
import { useModelLabExperimentHub } from "@/features/modelLab/hooks/useModelLabExperimentHub";
import { compareBlock } from "@/features/modelLab/models/ModelLabComparison";
import type { ModelLabIntent, ModelLabJump } from "@/features/modelLab/models/ModelLabNavigation";
import { activeJobPollMs } from "@/features/modelLab/models/ModelLabTrace";

const statusColors: Record<string, string> = { Running: "blue", Succeeded: "green", Failed: "red", Cancelled: "gray" };

interface TraceExperimentsProps {
	// Another tab asked to open this saved experiment.
	intent?: ModelLabIntent<"trace">;
	onJump?: (to: ModelLabJump) => void;
}

// Start a trace, follow the one running job, and reopen any saved one.
export function TraceExperiments({ intent, onJump }: TraceExperimentsProps) {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const [selectedId, setSelectedId] = useState<string | null>(null);
	// Ticked rows, and the runs the open comparison shows.
	const [checked, setChecked] = useState<string[]>([]);
	const [comparedIds, setComparedIds] = useState<string[] | null>(null);
	const [appliedNonce, setAppliedNonce] = useState<number>();
	if (intent && intent.nonce !== appliedNonce) {
		setAppliedNonce(intent.nonce);
		setSelectedId(intent.experimentId);
		setComparedIds(null);
	}
	const compareReasonId = useId();
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
			const removed = variables.path.experimentId;
			if (removed === selectedId) {
				setSelectedId(null);
			}
			setChecked((ids) => ids.filter((id) => id !== removed));
			setComparedIds((ids) => (ids?.includes(removed) ? null : ids));
			return refreshList();
		},
	});
	const pin = useMutation({ ...setModelLabExperimentPinMutation(), onSuccess: refreshList });

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
	const selected = experiments.filter((experiment) => checked.includes(experiment.id));
	const block = compareBlock(selected);
	const blockReasons = {
		count: t("pages.modelLab.trace.compareCount", "Select 2 to 4 runs to compare."),
		kind: t("pages.modelLab.trace.compareKind", "Compare traces only with traces, and steering runs only with steering runs."),
		unfinished: t("pages.modelLab.trace.compareUnfinished", "Only finished runs can be compared."),
	};
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
					<Text size="sm" c="dimmed" data-testid="model-lab-retention">
						{t(
							"pages.modelLab.trace.retention",
							"The newest {{count}} unpinned experiments are kept. Pinned ones stay until you delete them: {{pinned}} of {{max}} pinned.",
							{
								count: list.data.maxRetainedExperiments,
								pinned: experiments.filter((experiment) => experiment.isPinned).length,
								max: list.data.maxPinnedExperiments,
							},
						)}
					</Text>
					<Group gap="xs">
						<Button
							size="xs"
							variant="light"
							disabled={block !== undefined}
							aria-describedby={block ? compareReasonId : undefined}
							onClick={() => {
								setComparedIds(selected.map((experiment) => experiment.id));
								setSelectedId(null);
							}}
							data-testid="model-lab-compare-button"
						>
							{t("pages.modelLab.trace.compare", "Compare selected")}
						</Button>
						{block && (
							<Text size="xs" c="dimmed" id={compareReasonId} data-testid="model-lab-compare-reason">
								{blockReasons[block]}
							</Text>
						)}
					</Group>
					{/* The checkbox, name, date and two actions overflow a phone: the table scrolls inside its box instead. */}
					<Table.ScrollContainer minWidth={420} type="native">
						<Table highlightOnHover={true} data-testid="model-lab-experiments">
							<Table.Tbody>
								{experiments.map((experiment) => {
									const createdAt = formatTimestamp(experiment.createdAtUtc);
									return (
										<Table.Tr key={experiment.id}>
											<Table.Td w={1}>
												<Checkbox
													aria-label={t("pages.modelLab.trace.select", "Select {{repo}} from {{date}} for comparison", {
														repo: experiment.repoId,
														date: createdAt,
													})}
													checked={checked.includes(experiment.id)}
													onChange={(event) => {
														const on = event.currentTarget.checked;
														setChecked((ids) => (on ? [...ids, experiment.id] : ids.filter((id) => id !== experiment.id)));
													}}
													data-testid="model-lab-select"
												/>
											</Table.Td>
											<Table.Td>
												<Stack gap={2} align="flex-start">
													<Button
														variant="subtle"
														size="compact-sm"
														onClick={() => {
															setSelectedId(experiment.id);
															setComparedIds(null);
														}}
													>
														{experiment.repoId}
													</Button>
													<Group gap={4}>
														<Badge color={statusColors[experiment.status] ?? "gray"}>
															{t(`pages.modelLab.trace.status.${experiment.status}`, experiment.status)}
														</Badge>
														{experiment.kind === "Steering" && (
															<Badge variant="light" color="gray" data-testid="model-lab-steering-badge">
																{t("pages.modelLab.steer.jobSteering", "Steering")}
															</Badge>
														)}
														{experiment.isPinned && (
															<Badge
																variant="outline"
																color="gray"
																leftSection={<IconPinFilled size={12} aria-hidden={true} />}
																data-testid="model-lab-pinned-badge"
															>
																{t("pages.modelLab.trace.pinned", "Pinned")}
															</Badge>
														)}
													</Group>
												</Stack>
											</Table.Td>
											<Table.Td>
												<Text size="xs" c="dimmed">
													{createdAt}
												</Text>
											</Table.Td>
											<Table.Td ta="right">
												<Group gap={4} justify="flex-end" wrap="nowrap">
													<Tooltip label={t("pages.modelLab.trace.pinHint", "Pinned runs are kept by cleanup.")}>
														<ActionIcon
															variant={experiment.isPinned ? "light" : "subtle"}
															color="gray"
															disabled={experiment.id === activeId || pin.isPending}
															aria-label={t("pages.modelLab.trace.pin", "Pin")}
															aria-pressed={experiment.isPinned}
															onClick={() =>
																pin.mutate({
																	path: { experimentId: experiment.id },
																	body: { pinned: !experiment.isPinned },
																})
															}
															data-testid="model-lab-pin"
														>
															{experiment.isPinned ? <IconPinFilled size={16} /> : <IconPin size={16} />}
														</ActionIcon>
													</Tooltip>
													<Tooltip label={t("pages.modelLab.trace.delete", "Delete")}>
														<ActionIcon
															variant="subtle"
															color="gray"
															disabled={experiment.id === activeId}
															aria-label={t("pages.modelLab.trace.delete", "Delete")}
															onClick={() => remove.mutate({ path: { experimentId: experiment.id } })}
														>
															<IconTrash size={16} />
														</ActionIcon>
													</Tooltip>
												</Group>
											</Table.Td>
										</Table.Tr>
									);
								})}
							</Table.Tbody>
						</Table>
					</Table.ScrollContainer>
				</Stack>
			)}
			{pin.isError && (
				<Alert color="yellow" data-testid="model-lab-pin-refused">
					{apiErrorMessage(pin.error, t("pages.modelLab.trace.pinFailed", "The pin could not be changed."))}
				</Alert>
			)}
			{remove.isError && (
				<Alert color="yellow" data-testid="model-lab-delete-refused">
					{apiErrorMessage(remove.error, t("pages.modelLab.trace.deleteFailed", "The experiment could not be deleted."))}
				</Alert>
			)}
			{comparedIds && (
				<Stack gap="xs">
					<Group justify="flex-end">
						<Button size="xs" variant="subtle" onClick={() => setComparedIds(null)} data-testid="model-lab-compare-close">
							{t("pages.modelLab.trace.compareClose", "Close comparison")}
						</Button>
					</Group>
					<SavedExperimentComparison key={comparedIds.join()} experimentIds={comparedIds} />
				</Stack>
			)}
			{selectedId && <TraceExperimentView key={selectedId} experimentId={selectedId} isLive={live.isLive} onJump={onJump} />}
		</Stack>
	);
}
