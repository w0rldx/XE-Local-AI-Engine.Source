import { Alert, Group, Loader, Paper, Progress, ScrollArea, Stack, Table, Text, VisuallyHidden } from "@mantine/core";
import { useQuery } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { getModelLabExperimentLensOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import type {
	XeLocalAiEngineClientEndpointsModelLabV1ModelLabLensBlockResponse as LensBlock,
	XeLocalAiEngineClientServicesModelLabModelLabTraceResult as TraceResult,
} from "@/core/api/generated/types.gen";
import { logprobPercentLabel, logprobTextLabel } from "@/core/directInference/DirectInferenceModels";
import {
	BasisBadge,
	ExplanationEntry,
	ExplanationLevelSwitch,
	ExplanationList,
} from "@/features/modelLab/components/ArchitectureExplanation";
import { CandidateBar } from "@/features/modelLab/components/NextTokenStepView";
import { type ExplanationLevel, explanationKey, type lensTopics } from "@/features/modelLab/models/ModelLabExplanation";

// S0 measured the last block's lens bitwise equal to the raw output; one storage quantum (log-probabilities are floored
// to 1/10,000) is the most the two stored values may differ by.
const lastBlockTolerance = 1e-4;
// Both values are multiples of the quantum, so their float difference can land a hair above it.
const toleranceSlack = 1e-9;

const notRecorded = ["stepper.lensNotRecorded"];

// Null is a value that was not finite: probability 0.
function logprobOf(value: number | null | undefined): number {
	return value ?? Number.NEGATIVE_INFINITY;
}

function probabilityOf(logprob: number | null | undefined): number {
	return Math.exp(logprobOf(logprob));
}

interface TraceLensPanelProps {
	experimentId: string;
	result: TraceResult;
	// 0-based predicted token.
	step: number;
	// The block picked in the stepper's block strip; its row is highlighted and its readout shown in full.
	selectedBlock: number | undefined;
	layerKinds?: readonly string[];
}

// Every block's logit-lens readout at one step of a saved run: a derived analysis beside the run, never the model's own
// decision. The run's lens is fetched once, when the stepper first shows it.
export function TraceLensPanel({ experimentId, result, step, selectedBlock, layerKinds }: TraceLensPanelProps) {
	const { t, i18n } = useTranslation();
	const [level, setLevel] = useState<ExplanationLevel>("overview");
	const [open, setOpen] = useState<string | null>(null);
	const recorded = result.lens != null;
	const lens = useQuery({
		...getModelLabExperimentLensOptions({ path: { experimentId } }),
		// A saved run's lens never changes.
		staleTime: Number.POSITIVE_INFINITY,
		enabled: recorded,
	});
	const scroller = useRef<HTMLDivElement>(null);
	const loaded = lens.data !== undefined;
	// A newly picked block's row is brought into the table's own scroller, never by scrolling the page; a step change
	// that keeps the block leaves the scroll where the reader put it.
	useEffect(() => {
		const viewport = scroller.current;
		const row = viewport?.querySelector<HTMLElement>("[aria-current='true']");
		if (selectedBlock === undefined || !loaded || !viewport || !row) {
			return;
		}
		const view = viewport.getBoundingClientRect();
		const box = row.getBoundingClientRect();
		// The horizontal bar is an overlay on the scroller's bottom edge. While it shows, ScrollArea reserves its height
		// as the viewport's bottom padding, which only adds room at the scroll end; a row aligned to the box's bottom
		// would sit under the bar, so it is aligned to the top of that padding instead.
		const bottom = view.bottom - (Number.parseFloat(getComputedStyle(viewport).paddingBottom) || 0);
		if (box.top < view.top) {
			viewport.scrollTop += box.top - view.top;
		} else if (box.bottom > bottom) {
			viewport.scrollTop += box.bottom - bottom;
		}
	}, [selectedBlock, loaded]);
	const title = (
		<Text size="sm" fw={600}>
			{t("pages.modelLab.trace.stepper.lens.title", "What each block would predict (logit lens)")}
		</Text>
	);

	if (!recorded) {
		return (
			<Stack gap="xs" data-testid="model-lab-stepper-lens">
				<Group gap="xs">
					{title}
					<BasisBadge basis="unavailable" />
				</Group>
				<Text size="sm" c="dimmed" data-testid="model-lab-stepper-lens-unavailable">
					{t(
						"pages.modelLab.trace.stepper.lens.unavailable",
						"This run has no logit lens: it was saved before lens recording, or its checkpoint has more blocks than the lens limit of 64.",
					)}
				</Text>
				{explanationKey(i18n, notRecorded, level) !== null && (
					<>
						<ExplanationLevelSwitch value={level} onChange={setLevel} />
						<ExplanationEntry candidates={notRecorded} level={level} basis="background" />
					</>
				)}
			</Stack>
		);
	}

	const data = lens.data;
	const texts = new Map(data?.texts.map((entry) => [entry.id, entry.text]));
	const textOf = (id: number) => texts.get(id) ?? `#${id}`;
	const token = result.tokens?.[step];
	const chosenId = token?.id;
	const blocks = data?.steps.find((entry) => entry.step === step)?.blocks ?? [];
	const lastLayer = data?.layers.at(-1);
	const last = blocks.find((block) => block.layer === lastLayer);
	const picked = blocks.find((block) => block.layer === selectedBlock);
	const caption = t(
		"pages.modelLab.trace.stepper.lens.caption",
		"Each block's lens readout at step {{step}}; the chosen token is {{text}}",
		{ step: step + 1, text: logprobTextLabel(token?.text) },
	);
	const numbers = new Intl.NumberFormat(i18n.language, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
	const counts = new Intl.NumberFormat(i18n.language, { maximumSignificantDigits: 3 });
	const quantum = new Intl.NumberFormat(i18n.language, { maximumSignificantDigits: 2 });
	// Competition rank: rank 1 with another token listed first is a tie at the top, not a second place. Without the
	// chosen token's id the top token cannot be told apart from it, so no tie is claimed. A null rank sits beside a
	// chosen logprob that was not finite.
	const rankLabel = (block: LensBlock) =>
		block.chosenRank === null
			? t("pages.modelLab.trace.stepper.lens.notAvailable", "not available")
			: block.chosenRank === 1 && chosenId !== undefined && block.ids[0] !== chosenId
				? t("pages.modelLab.trace.stepper.lens.tied", "tied for first")
				: t("pages.modelLab.trace.stepper.lens.rank", "rank {{rank}}", { rank: block.chosenRank });
	// Every lens topic but the one for a run without a lens, which the unavailable state shows instead.
	const labels: Record<Exclude<(typeof lensTopics)[number], "lensNotRecorded">, string> = {
		lens: t("pages.modelLab.trace.stepper.lens.explain.lens", "What the logit lens shows"),
		lensChosen: t("pages.modelLab.trace.stepper.lens.explain.chosen", "The chosen token across the blocks"),
		lensEntropy: t("pages.modelLab.trace.stepper.lens.explain.entropy", "Spread in nats"),
		lensLastBlock: t("pages.modelLab.trace.stepper.lens.explain.lastBlock", "The last block and the raw output"),
		lensCaveats: t("pages.modelLab.trace.stepper.lens.explain.caveats", "What the lens cannot tell"),
	};
	const explanations = Object.entries(labels).map(([id, label]) => ({ id, label, candidates: [`stepper.${id}`] }));

	return (
		<Stack gap="xs" data-testid="model-lab-stepper-lens">
			<Group gap="xs">
				{title}
				<BasisBadge basis="derived" />
			</Group>
			<Text size="xs" c="dimmed" data-testid="model-lab-stepper-lens-note">
				{t(
					"pages.modelLab.trace.stepper.lens.note",
					"The model's final norm and output layer applied to each block's output at this step. A derived readout, not what the model computed: the output layer read only the last block's output.",
				)}
			</Text>
			{data?.tiedEmbeddings && (
				<Text size="xs" c="dimmed" data-testid="model-lab-stepper-lens-tied">
					{t(
						"pages.modelLab.trace.stepper.lens.tiedEmbeddings",
						"This checkpoint shares its output layer with the token embeddings, so the first blocks tend to read back the token at the current position.",
					)}
				</Text>
			)}
			{lens.status === "pending" && <Loader size="sm" />}
			{lens.status === "error" && (
				<Alert color="red" data-testid="model-lab-stepper-lens-failed">
					{lens.error instanceof ApiError && lens.error.statusCode === 404
						? t("pages.modelLab.trace.stepper.lens.notFound", "This run kept no logit lens.")
						: apiErrorMessage(lens.error, t("pages.modelLab.trace.stepper.lens.failed", "The logit lens could not be read."))}
				</Alert>
			)}
			{data?.truncated && (
				<Alert color="yellow" data-testid="model-lab-stepper-lens-truncated">
					{t(
						"pages.modelLab.trace.stepper.lens.truncated",
						"The lens reached its size limit: the first steps kept their readouts, later steps were not kept. The generated tokens are not affected.",
					)}
				</Alert>
			)}
			{data && blocks.length === 0 && (
				<Text size="sm" c="dimmed" data-testid="model-lab-stepper-lens-missing">
					{t("pages.modelLab.trace.stepper.lens.stepMissing", "This step's lens readout was not kept.")}
				</Text>
			)}
			{data && blocks.length > 0 && (
				<>
					<ScrollArea.Autosize
						mah={360}
						type="auto"
						viewportRef={scroller}
						// The browser makes a scrolling box a Tab stop; named, it is announced as the table's region.
						viewportProps={{ role: "region", "aria-label": caption, tabIndex: 0 }}
					>
						<Table striped={true} data-testid="model-lab-stepper-lens-table">
							<Table.Caption>{caption}</Table.Caption>
							<Table.Thead>
								<Table.Tr>
									<Table.Th>{t("pages.modelLab.trace.stepper.lens.block", "Block")}</Table.Th>
									<Table.Th>{t("pages.modelLab.trace.stepper.lens.top", "Lens top token")}</Table.Th>
									<Table.Th>{t("pages.modelLab.trace.stepper.lens.chosen", "Chosen token")}</Table.Th>
									<Table.Th ta="right">{t("pages.modelLab.trace.stepper.lens.spread", "Spread (nats)")}</Table.Th>
								</Table.Tr>
							</Table.Thead>
							<Table.Tbody>
								{blocks.map((block) => {
									const selected = block.layer === selectedBlock;
									const top = block.ids[0];
									return (
										<Table.Tr
											key={block.layer}
											// The ring, not only the fill, marks the selected block.
											style={
												selected
													? {
															background: "var(--mantine-primary-color-light)",
															boxShadow: "inset 3px 0 0 var(--mantine-primary-color-filled)",
														}
													: undefined
											}
											aria-current={selected ? "true" : undefined}
											data-testid="model-lab-stepper-lens-row"
										>
											{/* A row header, so a screen reader names the block in every cell of its row. */}
											<Table.Th scope="row" ff="monospace" fw={selected ? 700 : 400} style={{ whiteSpace: "nowrap" }}>
												{`${layerKinds?.[block.layer] ?? "?"} ${block.layer}`}
											</Table.Th>
											{/* Unbroken: a token and its percentage on one line; a wide token scrolls the region sideways. */}
											<Table.Td ff="monospace" style={{ whiteSpace: "pre" }}>
												{top === undefined ? (
													"–"
												) : (
													<>
														{`${logprobTextLabel(textOf(top))} `}
														{block.logprobs[0] == null ? <NotAvailable /> : logprobPercentLabel(block.logprobs[0])}
													</>
												)}
											</Table.Td>
											{/* Unbroken like its siblings: "12.34%, rank 5" on one line; a narrow screen scrolls sideways. */}
											<Table.Td miw={96} style={{ whiteSpace: "nowrap" }}>
												<Stack gap={2}>
													{/* The text below carries the value; the bar repeats it for the eye down the blocks. */}
													<Progress value={probabilityOf(block.chosenLogprob) * 100} size="sm" aria-hidden={true} />
													<Text size="xs">
														{block.chosenLogprob === null ? (
															<NotAvailable />
														) : (
															`${logprobPercentLabel(block.chosenLogprob)}, ${rankLabel(block)}`
														)}
													</Text>
												</Stack>
											</Table.Td>
											<Table.Td ta="right" style={{ whiteSpace: "nowrap" }}>
												{block.entropy === null ? <NotAvailable /> : numbers.format(block.entropy)}
											</Table.Td>
										</Table.Tr>
									);
								})}
							</Table.Tbody>
						</Table>
					</ScrollArea.Autosize>
					{picked && (
						<Paper withBorder={true} p="sm">
							<Stack gap="xs" data-testid="model-lab-stepper-lens-detail">
								<Text size="sm" fw={600}>
									{t("pages.modelLab.trace.stepper.lens.detailTitle", "Lens readout of block {{index}}", {
										index: picked.layer,
									})}
								</Text>
								{picked.logprobs.every((logprob) => logprob === null) ? (
									<Text size="sm" c="dimmed" data-testid="model-lab-stepper-lens-no-top">
										{t(
											"pages.modelLab.trace.stepper.lens.noTop",
											"This block's readout is not a finite number, so it has no top tokens to show.",
										)}
									</Text>
								) : (
									picked.ids.map((id, at) => {
										const logprob = picked.logprobs[at];
										// A bar would draw a value that was not finite as 0; the text says it is missing instead.
										return logprob == null ? (
											// biome-ignore lint/suspicious/noArrayIndexKey: the stored top N in rank order; ids are unique within it.
											<Text key={at} size="sm" ff="monospace" data-testid="model-lab-stepper-lens-top-missing">
												{`${logprobTextLabel(textOf(id))} `}
												<NotAvailable />
											</Text>
										) : (
											<CandidateBar
												// biome-ignore lint/suspicious/noArrayIndexKey: see above.
												key={at}
												text={textOf(id)}
												chosen={id === chosenId}
												probability={Math.exp(logprob)}
												color="cyan"
											/>
										);
									})
								)}
								<Text size="sm" data-testid="model-lab-stepper-lens-chosen">
									{picked.chosenLogprob === null
										? t(
												"pages.modelLab.trace.stepper.lens.chosenMissing",
												"Chosen token {{text}}: not available (not a finite number).",
												{ text: logprobTextLabel(token?.text) },
											)
										: t("pages.modelLab.trace.stepper.lens.chosenLine", "Chosen token {{text}}: {{rank}}, {{percent}}", {
												text: logprobTextLabel(token?.text),
												rank: rankLabel(picked),
												percent: logprobPercentLabel(picked.chosenLogprob),
											})}
								</Text>
								<Text size="sm" data-testid="model-lab-stepper-lens-spread">
									{picked.entropy === null
										? t(
												"pages.modelLab.trace.stepper.lens.spreadMissing",
												"Spread of this block's readout: not available (not a finite number).",
											)
										: t(
												"pages.modelLab.trace.stepper.lens.spreadLine",
												"Spread of this block's readout: {{entropy}} nats, about as spread as e^H ≈ {{tokens}} equally likely tokens (at most ln V = {{max}} nats for this vocabulary).",
												{
													entropy: numbers.format(picked.entropy),
													tokens: counts.format(Math.exp(picked.entropy)),
													max: numbers.format(Math.log(data.vocabSize)),
												},
											)}
								</Text>
							</Stack>
						</Paper>
					)}
					{last && (
						<LastBlock
							block={last}
							raw={token?.logprob}
							tolerance={quantum.format(lastBlockTolerance)}
							difference={(delta) => quantum.format(delta)}
						/>
					)}
				</>
			)}
			<ExplanationLevelSwitch value={level} onChange={setLevel} />
			<ExplanationList
				items={explanations}
				level={level}
				basis="derived"
				open={open}
				onOpen={setOpen}
				data-testid="model-lab-stepper-lens-explain"
			/>
		</Stack>
	);
}

function NotAvailable() {
	const { t } = useTranslation();
	return (
		<>
			<span aria-hidden={true}>–</span>
			<VisuallyHidden>{t("pages.modelLab.trace.stepper.lens.notAvailable", "not available")}</VisuallyHidden>
		</>
	);
}

interface LastBlockProps {
	block: LensBlock;
	// The step's raw output log-probability of the chosen token, before any generation-config processor.
	raw: number | null | undefined;
	tolerance: string;
	difference: (delta: number) => string;
}

// Both numbers are raw, so no processor can explain a gap between them: one above the tolerance means the lens did not
// reproduce this step's output.
function LastBlock({ block, raw, tolerance, difference }: LastBlockProps) {
	const { t } = useTranslation();
	const fromLens = block.chosenLogprob;
	const fromOutput = raw ?? null;
	const label = (logprob: number | null) =>
		logprob === null ? t("pages.modelLab.trace.stepper.lens.notAvailable", "not available") : logprobPercentLabel(logprob);
	// Null is a value that was not finite and has no difference to compare. The last block's lens is the raw output
	// itself, so a non-finite one is non-finite on both sides.
	const delta = fromLens === null || fromOutput === null ? 0 : Math.abs(fromLens - fromOutput);
	return (
		<Stack gap={4} data-testid="model-lab-stepper-lens-last">
			<Text size="sm">
				{t(
					"pages.modelLab.trace.stepper.lens.lastBlock",
					"Chosen token: {{lens}} from the last block's lens (block {{index}}), {{raw}} in the raw model output.",
					{ lens: label(fromLens), index: block.layer, raw: label(fromOutput) },
				)}
			</Text>
			<Text size="xs" c="dimmed" data-testid="model-lab-stepper-lens-tolerance">
				{t(
					"pages.modelLab.trace.stepper.lens.tolerance",
					"The view warns when their log-probabilities differ by more than {{tolerance}} nats, one step of the stored rounding.",
					{ tolerance },
				)}
			</Text>
			{delta > lastBlockTolerance + toleranceSlack && (
				<Alert color="yellow" data-testid="model-lab-stepper-lens-mismatch">
					{t(
						"pages.modelLab.trace.stepper.lens.mismatch",
						"Their log-probabilities differ by {{delta}} nats, more than the tolerance: the lens did not reproduce this step's raw output.",
						{ delta: difference(delta) },
					)}
				</Alert>
			)}
		</Stack>
	);
}
