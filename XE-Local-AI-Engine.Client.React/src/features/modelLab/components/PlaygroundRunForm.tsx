import { Alert, Button, Group, NumberInput, Paper, Select, SimpleGrid, Stack, Switch, Text } from "@mantine/core";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	listModelLabPlaygroundRunsQueryKey,
	startModelLabPlaygroundRunMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { zStartModelLabPlaygroundRunBody } from "@/core/api/generated/zod.gen";
import { FieldWithInfo } from "@/core/ui/components/FieldInfo/FieldInfo";

const knobs = zStartModelLabPlaygroundRunBody.shape;
type NumberKnob = "learningRate" | "depth" | "hidden" | "steps" | "seed";
const numberKnobs: readonly NumberKnob[] = ["learningRate", "depth", "hidden", "steps", "seed"];

// The server has no defaults: every knob is sent. These fit XOR in 500 steps.
const defaultNumbers: Record<NumberKnob, number | string> = { learningRate: 0.5, depth: 2, hidden: 8, steps: 500, seed: 0 };

// Pick the knobs and train one tiny network on the CPU; the run is saved and opened once it finishes.
export function PlaygroundRunForm({ onStarted }: { onStarted: (runId: string) => void }) {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const [dataset, setDataset] = useState<string | null>("xor");
	const [numbers, setNumbers] = useState(defaultNumbers);
	const [residual, setResidual] = useState(false);
	const [sharedBlock, setSharedBlock] = useState(false);
	// The knobs the last submit refused; any edit clears them.
	const [invalid, setInvalid] = useState<ReadonlySet<string>>(new Set());
	const start = useMutation({
		...startModelLabPlaygroundRunMutation(),
		onSuccess: async (run) => {
			// The list must hold the run before the panel opens it.
			await queryClient.invalidateQueries({ queryKey: listModelLabPlaygroundRunsQueryKey() });
			onStarted(run.id);
		},
	});
	const edit = (apply: () => void): void => {
		apply();
		setInvalid(new Set());
	};
	// The SDK does not validate requests, so the generated schema checks the draft here.
	const submit = (): void => {
		const parsed = zStartModelLabPlaygroundRunBody.safeParse({ dataset, residual, sharedBlock, ...numbers });
		if (!parsed.success) {
			setInvalid(new Set(parsed.error.issues.map((issue) => String(issue.path[0]))));
			return;
		}
		start.mutate({ body: parsed.data });
	};
	const labels: Record<NumberKnob, string> = {
		learningRate: t("pages.modelLab.playground.knob.learningRate.label", "Learning rate"),
		depth: t("pages.modelLab.playground.knob.depth.label", "Depth"),
		hidden: t("pages.modelLab.playground.knob.hidden.label", "Hidden units"),
		steps: t("pages.modelLab.playground.knob.steps.label", "Training steps"),
		seed: t("pages.modelLab.playground.knob.seed.label", "Seed"),
	};
	const help: Record<NumberKnob | "dataset" | "residual" | "sharedBlock", string> = {
		dataset: t(
			"pages.modelLab.playground.knob.dataset.help",
			"The points the network learns to separate. XOR is four corners where opposite corners share a class, the smallest problem a network without a hidden layer cannot solve. Two spirals is 192 points on two interleaved spirals and needs a deeper network.\n\nStart with XOR. Switch to two spirals to see depth and residual connections matter.",
		),
		learningRate: t(
			"pages.modelLab.playground.knob.learningRate.help",
			"How far each update moves the weights against the gradient: after = before − learning rate × gradient. 0.5 suits XOR.\n\nRaising it makes the loss fall faster until the steps overshoot, and the loss rises or oscillates (at 10 it oscillates). Lowering it makes training slower and steadier. Change it to see an unstable or a slow run.",
		),
		residual: t(
			"pages.modelLab.playground.knob.residual.help",
			"Adds each block's input to its output (h + block(h)) instead of replacing it, so a signal and its gradient can pass a block unchanged. Off is the usual start.\n\nTurn it on for a deep network or the spirals, where it usually trains much better. Leave it off to see a plain stack.",
		),
		sharedBlock: t(
			"pages.modelLab.playground.knob.sharedBlock.help",
			"Runs one block, with one set of weights, at every depth instead of a separate block per depth. Off is the usual setting.\n\nTurn it on to see one parameter set visited several times: the stepper shows each visit's output and gradient, and checks that the visit gradients add up to the one gradient the update uses.",
		),
		depth: t(
			"pages.modelLab.playground.knob.depth.help",
			"How many hidden blocks run between the input layer and the output layer, from 1 to 4. 2 is typical.\n\nMore blocks let the network bend its boundary more, but the gradient travels further back; fewer keep it simple. Raise it for the spirals.",
		),
		hidden: t(
			"pages.modelLab.playground.knob.hidden.help",
			"How many values each hidden layer holds, from 2 to 16. 8 is typical.\n\nMore units make the network more expressive and its matrices larger to read; fewer make it easier to follow but it may fail to fit. Lower it to see a network too small to fit, raise it for the spirals.",
		),
		steps: t(
			"pages.modelLab.playground.knob.steps.help",
			"How many full-batch updates the run makes, from 1 to 2000. 500 fits XOR at the default learning rate.\n\nMore steps train longer, but the stepper still records at most 16 of them, spread evenly; with 16 or fewer every step is recorded. Lower it to see an unfinished fit.",
		),
		seed: t(
			"pages.modelLab.playground.knob.seed.help",
			"Picks the starting weights; the data does not depend on it. 0 is the default.\n\nThe same seed and knobs give the same run on this node. Change it to see whether a result depends on the starting weights.",
		),
	};
	const datasetLabel = t("pages.modelLab.playground.knob.dataset.label", "Dataset");
	const residualLabel = t("pages.modelLab.playground.knob.residual.label", "Residual connections");
	const sharedLabel = t("pages.modelLab.playground.knob.sharedBlock.label", "Share one block across depths");

	return (
		<Paper withBorder={true} p="md">
			<Stack gap="sm">
				<Text size="sm" c="dimmed">
					{t(
						"pages.modelLab.playground.scope",
						"Trains one tiny network (a tanh input layer and tanh hidden blocks, then a linear output layer) on the CPU in a few seconds. No model is needed and the GPU is not used. The run records up to 16 of its steps for the stepper below.",
					)}
				</Text>
				<SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }}>
					<FieldWithInfo label={datasetLabel} text={help.dataset} testId="model-lab-playground-dataset-info">
						<Select
							label={datasetLabel}
							data={[
								{ value: "xor", label: t("pages.modelLab.playground.dataset.xor", "XOR (4 points)") },
								{ value: "spirals", label: t("pages.modelLab.playground.dataset.spirals", "Two spirals (192 points)") },
							]}
							value={dataset}
							onChange={(value) => edit(() => setDataset(value))}
							allowDeselect={false}
							error={invalid.has("dataset")}
							data-testid="model-lab-playground-dataset"
						/>
					</FieldWithInfo>
					{numberKnobs.map((knob) => {
						const { minValue, maxValue } = knobs[knob];
						return (
							<FieldWithInfo key={knob} label={labels[knob]} text={help[knob]} testId={`model-lab-playground-${knob}-info`}>
								<NumberInput
									label={labels[knob]}
									min={minValue ?? undefined}
									max={maxValue ?? undefined}
									// Out-of-range input stays as typed, so the schema check on submit can say so.
									clampBehavior="none"
									allowDecimal={knob === "learningRate"}
									step={knob === "learningRate" ? 0.1 : 1}
									value={numbers[knob]}
									onChange={(value) => edit(() => setNumbers((previous) => ({ ...previous, [knob]: value })))}
									error={
										invalid.has(knob) &&
										t("pages.modelLab.playground.knob.range", "Enter a value from {{min}} to {{max}}.", {
											min: minValue,
											max: maxValue,
										})
									}
									data-testid={`model-lab-playground-${knob}`}
								/>
							</FieldWithInfo>
						);
					})}
				</SimpleGrid>
				<Group gap="lg">
					<FieldWithInfo label={residualLabel} text={help.residual} testId="model-lab-playground-residual-info">
						<Switch
							label={residualLabel}
							checked={residual}
							onChange={(event) => {
								const { checked } = event.currentTarget;
								edit(() => setResidual(checked));
							}}
							data-testid="model-lab-playground-residual"
						/>
					</FieldWithInfo>
					<FieldWithInfo label={sharedLabel} text={help.sharedBlock} testId="model-lab-playground-sharedBlock-info">
						<Switch
							label={sharedLabel}
							checked={sharedBlock}
							onChange={(event) => {
								const { checked } = event.currentTarget;
								edit(() => setSharedBlock(checked));
							}}
							data-testid="model-lab-playground-sharedBlock"
						/>
					</FieldWithInfo>
				</Group>
				<Group>
					<Button loading={start.isPending} onClick={submit} data-testid="model-lab-playground-start">
						{t("pages.modelLab.playground.start", "Train")}
					</Button>
					{start.isPending && (
						<Text size="sm" c="dimmed" role="status">
							{t("pages.modelLab.playground.training", "Training on the CPU…")}
						</Text>
					)}
				</Group>
				{start.isError && (
					<Alert color="yellow" data-testid="model-lab-playground-start-refused">
						{apiErrorMessage(start.error, t("pages.modelLab.playground.startFailed", "The run could not start."))}
					</Alert>
				)}
			</Stack>
		</Paper>
	);
}
