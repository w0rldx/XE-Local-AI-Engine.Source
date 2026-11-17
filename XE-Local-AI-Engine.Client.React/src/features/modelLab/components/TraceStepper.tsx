import { ActionIcon, Button, Group, ScrollArea, Slider, Stack, Text, UnstyledButton } from "@mantine/core";
import { IconChevronLeft, IconChevronRight, IconEye } from "@tabler/icons-react";
import type { TFunction } from "i18next";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientServicesModelLabModelLabTraceResult as TraceResult } from "@/core/api/generated/types.gen";
import { logprobTextLabel } from "@/core/directInference/DirectInferenceModels";
import { BasisBadge, ExplanationEntry, ExplanationLevelSwitch } from "@/features/modelLab/components/ArchitectureExplanation";
import { TraceAttentionPanel } from "@/features/modelLab/components/TraceAttentionPanel";
import { TraceStepCandidates } from "@/features/modelLab/components/TraceStepCandidates";
import { type ExplanationLevel, explanationKey } from "@/features/modelLab/models/ModelLabExplanation";
import { moveFocusAmongButtons } from "@/features/modelLab/models/ModelLabFocus";
import {
	type CandidateBasis,
	type StepperBlock,
	stepperBlocks,
	type TraceLayer,
	type TraceRow,
} from "@/features/modelLab/models/ModelLabTrace";

// Most of the cell stays the page's background, so its label keeps its contrast at the largest norm.
const maxShadePercent = 60;

interface TraceStepperProps {
	experimentId: string;
	result: TraceResult;
	table: { layers: TraceLayer[]; rows: TraceRow[] };
	// Every block's kind when the result names them; the traced blocks only otherwise.
	layerKinds?: readonly string[];
	// 0-based; shared with the table so a row picked there opens here.
	step: number;
	onStepChange: (step: number) => void;
	basis: CandidateBasis;
	onBasisChange: (basis: CandidateBasis) => void;
}

// One saved trace run replayed token by token: the step, its blocks, and the candidates behind its token. It reads only
// this run's checkpoint data, never Next token's GGUF results.
export function TraceStepper({
	experimentId,
	result,
	table,
	layerKinds,
	step,
	onStepChange,
	basis,
	onBasisChange,
}: TraceStepperProps) {
	const { t } = useTranslation();
	const [pickedBlock, setPickedBlock] = useState<number>();
	const tokens = result.tokens ?? [];
	const total = tokens.length;
	const current = Math.min(step, Math.max(total - 1, 0));
	// The strip's one Tab stop follows focus, so Tab leaves from where the arrows went; a new step brings it back.
	const [focusedToken, setFocusedToken] = useState(current);
	const [stepSeen, setStepSeen] = useState(current);
	if (current !== stepSeen) {
		setStepSeen(current);
		setFocusedToken(current);
	}
	const blocks = stepperBlocks(table, current, layerKinds);
	const peak = Math.max(0, ...table.rows.flatMap((row) => row.norms.filter((norm) => norm !== null)));
	const selected = blocks.find((block) => block.index === pickedBlock) ?? blocks[0];
	const token = tokens[current];
	const captured = result.attention?.layers ?? [];

	return (
		<Stack gap="sm" data-testid="model-lab-stepper">
			<Text size="xs" c="dimmed" data-testid="model-lab-stepper-provenance">
				{result.attnImplementation
					? t(
							"pages.modelLab.trace.stepper.provenanceKernel",
							"Hugging Face checkpoint, bf16, transformers {{transformers}}, attention kernel {{kernel}}; not the GGUF. Every value below is from this one saved run.",
							{ transformers: result.transformers ?? "?", kernel: result.attnImplementation },
						)
					: t(
							"pages.modelLab.trace.stepper.provenance",
							"Hugging Face checkpoint, bf16, transformers {{transformers}}; not the GGUF. Every value below is from this one saved run.",
							{ transformers: result.transformers ?? "?" },
						)}
			</Text>
			{total === 0 ? (
				<Text size="sm" c="dimmed">
					{t("pages.modelLab.trace.stepper.noTokens", "This run generated no tokens to step through.")}
				</Text>
			) : (
				<>
					<Group gap="xs" wrap="nowrap">
						<ActionIcon
							variant="default"
							disabled={current === 0}
							onClick={() => onStepChange(current - 1)}
							aria-label={t("pages.modelLab.trace.stepper.previous", "Previous step")}
							data-testid="model-lab-stepper-previous"
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
							onChange={(value) => onStepChange(value - 1)}
							thumbLabel={t("pages.modelLab.trace.stepper.slider", "Step")}
							data-testid="model-lab-stepper-slider"
						/>
						<ActionIcon
							variant="default"
							disabled={current === total - 1}
							onClick={() => onStepChange(current + 1)}
							aria-label={t("pages.modelLab.trace.stepper.next", "Next step")}
							data-testid="model-lab-stepper-next"
						>
							<IconChevronRight size={16} aria-hidden={true} />
						</ActionIcon>
					</Group>
					<Text size="sm" data-testid="model-lab-stepper-position">
						{t("pages.modelLab.trace.stepper.position", "Step {{step}} of {{total}}: {{text}}", {
							step: current + 1,
							total,
							text: logprobTextLabel(token?.text),
						})}
					</Text>
					<ScrollArea.Autosize mah={140} type="auto">
						{/* One Tab stop, on the current step; the arrow keys move between the others. */}
						<Group
							gap={4}
							role="group"
							aria-label={t("pages.modelLab.trace.stepper.tokens", "Generated tokens")}
							onKeyDown={moveFocusAmongButtons}
							data-testid="model-lab-stepper-tokens"
						>
							{tokens.map((item, index) => (
								<Button
									// biome-ignore lint/suspicious/noArrayIndexKey: a step is its position in the generation.
									key={index}
									size="compact-xs"
									variant={index === current ? "filled" : "default"}
									ff="monospace"
									styles={{ label: { whiteSpace: "pre" } }}
									aria-current={index === current ? "step" : undefined}
									tabIndex={index === focusedToken ? 0 : -1}
									onFocus={() => setFocusedToken(index)}
									aria-label={t("pages.modelLab.trace.stepButton", "Step {{step}}: {{text}}", {
										step: index + 1,
										text: logprobTextLabel(item.text),
									})}
									onClick={() => onStepChange(index)}
									data-testid="model-lab-stepper-token"
								>
									{logprobTextLabel(item.text)}
								</Button>
							))}
						</Group>
					</ScrollArea.Autosize>
				</>
			)}
			<Text size="sm" fw={600}>
				{t("pages.modelLab.trace.stepper.blocks", "Blocks at this step")}
			</Text>
			{blocks.length === 0 ? (
				<Text size="sm" c="dimmed">
					{t("pages.modelLab.trace.stepper.noBlocks", "This run recorded no blocks.")}
				</Text>
			) : (
				<>
					<Group gap={4} data-testid="model-lab-stepper-blocks">
						{blocks.map((block) => (
							<BlockCell
								key={block.index}
								block={block}
								peak={peak}
								recorded={captured.includes(block.index)}
								selected={block.index === selected?.index}
								onSelect={() => setPickedBlock(block.index)}
							/>
						))}
					</Group>
					<Text size="xs" c="dimmed">
						{t(
							"pages.modelLab.trace.stepper.shade",
							"F: full attention, L: linear attention. The shade is the block's residual norm at this step, relative to the largest in this run.",
						)}
					</Text>
					{captured.length > 0 && (
						<Group gap={4} wrap="nowrap">
							<IconEye size={14} aria-hidden={true} style={{ flexShrink: 0 }} />
							<Text size="xs" c="dimmed">
								{t("pages.modelLab.trace.stepper.recordedLegend", "The eye marks the blocks whose attention this run recorded.")}
							</Text>
						</Group>
					)}
				</>
			)}
			{selected && (
				<>
					<Text size="sm" data-testid="model-lab-stepper-selected">
						{blockLabel(t, selected, captured.includes(selected.index))}
					</Text>
					{selected.kind !== "L" && captured.includes(selected.index) ? (
						<TraceAttentionPanel
							key={selected.index}
							experimentId={experimentId}
							result={result}
							layer={selected.index}
							step={current}
						/>
					) : (
						<AttentionUnavailable block={selected} captured={captured} />
					)}
				</>
			)}
			{token && (
				<>
					<TraceStepCandidates token={token} step={current} basis={basis} onBasisChange={onBasisChange} />
					{!token.candidates && (
						<Text size="sm" c="dimmed">
							{t("pages.modelLab.trace.noCandidates", "This trace recorded no candidates (made before this version).")}
						</Text>
					)}
				</>
			)}
		</Stack>
	);
}

function blockLabel(t: TFunction, block: StepperBlock, recorded: boolean): string {
	const kind =
		block.kind === "F"
			? t("pages.modelLab.trace.stepper.kind.full", "full attention")
			: block.kind === "L"
				? t("pages.modelLab.trace.stepper.kind.linear", "linear attention")
				: t("pages.modelLab.trace.stepper.kind.unknown", "kind not named");
	const norm =
		block.norm === undefined
			? t("pages.modelLab.trace.stepper.notTraced", "not traced in this run")
			: block.norm === null
				? t("pages.modelLab.trace.stepper.noRecord", "no record at this step")
				: t("pages.modelLab.trace.stepper.norm", "residual norm {{norm}}", { norm: block.norm });
	const label = t("pages.modelLab.trace.stepper.block", "Block {{index}}, {{kind}}: {{norm}}", {
		index: block.index,
		kind,
		norm,
	});
	return recorded ? t("pages.modelLab.trace.stepper.recorded", "{{block}}, attention recorded", { block: label }) : label;
}

interface BlockCellProps {
	block: StepperBlock;
	peak: number;
	// This run stored the block's attention rows.
	recorded: boolean;
	selected: boolean;
	onSelect: () => void;
}

function BlockCell({ block, peak, recorded, selected, onSelect }: BlockCellProps) {
	const { t } = useTranslation();
	const shade = typeof block.norm === "number" && peak > 0 ? Math.round((maxShadePercent * block.norm) / peak) : 0;
	return (
		<UnstyledButton
			aria-pressed={selected}
			aria-label={blockLabel(t, block, recorded)}
			onClick={onSelect}
			ff="monospace"
			fz="xs"
			px={8}
			py={2}
			style={{
				borderRadius: "var(--mantine-radius-sm)",
				border: "1px solid var(--mantine-color-default-border)",
				// The selected cell carries a second ring, so the choice does not rest on color alone.
				boxShadow: selected ? "inset 0 0 0 2px var(--mantine-primary-color-filled)" : undefined,
				background: `color-mix(in srgb, var(--mantine-primary-color-filled) ${shade}%, transparent)`,
				// Dimmed only when the run holds nothing for the block: recorded attention counts even without a norm.
				opacity: block.norm === undefined && !recorded ? 0.6 : undefined,
			}}
			data-testid="model-lab-stepper-block"
		>
			<Group gap={2} wrap="nowrap">
				{`${block.kind} ${block.index}`}
				{recorded && <IconEye size={12} aria-hidden={true} data-testid="model-lab-stepper-block-recorded" />}
			</Group>
		</UnstyledButton>
	);
}

// A linear block never has a matrix; a full one this run did not capture has no rows. A run that recorded attention
// keeps it for at most eight full blocks, so it names the ones it has.
function AttentionUnavailable({ block, captured }: { block: StepperBlock; captured: readonly number[] }) {
	const { t, i18n } = useTranslation();
	const [level, setLevel] = useState<ExplanationLevel>("overview");
	const linear = block.kind === "L";
	// The stepper's own text first; the delta-rule operation's explanation until it exists.
	const candidates = linear ? ["stepper.linearNoMatrix", "op.delta"] : ["stepper.notRecorded"];
	return (
		<Stack gap="xs" data-testid="model-lab-stepper-attention">
			<Group gap="xs">
				<Text size="sm" fw={600}>
					{t("pages.modelLab.trace.stepper.attention", "Attention at block {{index}}", { index: block.index })}
				</Text>
				<BasisBadge basis="unavailable" />
			</Group>
			<Text size="sm" c="dimmed" data-testid="model-lab-stepper-attention-reason">
				{linear
					? t(
							"pages.modelLab.trace.stepper.linearNoMatrix",
							"A linear-attention block keeps one running state instead of weights over earlier positions, so it has no attention matrix to show.",
						)
					: captured.length > 0
						? t(
								"pages.modelLab.trace.stepper.notCaptured",
								"This run recorded attention for blocks {{layers}} only; this block was not among them.",
								{ layers: captured.join(", ") },
							)
						: t("pages.modelLab.trace.stepper.notRecorded", "This run did not record attention.")}
			</Text>
			{explanationKey(i18n, candidates, level) !== null && (
				<>
					<ExplanationLevelSwitch value={level} onChange={setLevel} />
					<ExplanationEntry candidates={candidates} level={level} basis="background" />
				</>
			)}
		</Stack>
	);
}
