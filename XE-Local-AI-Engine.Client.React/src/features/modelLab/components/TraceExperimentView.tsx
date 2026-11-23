import {
	ActionIcon,
	Alert,
	Badge,
	Button,
	Group,
	Loader,
	Paper,
	ScrollArea,
	SegmentedControl,
	Stack,
	Table,
	Text,
	Textarea,
	Tooltip,
	UnstyledButton,
} from "@mantine/core";
import { IconArrowRight } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	getModelLabExperimentOptions,
	getModelLabExperimentQueryKey,
	getModelLabExperimentTraceOptions,
	listModelLabExperimentsQueryKey,
	setModelLabExperimentNotesMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { logprobTextLabel } from "@/core/directInference/DirectInferenceModels";
import { humanizeBytes } from "@/core/formatting/BytesFormatting";
import { SteeringExperimentResult } from "@/features/modelLab/components/SteeringExperimentResult";
import { TraceStepCandidates } from "@/features/modelLab/components/TraceStepCandidates";
import { TraceStepper } from "@/features/modelLab/components/TraceStepper";
import type { ModelLabJump } from "@/features/modelLab/models/ModelLabNavigation";
import { activeJobPollMs, type CandidateBasis, toTraceTable } from "@/features/modelLab/models/ModelLabTrace";

// The server's own bound (SetModelLabExperimentNotesRequestValidator).
const maxNotesLength = 8000;

// The operator's one note on a run. Saving it replaces the stored note; an empty one clears it.
function ExperimentNotes({ experimentId, notes, running }: { experimentId: string; notes: string; running: boolean }) {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const [draft, setDraft] = useState(notes);
	// The server note the draft was last aligned with. A newer one replaces an untouched draft, never an edited one,
	// so a stale draft cannot be saved over it.
	const [seen, setSeen] = useState(notes);
	if (notes !== seen) {
		setSeen(notes);
		if (draft === seen) {
			setDraft(notes);
		}
	}
	const save = useMutation({
		...setModelLabExperimentNotesMutation(),
		onSuccess: () =>
			Promise.all([
				queryClient.invalidateQueries({ queryKey: getModelLabExperimentQueryKey({ path: { experimentId } }) }),
				queryClient.invalidateQueries({ queryKey: listModelLabExperimentsQueryKey() }),
			]),
	});
	// The server stores a blank note as none, so a blank draft is no change from none.
	const blank = draft.trim() === "";
	const unchanged = draft === notes || (blank && notes === "");
	const write = (value: string) => save.mutate({ path: { experimentId }, body: { notes: value.trim() === "" ? null : value } });
	return (
		<Stack gap={4}>
			<Textarea
				label={t("pages.modelLab.trace.notes.label", "Your note")}
				description={t("pages.modelLab.trace.notes.description", "Saved encrypted on this node with the run.")}
				value={draft}
				onChange={(event) => setDraft(event.currentTarget.value)}
				maxLength={maxNotesLength}
				autosize={true}
				minRows={2}
				maxRows={10}
				disabled={running}
				data-testid="model-lab-notes"
			/>
			<Group justify="space-between" gap="xs">
				<Text size="xs" c="dimmed" data-testid="model-lab-notes-count">
					{t("pages.modelLab.trace.notes.count", "{{count}} of {{max}} characters", {
						count: draft.length,
						max: maxNotesLength,
					})}
				</Text>
				<Group gap="xs">
					<Button
						size="xs"
						variant="subtle"
						disabled={running || save.isPending || (blank && notes === "")}
						onClick={() => {
							setDraft("");
							write("");
						}}
						data-testid="model-lab-notes-clear"
					>
						{t("pages.modelLab.trace.notes.clear", "Clear note")}
					</Button>
					<Button
						size="xs"
						disabled={running || unchanged}
						loading={save.isPending}
						onClick={() => write(draft)}
						data-testid="model-lab-notes-save"
					>
						{t("pages.modelLab.trace.notes.save", "Save note")}
					</Button>
				</Group>
			</Group>
			{save.isError && (
				<Alert color="red" data-testid="model-lab-notes-failed">
					{apiErrorMessage(save.error, t("pages.modelLab.trace.notes.saveFailed", "The note could not be saved."))}
				</Alert>
			)}
		</Stack>
	);
}

interface TraceExperimentViewProps {
	experimentId: string;
	isLive: boolean;
	onJump?: (to: ModelLabJump) => void;
}

// A saved experiment: what was asked, what came back, what the hooks measured, and the operator's note.
export function TraceExperimentView({ experimentId, isLive, onJump }: TraceExperimentViewProps) {
	const { t } = useTranslation();
	const [selectedStep, setSelectedStep] = useState<number>();
	const [basis, setBasis] = useState<CandidateBasis>("raw");
	const [view, setView] = useState<"table" | "stepper">("table");
	const path = { path: { experimentId } };
	const experiment = useQuery({
		...getModelLabExperimentOptions(path),
		refetchInterval: (query) => (query.state.data?.status === "Running" ? activeJobPollMs(isLive) : false),
	});
	// A steering row has no trace, and its result is not a generation.
	const isSteering = experiment.data?.kind === "Steering";
	const succeeded = experiment.data?.status === "Succeeded" && !isSteering;
	const trace = useQuery({ ...getModelLabExperimentTraceOptions(path), enabled: succeeded });
	// A label on what can be read: a row can say Succeeded over a blob that is gone or was tampered with.
	const measured = succeeded && trace.status === "success";

	if (experiment.status === "pending") {
		return <Loader size="sm" />;
	}
	if (experiment.status === "error") {
		return (
			<Alert color="red">
				{apiErrorMessage(experiment.error, t("pages.modelLab.trace.loadFailed", "The experiment could not be loaded."))}
			</Alert>
		);
	}

	const { data } = experiment;
	const result = data.result;
	const tokens = result?.tokens ?? [];
	const table = trace.data ? toTraceTable(trace.data.records ?? [], tokens) : undefined;
	// Set on every contract-7 result, even one whose steps all lost their candidates (0).
	const candidateSteps = result?.candidateSteps;
	// A trace made before candidates were recorded has none: its rows are not selectable. A contract-7 one always is.
	const hasCandidates = (candidateSteps !== null && candidateSteps !== undefined) || tokens.some((token) => token.candidates);
	// Set only when a long run dropped the candidates of its last steps.
	const keptCandidateSteps =
		candidateSteps !== null && candidateSteps !== undefined && candidateSteps < tokens.length ? candidateSteps : undefined;
	const selectedToken = selectedStep === undefined ? undefined : tokens[selectedStep];
	const showBlockLabel = (index: number): string =>
		t("pages.modelLab.trace.showInArchitecture", "Show block {{index}} in Architecture", { index });
	return (
		<Paper withBorder={true} p="md" data-testid="model-lab-trace-view">
			<Stack gap="sm">
				<Group gap="xs">
					{measured && (
						<Badge color="blue" data-testid="model-lab-trace-measured">
							{t("pages.modelLab.trace.measured", "Measured")}
						</Badge>
					)}
					<Text size="sm">{`${data.repoId} @ ${data.revision?.slice(0, 12)}`}</Text>
				</Group>
				{measured && (
					<Text size="sm" c="dimmed">
						{t(
							"pages.modelLab.trace.measuredNote",
							"Read by hooks from the full-precision checkpoint during this run, not from the GGUF. A norm is a measurement, not an explanation of what the model is doing.",
						)}
					</Text>
				)}
				<Group gap="xs" justify="space-between">
					<Text size="sm" fw={600}>
						{t("pages.modelLab.trace.prompt", "Prompt")}
					</Text>
					{onJump && !isSteering && (
						<Button
							size="compact-xs"
							variant="light"
							rightSection={<IconArrowRight size={14} aria-hidden={true} />}
							// The checkpoint rendered the prompt with thinking off, so Next token starts from the same switch.
							onClick={() =>
								onJump({ tab: "nextToken", modelName: data.installedModelName, prompt: data.prompt, disableThinking: true })
							}
							data-testid="model-lab-trace-try-next-token"
						>
							{t("pages.modelLab.trace.tryNextToken", "Try in Next token")}
						</Button>
					)}
				</Group>
				<Text size="sm" style={{ whiteSpace: "pre-wrap" }}>
					{data.prompt}
				</Text>
				{data.errorMessage && (
					<Alert
						color={data.status === "Cancelled" ? "gray" : "red"}
						style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}
					>
						{data.errorMessage}
					</Alert>
				)}
				{isSteering && <SteeringExperimentResult result={result} running={data.status === "Running"} />}
				{result && !isSteering && (
					<>
						<Text size="sm" fw={600}>
							{t("pages.modelLab.trace.output", "Output")}
						</Text>
						<Text size="sm" style={{ whiteSpace: "pre-wrap" }}>
							{result.text}
						</Text>
						<Text size="xs" c="dimmed" data-testid="model-lab-trace-facts">
							{t(
								"pages.modelLab.trace.facts",
								"{{tokens}} tokens in {{seconds}} s, peak {{vram}} on {{device}}. {{modelClass}}, torch {{torch}}, transformers {{transformers}}.",
								{
									tokens: result.tokens?.length ?? 0,
									seconds: result.seconds,
									vram: humanizeBytes(result.peakVramBytes ?? 0),
									device: result.device ?? "?",
									modelClass: result.modelClass ?? "?",
									torch: result.torch ?? "?",
									transformers: result.transformers ?? "?",
								},
							)}
						</Text>
					</>
				)}
				{trace.status === "error" && (
					<Alert color="red" data-testid="model-lab-trace-unreadable">
						{apiErrorMessage(trace.error, t("pages.modelLab.trace.traceFailed", "The trace could not be loaded."))}
					</Alert>
				)}
				{trace.data?.truncated && (
					<Alert color="yellow" data-testid="model-lab-trace-truncated">
						{t(
							"pages.modelLab.trace.truncated",
							"The trace reached its {{cap}} cap: {{dropped}} records were not kept. Generation was not affected.",
							{
								cap: humanizeBytes(trace.data.byteCap ?? 0),
								dropped: trace.data.droppedRecords,
							},
						)}
					</Alert>
				)}
				{keptCandidateSteps !== undefined && (
					<Alert color="yellow" data-testid="model-lab-trace-candidates-partial">
						{t(
							"pages.modelLab.trace.candidatesPartial",
							"Candidates were kept for the first {{kept}} of {{total}} steps, so the saved result stays within its size limit. Every later step keeps its token, its text and its probability.",
							{ kept: keptCandidateSteps, total: tokens.length },
						)}
					</Alert>
				)}
				{table && (
					<SegmentedControl
						value={view}
						onChange={(value) => setView(value === "stepper" ? "stepper" : "table")}
						aria-label={t("pages.modelLab.trace.view.label", "Show the run as")}
						data={[
							{ value: "table", label: t("pages.modelLab.trace.view.table", "Table") },
							{ value: "stepper", label: t("pages.modelLab.trace.view.stepper", "Step through") },
						]}
						data-testid="model-lab-trace-view-switch"
					/>
				)}
				{view === "table" && table && (
					<ScrollArea.Autosize mah={420} type="auto">
						<Table striped={true} stickyHeader={true} data-testid="model-lab-trace-table">
							<Table.Thead>
								<Table.Tr>
									<Table.Th>{t("pages.modelLab.trace.token", "Token")}</Table.Th>
									{table.layers.map((layer) => (
										<Table.Th key={layer.index} ta="right">
											<Group gap={4} justify="flex-end" wrap="nowrap">
												{t("pages.modelLab.trace.layerNorm", "Layer {{index}} ({{kind}}) norm", {
													index: layer.index,
													kind: layer.kind,
												})}
												{onJump && (
													<Tooltip label={showBlockLabel(layer.index)}>
														<ActionIcon
															size="sm"
															variant="subtle"
															aria-label={showBlockLabel(layer.index)}
															onClick={() =>
																onJump({
																	tab: "architecture",
																	modelName: data.installedModelName,
																	traceId: experimentId,
																	blockIndex: layer.index,
																})
															}
															data-testid="model-lab-trace-show-block"
														>
															<IconArrowRight size={14} />
														</ActionIcon>
													</Tooltip>
												)}
											</Group>
										</Table.Th>
									))}
								</Table.Tr>
							</Table.Thead>
							<Table.Tbody>
								{table.rows.map((row) => (
									<Table.Tr key={row.step} bg={row.step === selectedStep ? "var(--mantine-primary-color-light)" : undefined}>
										<Table.Td style={{ fontFamily: "monospace", whiteSpace: "pre" }}>
											{hasCandidates ? (
												<UnstyledButton
													ff="monospace"
													style={{ whiteSpace: "pre" }}
													aria-pressed={row.step === selectedStep}
													aria-label={t("pages.modelLab.trace.stepButton", "Step {{step}}: {{text}}", {
														step: row.step + 1,
														text: logprobTextLabel(row.token),
													})}
													onClick={() => setSelectedStep(row.step)}
													data-testid="model-lab-trace-step"
												>
													{/* Quoted with its whitespace escaped, as the step view shows it: a "\n" token is not blank. */}
													{logprobTextLabel(row.token)}
												</UnstyledButton>
											) : (
												(row.token ?? `#${row.step}`)
											)}
										</Table.Td>
										{row.norms.map((norm, index) => (
											<Table.Td key={table.layers[index]?.index} ta="right">
												{norm ?? "–"}
											</Table.Td>
										))}
									</Table.Tr>
								))}
							</Table.Tbody>
						</Table>
					</ScrollArea.Autosize>
				)}
				{view === "table" && table && result && !hasCandidates && (
					<Text size="sm" c="dimmed" data-testid="model-lab-trace-no-candidates">
						{t("pages.modelLab.trace.noCandidates", "This trace recorded no candidates (made before this version).")}
					</Text>
				)}
				{view === "table" && table && hasCandidates && !selectedToken && (
					<Text size="sm" c="dimmed">
						{t("pages.modelLab.trace.pickStep", "Pick a token to see its next-token candidates.")}
					</Text>
				)}
				{view === "table" && selectedToken && selectedStep !== undefined && (
					<TraceStepCandidates
						token={selectedToken}
						step={selectedStep}
						candidateSteps={candidateSteps}
						basis={basis}
						onBasisChange={setBasis}
					/>
				)}
				{view === "stepper" && table && result && (
					<TraceStepper
						experimentId={experimentId}
						result={result}
						table={table}
						layerKinds={result.layerKinds ? [...result.layerKinds] : undefined}
						step={selectedStep ?? 0}
						onStepChange={setSelectedStep}
						basis={basis}
						onBasisChange={setBasis}
						modelName={data.installedModelName}
						onJump={onJump}
					/>
				)}
				<ExperimentNotes experimentId={experimentId} notes={data.notes ?? ""} running={data.status === "Running"} />
			</Stack>
		</Paper>
	);
}
