import { MatrixChart, type MatrixChartCell } from "@mantine/charts";
import { ActionIcon, Box, Button, Group, ScrollArea, SegmentedControl, Slider, Stack, Table, Text } from "@mantine/core";
import { IconChevronLeft, IconChevronRight } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { formatNumber } from "@/core/formatting/NumberFormatting";
import { BasisBadge } from "@/features/modelLab/components/ArchitectureExplanation";
import { moveFocusAmongButtons, useRovingTabStop } from "@/features/modelLab/models/ModelLabFocus";
import {
	missingPhase,
	type PlaygroundCheck,
	type PlaygroundGap,
	type PlaygroundPhase,
	type PlaygroundStep,
	type PlaygroundTensor,
	type PlaygroundTrace,
	playgroundPhases,
	playgroundValue,
	rows,
	updateCheck,
	visitSumCheck,
} from "@/features/modelLab/models/ModelLabPlayground";

function useGapText(): (gap: PlaygroundGap) => string {
	const { t } = useTranslation();
	return (gap) =>
		({
			lossNotFinite: t(
				"pages.modelLab.playground.gap.lossNotFinite",
				"The loss was not finite, so no backward pass or update ran.",
			),
			gradientNotFinite: t("pages.modelLab.playground.gap.gradientNotFinite", "A gradient was not finite, so no update ran."),
			nonFinite: t("pages.modelLab.playground.gap.nonFinite", "A value this check needs is not finite."),
			notShared: t(
				"pages.modelLab.playground.gap.notShared",
				"This parameter belongs to a module that runs once, so there are no visit gradients to add up.",
			),
			shapeMismatch: t(
				"pages.modelLab.playground.gap.shapeMismatch",
				"The recorded values this check compares do not have the same shape.",
			),
		})[gap];
}

// One saved playground run replayed step by step. Every value is from the recorded trace; nothing runs again.
export function PlaygroundStepper({ trace }: { trace: PlaygroundTrace }) {
	const { t } = useTranslation();
	const [at, setAt] = useState(0);
	const [phase, setPhase] = useState<PlaygroundPhase>("forward");
	const total = trace.recorded.length;
	const current = Math.min(at, Math.max(total - 1, 0));
	const step = trace.recorded[current];
	const lossStop = useRovingTabStop(current, total);
	const { outcome, config } = trace;
	const phaseLabels: Record<PlaygroundPhase, string> = {
		forward: t("pages.modelLab.playground.phase.forward.label", "Forward"),
		loss: t("pages.modelLab.playground.phase.loss.label", "Loss"),
		backward: t("pages.modelLab.playground.phase.backward.label", "Backward"),
		update: t("pages.modelLab.playground.phase.update.label", "Update"),
	};

	return (
		<Stack gap="sm" data-testid="model-lab-playground-stepper">
			<Text size="xs" c="dimmed" data-testid="model-lab-playground-provenance">
				{t(
					"pages.modelLab.playground.provenance",
					"Recorded playback: these values were recorded while the run trained on the CPU. Stepping replays them and does not run the network again.",
				)}{" "}
				{t("pages.modelLab.playground.provenanceRuntime", "PyTorch {{torch}}, float64, template {{template}}.", {
					torch: trace.torchVersion,
					template: trace.template,
				})}
			</Text>
			<Group gap="xs" data-testid="model-lab-playground-outcome">
				<Text size="sm">
					{outcome.kind === "diverged"
						? t(
								"pages.modelLab.playground.outcome.diverged",
								"Diverged at step {{step}}, after {{updates}} of {{steps}} updates.",
								{ step: (outcome.atStep ?? 0) + 1, updates: outcome.updatesApplied, steps: config.steps },
							)
						: t("pages.modelLab.playground.outcome.finished", "Finished all {{updates}} updates.", {
								updates: outcome.updatesApplied,
							})}
				</Text>
				<BasisBadge basis="measuredTrace" />
			</Group>
			{step === undefined ? (
				<Text size="sm" c="dimmed">
					{t("pages.modelLab.playground.noSteps", "This run recorded no steps.")}
				</Text>
			) : (
				<>
					<Stack gap={4}>
						<Group gap="xs">
							<Text size="sm" fw={600}>
								{t("pages.modelLab.playground.lossRow", "Loss at the recorded steps")}
							</Text>
							<BasisBadge basis="measuredTrace" />
						</Group>
						<Group
							gap={4}
							role="group"
							aria-label={t("pages.modelLab.playground.lossRow", "Loss at the recorded steps")}
							onKeyDown={moveFocusAmongButtons}
							data-testid="model-lab-playground-losses"
						>
							{trace.recorded.map((item, index) => (
								<Button
									key={item.step}
									size="compact-xs"
									variant={index === current ? "filled" : "default"}
									ff="monospace"
									aria-current={index === current ? "step" : undefined}
									tabIndex={lossStop.tabIndexFor(index)}
									onFocus={() => lossStop.onFocusAt(index)}
									onClick={() => setAt(index)}
									aria-label={t("pages.modelLab.playground.lossButton", "Step {{step}}: loss {{loss}}", {
										step: item.step + 1,
										loss: playgroundValue(item.loss.value, t),
									})}
								>
									{playgroundValue(item.loss.value, t)}
								</Button>
							))}
						</Group>
					</Stack>
					<Group gap="xs" wrap="nowrap">
						<ActionIcon
							variant="default"
							disabled={current === 0}
							onClick={() => setAt(current - 1)}
							aria-label={t("pages.modelLab.playground.previous", "Previous recorded step")}
							data-testid="model-lab-playground-previous"
						>
							<IconChevronLeft size={16} aria-hidden={true} />
						</ActionIcon>
						<Slider
							flex={1}
							min={1}
							max={Math.max(total, 2)}
							disabled={total === 1}
							step={1}
							value={current + 1}
							onChange={(value) => setAt(value - 1)}
							thumbLabel={t("pages.modelLab.playground.slider", "Recorded step")}
							label={null}
							data-testid="model-lab-playground-slider"
						/>
						<ActionIcon
							variant="default"
							disabled={current === total - 1}
							onClick={() => setAt(current + 1)}
							aria-label={t("pages.modelLab.playground.next", "Next recorded step")}
							data-testid="model-lab-playground-next"
						>
							<IconChevronRight size={16} aria-hidden={true} />
						</ActionIcon>
					</Group>
					<Text size="sm" data-testid="model-lab-playground-position">
						{t(
							"pages.modelLab.playground.position",
							"Recorded step {{index}} of {{count}}: training step {{step}} of {{steps}}",
							{ index: current + 1, count: total, step: step.step + 1, steps: config.steps },
						)}
					</Text>
					<SegmentedControl
						aria-label={t("pages.modelLab.playground.phaseLabel", "Phase of the step")}
						value={phase}
						onChange={(value) => setPhase(value as PlaygroundPhase)}
						w="fit-content"
						data={playgroundPhases.map((value) => ({ value, label: phaseLabels[value] }))}
						data-testid="model-lab-playground-phase"
					/>
					<PhaseView trace={trace} step={step} phase={phase} />
				</>
			)}
		</Stack>
	);
}

interface PhaseViewProps {
	trace: PlaygroundTrace;
	step: PlaygroundStep;
	phase: PlaygroundPhase;
}

function PhaseView({ trace, step, phase }: PhaseViewProps) {
	const { t } = useTranslation();
	const gapText = useGapText();
	const gap = missingPhase(step, phase);
	const about = {
		forward: t(
			"pages.modelLab.playground.phase.forward.about",
			"Each module's input and output at the shown points, in the order the forward pass ran them. A shared block appears once per visit. The output of embed and of each block is taken after its tanh; the output of head is its Linear output, the logit.",
		),
		loss: t(
			"pages.modelLab.playground.phase.loss.about",
			"The mean binary cross-entropy of the logits against the targets over all points. Accuracy counts a logit above 0 as class 1.",
		),
		backward: t(
			"pages.modelLab.playground.phase.backward.about",
			"The gradient of the mean loss with respect to each module output as the forward phase shows it (after tanh for embed and each block, the logit for head), at the shown points, and to each parameter. A shared block's parameter gets one gradient per visit, and autograd adds them up.",
		),
		update: t(
			"pages.modelLab.playground.phase.update.about",
			"Each parameter before and after this step's SGD update: after = before − learning rate × gradient.",
		),
	}[phase];
	return (
		<Stack gap="xs" data-testid="model-lab-playground-phase-view">
			<Group gap="xs" wrap="nowrap" align="flex-start">
				<Text size="sm" c="dimmed">
					{about}
				</Text>
				<BasisBadge basis="interpreted" />
			</Group>
			{gap !== null ? (
				<Group gap="xs" data-testid="model-lab-playground-phase-missing">
					<Text size="sm">{gapText(gap)}</Text>
					<BasisBadge basis="unavailable" />
				</Group>
			) : phase === "forward" ? (
				<ForwardView trace={trace} step={step} />
			) : phase === "loss" ? (
				<LossView trace={trace} step={step} />
			) : phase === "backward" ? (
				<BackwardView trace={trace} step={step} />
			) : (
				<UpdateView trace={trace} step={step} />
			)}
		</Stack>
	);
}

function useOccurrenceLabel(trace: PlaygroundTrace): (o: number) => string {
	const { t } = useTranslation();
	const shared = new Set(trace.occurrences.filter((item) => item.visit > 0).map((item) => item.module));
	return (o) => {
		const occurrence = trace.occurrences[o];
		if (occurrence === undefined) {
			return String(o);
		}
		return shared.has(occurrence.module)
			? t("pages.modelLab.playground.visit", "{{module}}, visit {{visit}}", {
					module: occurrence.module,
					visit: occurrence.visit + 1,
				})
			: occurrence.module;
	};
}

// The activation an occurrence's recorded output comes after (tanh), or null when it is the module's own output.
function outputActivation(trace: PlaygroundTrace, o: number): string | null {
	const module = trace.occurrences[o]?.module;
	return trace.modules.find((item) => item.path === module)?.outputActivation ?? null;
}

interface ChipsProps {
	label: string;
	items: readonly { value: number; label: string }[];
	value: number;
	onChange: (value: number) => void;
	"data-testid": string;
}

// A row of toggle buttons with one Tab stop; the arrow keys move between them.
function Chips({ label, items, value, onChange, "data-testid": testId }: ChipsProps) {
	const stop = useRovingTabStop(
		items.findIndex((item) => item.value === value),
		items.length,
	);
	return (
		<Group gap={4} role="group" aria-label={label} onKeyDown={moveFocusAmongButtons} data-testid={testId}>
			{items.map((item, index) => (
				<Button
					key={item.value}
					size="compact-xs"
					variant={item.value === value ? "filled" : "default"}
					ff="monospace"
					aria-pressed={item.value === value}
					tabIndex={stop.tabIndexFor(index)}
					onFocus={() => stop.onFocusAt(index)}
					onClick={() => onChange(item.value)}
				>
					{item.label}
				</Button>
			))}
		</Group>
	);
}

function ForwardView({ trace, step }: { trace: PlaygroundTrace; step: PlaygroundStep }) {
	const { t } = useTranslation();
	const label = useOccurrenceLabel(trace);
	const [picked, setPicked] = useState(0);
	const observation = step.forward.find((item) => item.o === picked) ?? step.forward[0];
	if (observation === undefined) {
		return null;
	}
	const activation = outputActivation(trace, observation.o);
	return (
		<Stack gap="xs">
			<Chips
				label={t("pages.modelLab.playground.occurrences", "Modules in execution order")}
				items={step.forward.map((item) => ({ value: item.o, label: label(item.o) }))}
				value={observation.o}
				onChange={setPicked}
				data-testid="model-lab-playground-occurrences"
			/>
			<PointTable
				trace={trace}
				title={t("pages.modelLab.playground.input", "Input of {{module}}", { module: label(observation.o) })}
				value={observation.input}
			/>
			<PointTable
				trace={trace}
				title={
					activation === null
						? t("pages.modelLab.playground.output", "Output of {{module}}", { module: label(observation.o) })
						: t("pages.modelLab.playground.outputAfter", "Output of {{module}} (after {{activation}})", {
								module: label(observation.o),
								activation,
							})
				}
				value={observation.output}
			/>
			{observation.stream && (
				<PointTable
					trace={trace}
					title={t("pages.modelLab.playground.stream", "Residual stream after {{module}} (input + output)", {
						module: label(observation.o),
					})}
					value={observation.stream}
				/>
			)}
		</Stack>
	);
}

function LossView({ trace, step }: { trace: PlaygroundTrace; step: PlaygroundStep }) {
	const { t } = useTranslation();
	return (
		<Stack gap="xs">
			<Group gap="xs">
				<Text size="sm" data-testid="model-lab-playground-loss">
					{t("pages.modelLab.playground.lossValue", "Loss {{loss}}, accuracy {{accuracy}} over all {{count}} points", {
						loss: playgroundValue(step.loss.value, t),
						accuracy: playgroundValue(step.loss.accuracy, t),
						count: trace.data.count,
					})}
				</Text>
				<BasisBadge basis="measuredTrace" />
			</Group>
			<PointTable
				trace={trace}
				title={t("pages.modelLab.playground.logits", "Logit and target at the shown points")}
				value={step.loss.probeLogits.map((logit, at) => [logit, trace.data.y[trace.data.probes[at] ?? 0] ?? null])}
				columns={[t("pages.modelLab.playground.logit", "Logit"), t("pages.modelLab.playground.target", "Target")]}
			/>
		</Stack>
	);
}

function useParameterChips(trace: PlaygroundTrace) {
	const [picked, setPicked] = useState(0);
	return {
		picked: Math.min(picked, Math.max(trace.parameters.length - 1, 0)),
		setPicked,
		items: trace.parameters.map((parameter, p) => ({ value: p, label: parameter.id })),
	};
}

function BackwardView({ trace, step }: { trace: PlaygroundTrace; step: PlaygroundStep }) {
	const { t } = useTranslation();
	const label = useOccurrenceLabel(trace);
	const [pickedOccurrence, setPickedOccurrence] = useState(0);
	const parameters = useParameterChips(trace);
	const backward = step.backward;
	if (backward === null) {
		return null;
	}
	const output = backward.outputGrads.find((item) => item.o === pickedOccurrence) ?? backward.outputGrads[0];
	const activation = output === undefined ? null : outputActivation(trace, output.o);
	const p = parameters.picked;
	const id = trace.parameters[p]?.id ?? "";
	const grad = backward.parameterGrads.find((item) => item.p === p);
	const visits = backward.visitGrads.filter((item) => item.p === p);
	return (
		<Stack gap="xs">
			{output && (
				<>
					<Chips
						label={t("pages.modelLab.playground.occurrences", "Modules in execution order")}
						items={backward.outputGrads.map((item) => ({ value: item.o, label: label(item.o) }))}
						value={output.o}
						onChange={setPickedOccurrence}
						data-testid="model-lab-playground-grad-occurrences"
					/>
					<PointTable
						trace={trace}
						title={
							activation === null
								? t("pages.modelLab.playground.outputGrad", "Gradient at the output of {{module}}", { module: label(output.o) })
								: t("pages.modelLab.playground.outputGradAfter", "Gradient at the output of {{module}} (after {{activation}})", {
										module: label(output.o),
										activation,
									})
						}
						value={output.grad}
					/>
				</>
			)}
			<Chips
				label={t("pages.modelLab.playground.parameters", "Parameters")}
				items={parameters.items}
				value={p}
				onChange={parameters.setPicked}
				data-testid="model-lab-playground-grad-parameters"
			/>
			{grad && (
				<TensorView
					title={t("pages.modelLab.playground.parameterGrad", "Gradient of {{parameter}}", { parameter: id })}
					value={grad.grad}
				/>
			)}
			{visits.map((visit) => (
				<TensorView
					key={visit.o}
					title={t("pages.modelLab.playground.visitGrad", "Gradient of {{parameter}} from {{module}}", {
						parameter: id,
						module: label(visit.o),
					})}
					value={visit.grad}
				/>
			))}
			<CheckLine
				title={t("pages.modelLab.playground.visitSum", "Visit gradients add up to the parameter gradient")}
				check={visitSumCheck(step, p)}
				testId="model-lab-playground-visit-check"
			/>
		</Stack>
	);
}

function UpdateView({ trace, step }: { trace: PlaygroundTrace; step: PlaygroundStep }) {
	const { t } = useTranslation();
	const parameters = useParameterChips(trace);
	const p = parameters.picked;
	const id = trace.parameters[p]?.id ?? "";
	const update = step.update?.find((item) => item.p === p);
	return (
		<Stack gap="xs">
			<Chips
				label={t("pages.modelLab.playground.parameters", "Parameters")}
				items={parameters.items}
				value={p}
				onChange={parameters.setPicked}
				data-testid="model-lab-playground-update-parameters"
			/>
			{update && (
				<>
					<TensorView
						title={t("pages.modelLab.playground.before", "{{parameter}} before the update", { parameter: id })}
						value={update.before}
					/>
					<TensorView
						title={t("pages.modelLab.playground.after", "{{parameter}} after the update", { parameter: id })}
						value={update.after}
					/>
				</>
			)}
			<CheckLine
				title={t("pages.modelLab.playground.updateCheck", "After = before − learning rate × gradient")}
				check={updateCheck(step, p, trace.config.learningRate)}
				testId="model-lab-playground-update-check"
			/>
		</Stack>
	);
}

function CheckLine({ title, check, testId }: { title: string; check: PlaygroundCheck; testId: string }) {
	const { t } = useTranslation();
	const gapText = useGapText();
	const scientific = (value: number): string => formatNumber(value, { notation: "scientific", maximumSignificantDigits: 2 });
	return (
		<Stack gap={2} data-testid={testId}>
			<Group gap="xs">
				<Text size="sm" fw={600}>
					{title}
				</Text>
				<BasisBadge basis={check.kind === "checked" ? "derived" : "unavailable"} />
			</Group>
			<Text size="sm" c={check.kind === "checked" && !check.withinTolerance ? "red" : undefined}>
				{check.kind === "unavailable"
					? gapText(check.gap)
					: check.withinTolerance
						? t(
								"pages.modelLab.playground.check.within",
								"Largest difference {{residual}}: within the tolerance of 1e-12 times the values' size.",
								{ residual: scientific(check.residual) },
							)
						: t(
								"pages.modelLab.playground.check.outside",
								"Largest difference {{residual}}: outside the tolerance of 1e-12 times the values' size.",
								{ residual: scientific(check.residual) },
							)}
			</Text>
		</Stack>
	);
}

interface PointTableProps {
	trace: PlaygroundTrace;
	title: string;
	// [probe][column]
	value: (number | null)[][];
	columns?: readonly string[];
}

// Values at the probe points: one row per point, named by its coordinates.
function PointTable({ trace, title, value, columns }: PointTableProps) {
	const { t } = useTranslation();
	const width = value[0]?.length ?? 0;
	return (
		<ValueTable
			title={title}
			header={columns ?? Array.from({ length: width }, (_, at) => String(at))}
			rows={value.map((cells, at) => {
				const point = trace.data.probes[at] ?? at;
				return {
					label: t("pages.modelLab.playground.point", "Point {{index}} ({{x}})", {
						index: point,
						x: (trace.data.x[point] ?? []).map((value) => playgroundValue(value, t)).join(", "),
					}),
					cells,
				};
			})}
		/>
	);
}

interface ValueTableProps {
	title: string;
	header: readonly string[];
	rows: readonly { label: string; cells: readonly (number | null)[] }[];
}

function ValueTable({ title, header, rows: tableRows }: ValueTableProps) {
	const { t } = useTranslation();
	return (
		<Stack gap={4} data-testid="model-lab-playground-values">
			<Group gap="xs">
				<Text size="sm" fw={600}>
					{title}
				</Text>
				<BasisBadge basis="measuredTrace" />
			</Group>
			<ScrollArea type="auto">
				<Table withTableBorder={true} withColumnBorders={true} fz="xs" ff="monospace" w="fit-content">
					<Table.Thead>
						<Table.Tr>
							<Table.Th />
							{header.map((column) => (
								<Table.Th key={column} ta="right">
									{column}
								</Table.Th>
							))}
						</Table.Tr>
					</Table.Thead>
					<Table.Tbody>
						{tableRows.map((row) => (
							<Table.Tr key={row.label}>
								<Table.Th style={{ whiteSpace: "nowrap" }}>{row.label}</Table.Th>
								{row.cells.map((cell, at) => (
									// biome-ignore lint/suspicious/noArrayIndexKey: a column is its position in the tensor.
									<Table.Td key={at} ta="right" title={cell === null ? undefined : String(cell)}>
										{playgroundValue(cell, t)}
									</Table.Td>
								))}
							</Table.Tr>
						))}
					</Table.Tbody>
				</Table>
			</ScrollArea>
		</Stack>
	);
}

const cellSize = 14;

// A parameter or its gradient: a weight's cells as a map (decorative; the table carries the values) above its table.
function TensorView({ title, value }: { title: string; value: PlaygroundTensor }) {
	const cells = rows(value);
	const width = cells[0]?.length ?? 0;
	const peak = Math.max(0, ...cells.flat().map((cell) => Math.abs(cell ?? 0)));
	return (
		<Stack gap={4}>
			<ValueTable
				title={title}
				header={Array.from({ length: width }, (_, at) => String(at))}
				rows={cells.map((row, at) => ({ label: String(at), cells: row }))}
			/>
			{cells.length > 1 && width > 1 && (
				<Box aria-hidden={true} data-testid="model-lab-playground-map">
					<MatrixChart
						data={cells.flatMap((row, y) => row.map((cell, x) => ({ x: String(x), y: String(y), value: cell })))}
						xLabels={Array.from({ length: width }, (_, at) => String(at))}
						yLabels={cells.map((_, at) => String(at))}
						domain={[-1, 1]}
						cellSize={cellSize}
						cellRadius={0}
						// Without it a null cell gets data-empty, whose stylesheet fill beats the outline below.
						emptyColor="transparent"
						// Positive in the primary color, negative in red, each scaled to the largest magnitude; a non-finite cell is outlined.
						getCellProps={(cell: MatrixChartCell) =>
							cell.value === null
								? { fill: "transparent", stroke: "var(--mantine-color-text)", strokeDasharray: "2 1" }
								: {
										fill: `color-mix(in srgb, ${cell.value < 0 ? "var(--mantine-color-red-filled)" : "var(--mantine-primary-color-filled)"} ${Math.round((100 * Math.abs(cell.value)) / (peak > 0 ? peak : 1))}%, transparent)`,
									}
						}
					/>
				</Box>
			)}
		</Stack>
	);
}
