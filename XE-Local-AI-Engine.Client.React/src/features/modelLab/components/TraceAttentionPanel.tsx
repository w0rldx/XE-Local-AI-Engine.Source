import { MatrixChart, type MatrixChartCell } from "@mantine/charts";
import { Alert, Box, Group, Loader, ScrollArea, Select, Stack, Table, Text } from "@mantine/core";
import { useQuery } from "@tanstack/react-query";
import { type CSSProperties, type ReactNode, useId, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { getModelLabExperimentAttentionOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import type { XeLocalAiEngineClientServicesModelLabModelLabTraceResult as TraceResult } from "@/core/api/generated/types.gen";
import { logprobPercentLabel, logprobTextLabel } from "@/core/directInference/DirectInferenceModels";
import { BasisBadge, ExplanationLevelSwitch, ExplanationList } from "@/features/modelLab/components/ArchitectureExplanation";
import {
	type AttentionCell,
	type AttentionCellState,
	attentionMatrix,
	kvHeadOf,
	rowKeys,
} from "@/features/modelLab/models/ModelLabAttention";
import type { ExplanationLevel } from "@/features/modelLab/models/ModelLabExplanation";

const cellSize = 6;
const cellGap = 1;

// The weakest stored weight keeps this much of the primary color, so it never reads as an empty cell.
const minShadePercent = 20;

function weightFill(weight: number, max: number): string {
	const percent = Math.round(minShadePercent + ((100 - minShadePercent) * weight) / (max > 0 ? max : 1));
	return `color-mix(in srgb, var(--mantine-primary-color-filled) ${percent}%, transparent)`;
}

// Each empty state its own look, so a cell the run never measured cannot pass for a weight of 0.
const stateStyles: Record<
	Exclude<AttentionCellState, "weight" | "missing">,
	{ fill: string; stroke?: string; strokeDasharray?: string }
> = {
	floored: { fill: "transparent", stroke: "var(--mantine-primary-color-filled)" },
	notTopK: { fill: "var(--mantine-color-default-border)" },
	future: { fill: "transparent", stroke: "var(--mantine-color-default-border)", strokeDasharray: "2 1" },
};

// A weight as a percentage; 0 is a weight floored below 1/10,000, so it reads as "<0.01 %", not as no weight.
function weightLabel(weight: number): string {
	return logprobPercentLabel(Math.log(weight));
}

interface TraceAttentionPanelProps {
	experimentId: string;
	result: TraceResult;
	layer: number;
	// 0-based predicted token.
	step: number;
}

// One captured full-attention block of a saved run: the current step's row for a chosen head as a keyed list, and every
// step's rows as a decorative map. The rows are fetched for this block only, when it is selected.
export function TraceAttentionPanel({ experimentId, result, layer, step }: TraceAttentionPanelProps) {
	const { t } = useTranslation();
	const summary = result.attention;
	const heads = summary?.heads ?? 0;
	const kvHeads = summary?.kvHeads ?? 0;
	const topKeys = summary?.topKeys ?? 0;
	const [head, setHead] = useState(0);
	const [level, setLevel] = useState<ExplanationLevel>("overview");
	const [open, setOpen] = useState<string | null>(null);
	const attention = useQuery({
		...getModelLabExperimentAttentionOptions({ path: { experimentId, layer } }),
		// A saved run's rows never change.
		staleTime: Number.POSITIVE_INFINITY,
	});
	const steps = result.tokens?.length ?? 0;
	const matrix = useMemo(() => attention.data && attentionMatrix(attention.data, head, steps), [attention.data, head, steps]);
	const row = attention.data?.rows.find((item) => item.step === step);
	// The file's dropped count spans every captured block; this block's own gaps are the steps without a row.
	const missingRows = attention.data ? steps - new Set(attention.data.rows.map((item) => item.step)).size : 0;
	const stored = row?.heads[head];
	const keys = row && stored ? rowKeys(result, stored, row.queryPosition) : [];
	const explanations = [
		{ id: "attentionRow", label: t("pages.modelLab.trace.stepper.explain.attentionRow", "What this row shows") },
		{ id: "attentionMap", label: t("pages.modelLab.trace.stepper.explain.attentionMap", "Reading the map") },
		{ id: "topKeys", label: t("pages.modelLab.trace.stepper.explain.topKeys", "Top K and the rest") },
		{ id: "heads", label: t("pages.modelLab.trace.stepper.explain.heads", "Heads and key/value heads") },
	].map((item) => ({ ...item, candidates: [`stepper.${item.id}`] }));

	return (
		<Stack gap="xs" data-testid="model-lab-stepper-attention">
			<Group gap="xs">
				<Text size="sm" fw={600}>
					{t("pages.modelLab.trace.stepper.attention", "Attention at block {{index}}", { index: layer })}
				</Text>
				<BasisBadge basis="measurement" />
			</Group>
			<Text size="xs" c="dimmed" data-testid="model-lab-stepper-attention-note">
				{t(
					"pages.modelLab.trace.stepper.attentionNote",
					"Recomputed in fp32 from the kernel's own query/key tensors, top K kept, floored. This run kept the top {{topKeys}} keys per row and head; the model has {{heads}} query heads and {{kvHeads}} key/value heads.",
					{ topKeys, heads, kvHeads },
				)}
			</Text>
			<Select
				label={t("pages.modelLab.trace.stepper.head", "Head")}
				data={Array.from({ length: heads }, (_, index) => ({
					value: String(index),
					label: t("pages.modelLab.trace.stepper.headOption", "Head {{head}} (key/value head {{kvHead}})", {
						head: index,
						kvHead: kvHeadOf(index, heads, kvHeads),
					}),
				}))}
				value={String(Math.min(head, Math.max(heads - 1, 0)))}
				onChange={(value) => setHead(Number(value ?? 0))}
				allowDeselect={false}
				w="fit-content"
				data-testid="model-lab-stepper-head"
			/>
			{attention.status === "pending" && <Loader size="sm" />}
			{attention.status === "error" && (
				<Alert color="red" data-testid="model-lab-stepper-attention-failed">
					{attention.error instanceof ApiError && attention.error.statusCode === 404
						? t("pages.modelLab.trace.stepper.attentionNotFound", "This run kept no attention rows for block {{index}}.", {
								index: layer,
							})
						: apiErrorMessage(
								attention.error,
								t("pages.modelLab.trace.stepper.attentionFailed", "The attention rows could not be read."),
							)}
				</Alert>
			)}
			{attention.data?.truncated && (
				<Alert color="yellow" data-testid="model-lab-stepper-attention-truncated">
					{t(
						"pages.modelLab.trace.stepper.attentionTruncated",
						"The attention file reached its cap: {{count}} rows of this block were not kept, and the map marks them as missing.",
						{
							count: Math.max(missingRows, 0),
							// The route's bundle holds the same forms; these keep the plural right if it failed to load.
							// biome-ignore lint/style/useNamingConvention: i18next's own option name for a plural form's default.
							defaultValue_one:
								"The attention file reached its cap: {{count}} row of this block was not kept, and the map marks it as missing.",
							// biome-ignore lint/style/useNamingConvention: i18next's own option name for a plural form's default.
							defaultValue_zero: "The attention file reached its cap, but every row of this block was kept.",
						},
					)}
				</Alert>
			)}
			{attention.data &&
				(row && stored ? (
					<>
						<ScrollArea.Autosize mah={280} type="auto">
							<Table striped={true} data-testid="model-lab-stepper-row">
								<Table.Caption>
									{t(
										"pages.modelLab.trace.stepper.rowCaption",
										"Stored keys of head {{head}} at step {{step}}, in position order, each with its neighbours",
										{ head, step: step + 1 },
									)}
								</Table.Caption>
								<Table.Thead>
									<Table.Tr>
										{/* Weight first: on a phone the token context can be wider than the screen. */}
										<Table.Th ta="right">{t("pages.modelLab.trace.stepper.rowWeight", "Weight")}</Table.Th>
										<Table.Th>{t("pages.modelLab.trace.stepper.rowPosition", "Position")}</Table.Th>
										<Table.Th>{t("pages.modelLab.trace.stepper.rowToken", "Token in context")}</Table.Th>
									</Table.Tr>
								</Table.Thead>
								<Table.Tbody>
									{keys.map((key) => (
										<Table.Tr key={key.position} data-testid="model-lab-stepper-row-key">
											<Table.Td ta="right" style={{ whiteSpace: "nowrap" }}>
												{weightLabel(key.weight)}
											</Table.Td>
											<Table.Td ff="monospace">{key.position}</Table.Td>
											<Table.Td ff="monospace" style={{ whiteSpace: "pre" }}>
												{key.before !== undefined && (
													<Text span={true} c="dimmed" size="xs">{`${logprobTextLabel(key.before)} `}</Text>
												)}
												<Text span={true} fw={700}>
													{key.text === undefined ? `#${key.position}` : logprobTextLabel(key.text)}
												</Text>
												{key.after !== undefined && (
													<Text span={true} c="dimmed" size="xs">{` ${logprobTextLabel(key.after)}`}</Text>
												)}
											</Table.Td>
										</Table.Tr>
									))}
								</Table.Tbody>
							</Table>
						</ScrollArea.Autosize>
						<Text size="sm" data-testid="model-lab-stepper-row-rest">
							{t("pages.modelLab.trace.stepper.rowRest", "Rest of the row: {{rest}}", { rest: weightLabel(stored.rest) })}
						</Text>
					</>
				) : (
					<Text size="sm" c="dimmed" data-testid="model-lab-stepper-row-missing">
						{t("pages.modelLab.trace.stepper.rowMissing", "This step's row was not kept.")}
					</Text>
				))}
			{matrix && matrix.columns.length > 0 && <AttentionMap matrix={matrix} step={step} steps={steps} />}
			{matrix && matrix.droppedColumns > 0 && (
				<Text size="xs" c="dimmed" data-testid="model-lab-stepper-map-capped">
					{t(
						"pages.modelLab.trace.stepper.mapCapped",
						"The map shows {{shown}} of {{total}} key positions; the {{dropped}} with the least summed weight are left out.",
						{
							shown: matrix.columns.length,
							total: matrix.columns.length + matrix.droppedColumns,
							dropped: matrix.droppedColumns,
						},
					)}
				</Text>
			)}
			<ExplanationLevelSwitch value={level} onChange={setLevel} />
			<ExplanationList
				items={explanations}
				level={level}
				basis="measurement"
				open={open}
				onOpen={setOpen}
				data-testid="model-lab-stepper-attention-explain"
			/>
		</Stack>
	);
}

interface AttentionMapProps {
	matrix: NonNullable<ReturnType<typeof attentionMatrix>>;
	step: number;
	steps: number;
}

// Decorative: the row table above carries the same values for assistive technology.
function AttentionMap({ matrix, step, steps }: AttentionMapProps) {
	const { t } = useTranslation();
	// A row the cap cut is hatched in the text color: no fill of the weight scale can pass for it, in either theme.
	const hatchId = `attention-hatch-${useId().replace(/[^\w-]/g, "")}`;
	const missingFill = `url(#${hatchId})`;
	const pitch = cellSize + cellGap;
	const gridWidth = matrix.columns.length * pitch - cellGap;
	// A rect per step (up to 256) and key position: a step change only moves the outline, so the chart itself is built once per matrix.
	const chart = useMemo(
		() => (
			<MatrixChart
				data={matrix.cells}
				xLabels={matrix.columns.map(String)}
				yLabels={Array.from({ length: steps }, (_, index) => String(index))}
				domain={[0, matrix.max > 0 ? matrix.max : 1]}
				cellSize={cellSize}
				gap={cellGap}
				cellRadius={0}
				// Without it a null cell gets data-empty, whose stylesheet fill beats the per-state fill below.
				emptyColor={stateStyles.notTopK.fill}
				// The chart hands back the cell objects it was given, so the state rides along.
				getCellProps={(cell: MatrixChartCell) => {
					const { state, value } = cell as AttentionCell;
					if (state === "weight") {
						return { fill: weightFill(value ?? 0, matrix.max) };
					}
					return state === "missing" ? { fill: missingFill } : stateStyles[state];
				}}
			/>
		),
		[matrix, steps, missingFill],
	);
	const legend: { id: string; label: string; style?: CSSProperties; swatch?: ReactNode }[] = [
		{
			id: "weight",
			label: t("pages.modelLab.trace.stepper.legend.weight", "Weight, 0 to {{max}}", { max: weightLabel(matrix.max) }),
			style: { background: `linear-gradient(to right, ${weightFill(0, 1)}, ${weightFill(1, 1)})`, width: 32 },
		},
		{
			id: "floored",
			label: t("pages.modelLab.trace.stepper.legend.floored", "Stored, under 0.01 %"),
			style: { border: `1px solid ${stateStyles.floored.stroke}` },
		},
		{
			id: "notTopK",
			label: t("pages.modelLab.trace.stepper.legend.notTopK", "Not in the top K (part of the rest)"),
			style: { background: stateStyles.notTopK.fill },
		},
		{
			id: "future",
			label: t("pages.modelLab.trace.stepper.legend.future", "Later position (never read)"),
			style: { border: `1px dashed ${stateStyles.future.stroke}` },
		},
		{
			id: "missing",
			label: t("pages.modelLab.trace.stepper.legend.missing", "Row not kept"),
			swatch: (
				<svg width={12} height={12} aria-hidden={true} style={{ flexShrink: 0 }}>
					<rect width={12} height={12} fill={missingFill} data-testid="model-lab-stepper-legend-missing" />
				</svg>
			),
		},
	];
	return (
		<Stack gap={4}>
			<Text size="xs" c="dimmed">
				{t(
					"pages.modelLab.trace.stepper.mapCaption",
					"Map: one row per generated token, one column per stored key position; the current step's row is outlined.",
				)}
			</Text>
			<ScrollArea.Autosize mah={320} type="auto">
				<Box pos="relative" w="fit-content" aria-hidden={true} data-testid="model-lab-stepper-map">
					<svg width={0} height={0} style={{ position: "absolute" }}>
						<defs>
							<pattern id={hatchId} width={3} height={3} patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
								<line x1={0} y1={0} x2={0} y2={3} stroke="var(--mantine-color-text)" strokeWidth={1} />
							</pattern>
						</defs>
					</svg>
					{chart}
					<Box
						pos="absolute"
						// Pixels, not Mantine's scaled rem: it has to sit on the chart's own pixel grid.
						style={{
							left: -1,
							top: step * pitch - 1,
							width: gridWidth + 2,
							height: cellSize + 2,
							outline: "1px solid var(--mantine-color-text)",
							pointerEvents: "none",
						}}
						data-testid="model-lab-stepper-map-current"
					/>
				</Box>
			</ScrollArea.Autosize>
			<Group gap="md" data-testid="model-lab-stepper-map-legend">
				{legend.map((entry) => (
					<Group key={entry.id} gap={4} wrap="nowrap">
						{entry.swatch ?? <Box style={{ width: 12, height: 12, flexShrink: 0, ...entry.style }} />}
						<Text size="xs">{entry.label}</Text>
					</Group>
				))}
			</Group>
		</Stack>
	);
}
