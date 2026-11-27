import { ActionIcon, Alert, Badge, Button, Group, Loader, Stack, Table, Text, Tooltip } from "@mantine/core";
import { IconTrash } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	deleteModelLabPlaygroundRunMutation,
	getModelLabPlaygroundRunOptions,
	listModelLabPlaygroundRunsOptions,
	listModelLabPlaygroundRunsQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import type { XeLocalAiEngineClientEndpointsModelLabV1ModelLabPlaygroundRunResponse as PlaygroundRun } from "@/core/api/generated/types.gen";
import { formatNumber } from "@/core/formatting/NumberFormatting";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { BasisBadge } from "@/features/modelLab/components/ArchitectureExplanation";
import { PlaygroundRunForm } from "@/features/modelLab/components/PlaygroundRunForm";
import { PlaygroundStepper } from "@/features/modelLab/components/PlaygroundStepper";
import { sameExperimentId } from "@/features/modelLab/models/ModelLabNavigation";
import { playgroundTraceSchema, playgroundValue } from "@/features/modelLab/models/ModelLabPlayground";

interface PlaygroundPanelProps {
	// The tab has been shown: the list is read only from then on.
	shown: boolean;
	// The saved run the URL names: opened once the list holds it.
	runId?: string;
	onRunChange: (runId: string | undefined) => void;
	// The user's own delete of this run finished.
	onRunDeleted: (runId: string) => void;
}

// Train a tiny network, list the saved runs, and step through one.
export function PlaygroundPanel({ shown, runId, onRunChange, onRunDeleted }: PlaygroundPanelProps) {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const list = useQuery({ ...listModelLabPlaygroundRunsOptions(), enabled: shown });
	const remove = useMutation({
		...deleteModelLabPlaygroundRunMutation(),
		onSuccess: (_data, variables) => {
			onRunDeleted(variables.path.runId);
			return queryClient.invalidateQueries({ queryKey: listModelLabPlaygroundRunsQueryKey() });
		},
	});
	if (!shown || list.status === "pending") {
		return null;
	}
	const listFailed = list.isError && (
		<Alert color="red" data-testid="model-lab-playground-list-failed">
			{apiErrorMessage(list.error, t("pages.modelLab.playground.listFailed", "The saved runs could not be loaded."))}
		</Alert>
	);
	// A failed refetch keeps the cached list: the panel stays mounted so knob edits and playback survive.
	if (list.data === undefined) {
		return listFailed;
	}
	const runs = list.data.runs;
	// Only an id the held list positively has is opened: an unknown one asks for nothing.
	const open = runs.find((run) => sameExperimentId(run.id, runId));

	return (
		<Stack gap="md">
			{listFailed}
			{/* Only training needs the platform: saved runs are still read, replayed and deleted. */}
			{list.data.platformSupported ? (
				<PlaygroundRunForm onStarted={onRunChange} />
			) : (
				<Alert color="blue" data-testid="model-lab-playground-unsupported">
					{list.data.unsupportedMessage ??
						t("pages.modelLab.playground.unsupported", "The training stepper is not available on this node's platform.")}
				</Alert>
			)}
			{runs.length > 0 && (
				<Stack gap="xs">
					<Text size="sm" c="dimmed" data-testid="model-lab-playground-retention">
						{t("pages.modelLab.playground.retention", "The newest {{count}} runs are kept.", {
							count: list.data.maxRetainedRuns,
						})}
					</Text>
					<Table highlightOnHover={true} data-testid="model-lab-playground-runs">
						<Table.Tbody>
							{runs.map((run) => (
								<RunRow
									key={run.id}
									run={run}
									open={run.id === open?.id}
									onOpen={() => onRunChange(run.id)}
									onDelete={() => remove.mutate({ path: { runId: run.id } })}
								/>
							))}
						</Table.Tbody>
					</Table>
				</Stack>
			)}
			{remove.isError && (
				<Alert color="yellow" data-testid="model-lab-playground-delete-refused">
					{apiErrorMessage(remove.error, t("pages.modelLab.playground.deleteFailed", "The run could not be deleted."))}
				</Alert>
			)}
			{open && <PlaygroundRunView key={open.id} run={open} />}
		</Stack>
	);
}

interface RunRowProps {
	run: PlaygroundRun;
	open: boolean;
	onOpen: () => void;
	// Runs once the operator has confirmed.
	onDelete: () => void;
}

function RunRow({ run, open, onOpen, onDelete }: RunRowProps) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const confirmDelete = async (): Promise<void> => {
		const confirmed = await confirm({
			title: t("pages.modelLab.playground.deleteConfirm.title", "Delete this run?"),
			description: t(
				"pages.modelLab.playground.deleteConfirm.description",
				"The saved run and its recorded steps are deleted from this node. This cannot be undone.",
			),
			confirmationText: t("pages.modelLab.playground.delete", "Delete"),
			cancellationText: t("common.cancel", "Cancel"),
		});
		if (confirmed) {
			onDelete();
		}
	};
	const { config } = run;
	const createdAt = formatTimestamp(run.createdAtUtc);
	// Every saved knob is in the name: two runs that differ only in learning rate or seed stay apart.
	const name = t(
		"pages.modelLab.playground.runName",
		"{{dataset}}, depth {{depth}}, {{hidden}} hidden units, {{steps}} steps, learning rate {{learningRate}}, seed {{seed}}",
		{
			dataset:
				config.dataset === "xor"
					? t("pages.modelLab.playground.dataset.xor", "XOR (4 points)")
					: t("pages.modelLab.playground.dataset.spirals", "Two spirals (192 points)"),
			depth: config.depth,
			hidden: config.hidden,
			steps: config.steps,
			learningRate: formatNumber(config.learningRate, { maximumSignificantDigits: 4 }),
			seed: config.seed,
		},
	);
	return (
		<Table.Tr data-testid="model-lab-playground-run">
			<Table.Td>
				<Stack gap={2} align="flex-start">
					<Button variant="subtle" size="compact-sm" aria-current={open ? "true" : undefined} onClick={onOpen}>
						{name}
					</Button>
					<Group gap={4}>
						<Badge color={run.outcome.kind === "diverged" ? "red" : "green"}>
							{run.outcome.kind === "diverged"
								? t("pages.modelLab.playground.diverged", "Diverged")
								: t("pages.modelLab.playground.finished", "Finished")}
						</Badge>
						{config.residual && (
							<Badge variant="light" color="gray">
								{t("pages.modelLab.playground.knob.residual.label", "Residual connections")}
							</Badge>
						)}
						{config.sharedBlock && (
							<Badge variant="light" color="gray">
								{t("pages.modelLab.playground.sharedBadge", "Shared block")}
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
				<Tooltip label={t("pages.modelLab.playground.delete", "Delete")}>
					<ActionIcon
						variant="subtle"
						color="gray"
						aria-label={t("pages.modelLab.playground.deleteRun", "Delete {{run}} from {{date}}", { run: name, date: createdAt })}
						onClick={confirmDelete}
					>
						<IconTrash size={16} />
					</ActionIcon>
				</Tooltip>
			</Table.Td>
		</Table.Tr>
	);
}

// One saved run: its summary and its recorded steps.
function PlaygroundRunView({ run }: { run: PlaygroundRun }) {
	const { t } = useTranslation();
	const detail = useQuery({
		...getModelLabPlaygroundRunOptions({ path: { runId: run.id } }),
		// A saved run never changes.
		staleTime: Number.POSITIVE_INFINITY,
	});
	const trace = useMemo(() => detail.data && playgroundTraceSchema.safeParse(detail.data.trace), [detail.data]);
	return (
		<Stack gap="sm" data-testid="model-lab-playground-run-view">
			<Group gap="xs">
				<Text size="sm" data-testid="model-lab-playground-final">
					{t("pages.modelLab.playground.final", "After the last update: loss {{loss}}, accuracy {{accuracy}}.", {
						loss: playgroundValue(run.finalLoss, t),
						accuracy: playgroundValue(run.finalAccuracy, t),
					})}
				</Text>
				<BasisBadge basis="measuredTrace" />
			</Group>
			{detail.status === "pending" && <Loader size="sm" />}
			{detail.status === "error" && (
				<Alert color="red" data-testid="model-lab-playground-run-failed">
					{apiErrorMessage(detail.error, t("pages.modelLab.playground.runFailed", "The saved run could not be read."))}
				</Alert>
			)}
			{trace &&
				(trace.success ? (
					<PlaygroundStepper trace={trace.data} />
				) : (
					<Alert color="red" data-testid="model-lab-playground-trace-unreadable">
						{t("pages.modelLab.playground.traceUnreadable", "The saved steps of this run are not in a shape this page can read.")}
					</Alert>
				))}
		</Stack>
	);
}
