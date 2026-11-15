import {
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
	UnstyledButton,
} from "@mantine/core";
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
import { NextTokenStepView } from "@/features/modelLab/components/NextTokenStepView";
import { SteeringExperimentResult } from "@/features/modelLab/components/SteeringExperimentResult";
import {
	activeJobPollMs,
	type CandidateBasis,
	sameCandidates,
	toStepEntry,
	toTraceTable,
} from "@/features/modelLab/models/ModelLabTrace";

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

// A saved experiment: what was asked, what came back, what the hooks measured, and the operator's note.
export function TraceExperimentView({ experimentId, isLive }: { experimentId: string; isLive: boolean }) {
	const { t } = useTranslation();
	const [selectedStep, setSelectedStep] = useState<number>();
	const [basis, setBasis] = useState<CandidateBasis>("raw");
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
	// A trace made before this version recorded no candidates: its rows are not selectable.
	const hasCandidates = tokens.some((token) => token.candidates);
	const selectedToken = selectedStep === undefined ? undefined : tokens[selectedStep];
	const selectedEntry = selectedToken && toStepEntry(selectedToken, basis);
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
				<Text size="sm" fw={600}>
					{t("pages.modelLab.trace.prompt", "Prompt")}
				</Text>
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
				{table && (
					<ScrollArea.Autosize mah={420} type="auto">
						<Table striped={true} stickyHeader={true} data-testid="model-lab-trace-table">
							<Table.Thead>
								<Table.Tr>
									<Table.Th>{t("pages.modelLab.trace.token", "Token")}</Table.Th>
									{table.layers.map((layer) => (
										<Table.Th key={layer.index} ta="right">
											{t("pages.modelLab.trace.layerNorm", "Layer {{index}} ({{kind}}) norm", {
												index: layer.index,
												kind: layer.kind,
											})}
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
				{table && result && !hasCandidates && (
					<Text size="sm" c="dimmed" data-testid="model-lab-trace-no-candidates">
						{t("pages.modelLab.trace.noCandidates", "This trace recorded no candidates (made before this version).")}
					</Text>
				)}
				{table && hasCandidates && !selectedEntry && (
					<Text size="sm" c="dimmed">
						{t("pages.modelLab.trace.pickStep", "Pick a token to see its next-token candidates.")}
					</Text>
				)}
				{selectedToken && selectedEntry && selectedStep !== undefined && (
					<Stack gap="xs" data-testid="model-lab-trace-candidates">
						<SegmentedControl
							value={basis}
							onChange={(value) => setBasis(value === "processed" ? "processed" : "raw")}
							aria-label={t("pages.modelLab.trace.basisLabel", "Candidates from")}
							data={[
								{ value: "raw", label: t("pages.modelLab.trace.basisRaw", "Raw logits") },
								{ value: "processed", label: t("pages.modelLab.trace.basisProcessed", "Processed scores") },
							]}
							data-testid="model-lab-trace-basis"
						/>
						{sameCandidates(selectedToken) && (
							<Text size="xs" c="dimmed" data-testid="model-lab-trace-same-candidates">
								{t(
									"pages.modelLab.trace.sameCandidates",
									"Raw and processed candidates are identical for this step: the generation config's processors changed nothing among these candidates.",
								)}
							</Text>
						)}
						<NextTokenStepView
							entry={selectedEntry}
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
					</Stack>
				)}
				<ExperimentNotes experimentId={experimentId} notes={data.notes ?? ""} running={data.status === "Running"} />
			</Stack>
		</Paper>
	);
}
