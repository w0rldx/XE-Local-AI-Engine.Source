import {
	Alert,
	Badge,
	Group,
	Loader,
	Paper,
	ScrollArea,
	SegmentedControl,
	SimpleGrid,
	Stack,
	Table,
	Text,
	Title,
	UnstyledButton,
} from "@mantine/core";
import { useQueries } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { getModelLabExperimentOptions, getModelLabExperimentTraceOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import type { XeLocalAiEngineClientEndpointsModelLabV1ModelLabExperimentResponse as Experiment } from "@/core/api/generated/types.gen";
import { logprobPercentLabel, logprobTextLabel } from "@/core/directInference/DirectInferenceModels";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { NextTokenStepView } from "@/features/modelLab/components/NextTokenStepView";
import { CandidateNoteText } from "@/features/modelLab/components/TraceStepCandidates";
import {
	commonLayerMeans,
	compareFacts,
	type FactKey,
	type FactRow,
	firstDivergence,
	pairSteeringArms,
	sameCheckpoint,
} from "@/features/modelLab/models/ModelLabComparison";
import { type CandidateBasis, candidateNote, toStepEntry } from "@/features/modelLab/models/ModelLabTrace";

const differsBackground = "var(--mantine-color-yellow-light)";
const selectedBackground = "var(--mantine-primary-color-light)";
const cellStyle = { overflowWrap: "anywhere", whiteSpace: "pre-wrap", verticalAlign: "top" } as const;

function factLabel(t: TFunction, key: FactKey): string {
	switch (key) {
		case "repo":
			return t("pages.modelLab.savedCompare.fact.repo", "Checkpoint repository");
		case "revision":
			return t("pages.modelLab.savedCompare.fact.revision", "Revision");
		case "installedModel":
			return t("pages.modelLab.savedCompare.fact.installedModel", "Installed model");
		case "device":
			return t("pages.modelLab.savedCompare.fact.device", "Device");
		case "torch":
			return t("pages.modelLab.savedCompare.fact.torch", "torch");
		case "transformers":
			return t("pages.modelLab.savedCompare.fact.transformers", "transformers");
		case "cuda":
			return t("pages.modelLab.savedCompare.fact.cuda", "CUDA");
		case "appVersion":
			return t("pages.modelLab.savedCompare.fact.appVersion", "App version");
		case "seed":
			return t("pages.modelLab.savedCompare.fact.seed", "Seed");
		case "layers":
			return t("pages.modelLab.savedCompare.fact.layers", "Traced blocks");
		case "maxNewTokens":
			return t("pages.modelLab.savedCompare.fact.maxNewTokens", "Max new tokens");
		case "steeringLayer":
			return t("pages.modelLab.savedCompare.fact.steeringLayer", "Steered block");
		case "scale":
			return t("pages.modelLab.savedCompare.fact.scale", "Scale");
		case "armBuilds":
			return t("pages.modelLab.savedCompare.fact.armBuilds", "Arm builds");
		case "vectorNorm":
			return t("pages.modelLab.savedCompare.fact.vectorNorm", "Vector length");
		case "positivePrompts":
			return t("pages.modelLab.savedCompare.fact.positivePrompts", "Positive prompts");
		case "negativePrompts":
			return t("pages.modelLab.savedCompare.fact.negativePrompts", "Negative prompts");
		default:
			return t("pages.modelLab.savedCompare.fact.prompt", "Prompt");
	}
}

function runLabel(t: TFunction, index: number): string {
	return t("pages.modelLab.savedCompare.run", "Run {{number}}", { number: index + 1 });
}

function NotRecorded() {
	const { t } = useTranslation();
	return (
		<Text component="span" size="sm" c="dimmed">
			{t("pages.modelLab.savedCompare.notRecorded", "Not recorded")}
		</Text>
	);
}

function FactCells({ row }: { row: FactRow }) {
	const { t } = useTranslation();
	// A prompt or prompt set every run shares is printed once, across the columns.
	if ((row.key === "prompt" || row.key === "positivePrompts" || row.key === "negativePrompts") && row.state === "same") {
		return (
			<Table.Td colSpan={row.values.length} style={cellStyle}>
				<Text size="xs" c="dimmed">
					{t("pages.modelLab.savedCompare.samePrompt", "Same in every run:")}
				</Text>
				{row.values[0]}
			</Table.Td>
		);
	}
	return row.values.map((value, index) => (
		// biome-ignore lint/suspicious/noArrayIndexKey: one cell per run, in the runs' fixed order.
		<Table.Td key={index} style={cellStyle}>
			{value ?? <NotRecorded />}
		</Table.Td>
	));
}

// Every fact that could explain a difference, then each run's note. Differing rows carry a badge, not only a colour.
function FactsTable({ runs, facts }: { runs: Experiment[]; facts: FactRow[] }) {
	const { t } = useTranslation();
	return (
		<Table.ScrollContainer minWidth={160 + runs.length * 180} type="native">
			<Table data-testid="model-lab-compare-facts">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.modelLab.savedCompare.fact.title", "Fact")}</Table.Th>
						{runs.map((run, index) => (
							<Table.Th key={run.id}>
								{runLabel(t, index)}
								<Text size="xs" c="dimmed" fw={400}>
									{formatTimestamp(run.createdAtUtc)}
								</Text>
							</Table.Th>
						))}
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{facts.map((row) => (
						<Table.Tr
							key={row.key}
							bg={row.state === "differs" ? differsBackground : undefined}
							data-testid={`model-lab-compare-fact-${row.key}`}
							data-state={row.state}
						>
							<Table.Th scope="row" style={cellStyle}>
								<Stack gap={2} align="flex-start">
									{factLabel(t, row.key)}
									{row.state === "differs" && (
										<Badge size="xs" color="yellow" variant="filled">
											{t("pages.modelLab.savedCompare.differs", "Differs")}
										</Badge>
									)}
									{row.state === "unrecorded" && (
										<Badge size="xs" color="gray" variant="light">
											{t("pages.modelLab.savedCompare.unchecked", "Not checked")}
										</Badge>
									)}
								</Stack>
							</Table.Th>
							<FactCells row={row} />
						</Table.Tr>
					))}
					<Table.Tr data-testid="model-lab-compare-notes">
						<Table.Th scope="row">{t("pages.modelLab.savedCompare.note", "Your note")}</Table.Th>
						{runs.map((run) => (
							<Table.Td key={run.id} style={cellStyle}>
								{run.notes || (
									<Text component="span" size="sm" c="dimmed">
										{t("pages.modelLab.savedCompare.noNote", "No note")}
									</Text>
								)}
							</Table.Td>
						))}
					</Table.Tr>
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}

function TraceComparison({ runs }: { runs: Experiment[] }) {
	const { t } = useTranslation();
	const [selectedStep, setSelectedStep] = useState<number>();
	const [basis, setBasis] = useState<CandidateBasis>("raw");
	// Each run's blob, fetched once the comparison is open, with the query the single view uses.
	const traces = useQueries({
		queries: runs.map((run) => getModelLabExperimentTraceOptions({ path: { experimentId: run.id } })),
	});
	const tokens = runs.map((run) => run.result?.tokens ?? []);
	const byText = !sameCheckpoint(runs);
	const divergence = firstDivergence(tokens, byText);
	const steps = Array.from({ length: Math.max(0, ...tokens.map((list) => list.length)) }, (_, step) => step);
	// A contract-7 run counts even when it dropped every step's candidates: its steps then say "not kept".
	const hasCandidates =
		runs.some((run) => run.result?.candidateSteps !== null && run.result?.candidateSteps !== undefined) ||
		tokens.some((list) => list.some((token) => token.candidates));
	const traceError = traces.find((trace) => trace.status === "error")?.error;
	const records = traces.every((trace) => trace.status === "success")
		? traces.map((trace) => trace.data?.records ?? [])
		: undefined;
	const means = records && commonLayerMeans(records);
	const truncated = traces.flatMap((trace, index) => (trace.data?.truncated ? [runLabel(t, index)] : []));

	return (
		<Stack gap="sm">
			<Title order={5}>{t("pages.modelLab.trace.output", "Output")}</Title>
			<SimpleGrid cols={{ base: 1, md: Math.min(runs.length, 2), lg: runs.length }} spacing="xs">
				{runs.map((run, index) => (
					<Paper key={run.id} withBorder={true} p="xs" data-testid="model-lab-compare-output">
						<Stack gap={4}>
							<Badge variant="light" color="gray" w="fit-content">
								{runLabel(t, index)}
							</Badge>
							<Text size="sm" style={cellStyle}>
								{run.result?.text}
							</Text>
						</Stack>
					</Paper>
				))}
			</SimpleGrid>
			<Text size="sm" data-testid="model-lab-compare-divergence">
				{divergence === undefined
					? t("pages.modelLab.savedCompare.noDivergence", "Every run chose the same tokens.")
					: t("pages.modelLab.savedCompare.divergence", "The chosen tokens first differ at step {{step}}.", {
							step: divergence + 1,
						})}
			</Text>
			{byText && (
				<Text size="xs" c="dimmed" data-testid="model-lab-compare-by-text">
					{t(
						"pages.modelLab.savedCompare.byText",
						"These runs come from different checkpoints, so tokens are compared by their text, not by id.",
					)}
				</Text>
			)}
			<ScrollArea.Autosize mah={420} type="auto">
				<Table striped={true} stickyHeader={true} miw={120 + runs.length * 160} data-testid="model-lab-compare-steps">
					<Table.Thead>
						<Table.Tr>
							<Table.Th>{t("pages.modelLab.savedCompare.step", "Step")}</Table.Th>
							{runs.map((run, index) => (
								<Table.Th key={run.id}>
									{t("pages.modelLab.savedCompare.tokenOf", "{{run}}: token and probability", { run: runLabel(t, index) })}
								</Table.Th>
							))}
						</Table.Tr>
					</Table.Thead>
					<Table.Tbody>
						{steps.map((step) => {
							const diverges = step === divergence;
							return (
								<Table.Tr
									key={step}
									bg={step === selectedStep ? selectedBackground : diverges ? differsBackground : undefined}
									data-testid="model-lab-compare-step-row"
									data-divergence={diverges || undefined}
								>
									<Table.Td>
										<Group gap={4} wrap="nowrap">
											{hasCandidates ? (
												<UnstyledButton
													aria-pressed={step === selectedStep}
													onClick={() => setSelectedStep(step)}
													data-testid="model-lab-compare-step"
												>
													{t("pages.modelLab.savedCompare.stepButton", "Step {{step}}", { step: step + 1 })}
												</UnstyledButton>
											) : (
												step + 1
											)}
											{diverges && (
												<Badge size="xs" color="yellow" variant="filled" data-testid="model-lab-compare-first-difference">
													{t("pages.modelLab.savedCompare.firstDifference", "First difference")}
												</Badge>
											)}
										</Group>
									</Table.Td>
									{tokens.map((list, index) => {
										const token = list[step];
										return (
											// biome-ignore lint/suspicious/noArrayIndexKey: one cell per run, in the runs' fixed order.
											<Table.Td key={index}>
												{token ? (
													<Group gap={6} wrap="nowrap">
														<Text size="sm" ff="monospace" style={{ whiteSpace: "pre" }}>
															{logprobTextLabel(token.text)}
														</Text>
														<Text size="xs" c="dimmed">
															{token.logprob === null || token.logprob === undefined ? "–" : logprobPercentLabel(token.logprob)}
														</Text>
													</Group>
												) : (
													<Text size="xs" c="dimmed">
														{t("pages.modelLab.savedCompare.ended", "Ended")}
													</Text>
												)}
											</Table.Td>
										);
									})}
								</Table.Tr>
							);
						})}
					</Table.Tbody>
				</Table>
			</ScrollArea.Autosize>
			{hasCandidates && selectedStep === undefined && (
				<Text size="sm" c="dimmed">
					{t("pages.modelLab.savedCompare.pickStep", "Pick a step to see each run's next-token candidates.")}
				</Text>
			)}
			{selectedStep !== undefined && (
				<Stack gap="xs" data-testid="model-lab-compare-candidates">
					<SegmentedControl
						value={basis}
						onChange={(value) => setBasis(value === "processed" ? "processed" : "raw")}
						aria-label={t("pages.modelLab.trace.basisLabel", "Candidates from")}
						data={[
							{ value: "raw", label: t("pages.modelLab.trace.basisRaw", "Raw logits") },
							{ value: "processed", label: t("pages.modelLab.trace.basisProcessed", "Processed scores") },
						]}
					/>
					{tokens.map((list, index) => {
						const token = list[selectedStep];
						const entry = token && toStepEntry(token, basis);
						const candidateSteps = runs[index]?.result?.candidateSteps;
						return (
							// biome-ignore lint/suspicious/noArrayIndexKey: one panel per run, in the runs' fixed order.
							<Paper key={index} withBorder={true} p="sm">
								<Stack gap="xs">
									<Badge variant="light" color="gray" w="fit-content">
										{runLabel(t, index)}
									</Badge>
									{entry ? (
										<NextTokenStepView
											entry={entry}
											stepNumber={selectedStep + 1}
											trace={{
												processed: basis === "processed",
												basis:
													basis === "raw"
														? t(
																"pages.modelLab.trace.basisRawNote",
																"Computed by PyTorch from the model's raw logits over the whole vocabulary",
															)
														: t(
																"pages.modelLab.trace.basisProcessedNote",
																"Computed by PyTorch from the processed scores, after the checkpoint's generation config processors",
															),
											}}
										/>
									) : token ? (
										<CandidateNoteText note={candidateNote(token, selectedStep, candidateSteps)} />
									) : (
										<Text size="sm" c="dimmed">
											{t("pages.modelLab.savedCompare.endedBefore", "This run ended before this step.")}
										</Text>
									)}
								</Stack>
							</Paper>
						);
					})}
				</Stack>
			)}
			<Title order={5}>{t("pages.modelLab.savedCompare.meansTitle", "Mean norm per block")}</Title>
			<Text size="sm" c="dimmed">
				{t(
					"pages.modelLab.savedCompare.meansNote",
					"Derived: the mean of the measured norms over each run's generated tokens, for the blocks every run traced.",
				)}
			</Text>
			{traceError !== undefined && (
				<Alert color="red">
					{apiErrorMessage(traceError, t("pages.modelLab.trace.traceFailed", "The trace could not be loaded."))}
				</Alert>
			)}
			{traceError === undefined && !means && <Loader size="sm" />}
			{truncated.length > 0 && (
				<Text size="sm" c="dimmed" data-testid="model-lab-compare-truncated">
					{t(
						"pages.modelLab.savedCompare.truncated",
						"The trace of {{runs}} reached its cap: its mean covers only the records it kept.",
						{ runs: truncated.join(", ") },
					)}
				</Text>
			)}
			{means && means.layers.length === 0 && (
				<Text size="sm" c="dimmed" data-testid="model-lab-compare-no-common-layers">
					{t("pages.modelLab.savedCompare.noCommonLayers", "These runs traced no block in common.")}
				</Text>
			)}
			{means && means.layers.length > 0 && (
				<Table.ScrollContainer minWidth={160 + runs.length * 120} type="native">
					<Table striped={true} data-testid="model-lab-compare-means">
						<Table.Thead>
							<Table.Tr>
								<Table.Th>{t("pages.modelLab.savedCompare.block", "Block")}</Table.Th>
								{runs.map((run, index) => (
									<Table.Th key={run.id} ta="right">
										{runLabel(t, index)}
									</Table.Th>
								))}
							</Table.Tr>
						</Table.Thead>
						<Table.Tbody>
							{means.layers.map((layer, row) => (
								<Table.Tr key={layer.index}>
									<Table.Th scope="row">
										{t("pages.modelLab.savedCompare.blockName", "Block {{index}} ({{kind}})", {
											index: layer.index,
											kind: layer.kind,
										})}
									</Table.Th>
									{means.means[row]?.map((mean, index) => (
										// biome-ignore lint/suspicious/noArrayIndexKey: one cell per run, in the runs' fixed order.
										<Table.Td key={index} ta="right">
											{mean === null ? "–" : mean.toFixed(2)}
										</Table.Td>
									))}
								</Table.Tr>
							))}
						</Table.Tbody>
					</Table>
				</Table.ScrollContainer>
			)}
		</Stack>
	);
}

function SteeringComparison({ runs }: { runs: Experiment[] }) {
	const { t } = useTranslation();
	const armLabels: Record<string, string> = {
		baseline: t("pages.modelLab.steer.armBaseline", "Baseline"),
		vector: t("pages.modelLab.steer.armVector", "Vector"),
		random: t("pages.modelLab.steer.armRandom", "Random control"),
	};
	const arms = pairSteeringArms(runs.map((run) => run.result?.steering));
	return (
		<Stack gap="sm">
			{arms.map(({ name, rows }) => (
				<Stack key={name} gap="xs" data-testid={`model-lab-compare-arm-${name}`}>
					<Title order={5}>{armLabels[name]}</Title>
					{rows.map((row) => (
						<Stack key={row.position} gap={4}>
							{row.samePrompt && (
								<Text size="sm" fw={600} style={cellStyle}>
									{row.prompts[0]}
								</Text>
							)}
							<SimpleGrid cols={{ base: 1, md: Math.min(runs.length, 2), lg: runs.length }} spacing="xs">
								{row.texts.map((text, index) => (
									// biome-ignore lint/suspicious/noArrayIndexKey: one panel per run, in the runs' fixed order.
									<Paper key={index} withBorder={true} p="xs">
										<Stack gap={4}>
											<Badge variant="light" color="gray" w="fit-content">
												{runLabel(t, index)}
											</Badge>
											{!row.samePrompt && row.prompts[index] && (
												<Text size="xs" fw={600} style={cellStyle}>
													{row.prompts[index]}
												</Text>
											)}
											{text === undefined ? (
												<Text size="sm" c="dimmed">
													{t("pages.modelLab.steer.armMissing", "This arm did not finish.")}
												</Text>
											) : (
												<Text size="sm" style={cellStyle}>
													{text}
												</Text>
											)}
										</Stack>
									</Paper>
								))}
							</SimpleGrid>
						</Stack>
					))}
				</Stack>
			))}
		</Stack>
	);
}

// Two to four saved runs of one kind side by side. Every fact that differs is listed first: a difference in the
// results may come from it, and the view never says it did.
export function SavedExperimentComparison({ experimentIds }: { experimentIds: string[] }) {
	const { t } = useTranslation();
	const details = useQueries({
		queries: experimentIds.map((experimentId) => getModelLabExperimentOptions({ path: { experimentId } })),
	});
	const failed = details.find((detail) => detail.status === "error");
	if (failed) {
		return (
			<Alert color="red">
				{apiErrorMessage(failed.error, t("pages.modelLab.trace.loadFailed", "The experiment could not be loaded."))}
			</Alert>
		);
	}
	const runs = details.flatMap((detail) => (detail.data ? [detail.data] : []));
	if (runs.length < experimentIds.length) {
		return <Loader size="sm" />;
	}

	const facts = compareFacts(runs);
	const names = (state: FactRow["state"]) =>
		facts
			.filter((row) => row.state === state)
			.map((row) => factLabel(t, row.key))
			.join(", ");
	const differing = names("differs");
	const unchecked = names("unrecorded");
	return (
		<Paper withBorder={true} p="md" data-testid="model-lab-compare">
			<Stack gap="sm">
				<Alert color={differing ? "yellow" : "blue"} data-testid="model-lab-compare-alert">
					<Stack gap={4}>
						<Text size="sm" fw={600}>
							{differing
								? t(
										"pages.modelLab.savedCompare.differing",
										"These runs differ in: {{facts}}. A difference in their results may come from these facts, not from the experiment.",
										{ facts: differing },
									)
								: t("pages.modelLab.savedCompare.noneDiffering", "No recorded fact differs between these runs.")}
						</Text>
						{unchecked && (
							<Text size="sm" data-testid="model-lab-compare-unchecked">
								{t("pages.modelLab.savedCompare.uncheckedFacts", "Not recorded in every run, so not checked: {{facts}}.", {
									facts: unchecked,
								})}
							</Text>
						)}
						<Text size="sm">
							{t(
								"pages.modelLab.compare.notIdentical",
								"Identical settings do not guarantee identical results: a model file, runtime variant or background load can still change the output.",
							)}
						</Text>
						<Text size="sm">
							{t(
								"pages.modelLab.compare.notEvidence",
								"A difference alone is not evidence of a meaningful effect. Output also changes for reasons unrelated to what you are comparing.",
							)}
						</Text>
					</Stack>
				</Alert>
				<FactsTable runs={runs} facts={facts} />
				{runs[0]?.kind === "Steering" ? <SteeringComparison runs={runs} /> : <TraceComparison runs={runs} />}
			</Stack>
		</Paper>
	);
}
