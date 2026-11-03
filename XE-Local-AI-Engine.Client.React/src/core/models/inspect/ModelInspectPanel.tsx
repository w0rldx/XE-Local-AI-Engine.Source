import { Accordion, Alert, Box, Group, Loader, Progress, SimpleGrid, Stack, Table, Text, Tooltip } from "@mantine/core";
import { useQuery } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import type { XeLocalAiEngineClientEndpointsLocalModelsV1ModelGgufTensorDto } from "@/core/api/generated";
import { getModelGgufInspectionOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { humanizeBytes } from "@/core/formatting/BytesFormatting";
import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";

import { ModelInspectComparison } from "@/core/models/inspect/ModelInspectComparison";
import { ModelInspectMetadataItem } from "@/core/models/inspect/ModelInspectMetadataItem";
import {
	type BlockKind,
	blockKind,
	hasExperts,
	shortTensorName,
	tensorSizeLabel,
	tensorTypeLabel,
	tensorTypeRows,
} from "@/core/models/inspect/GgufInspectInsights";

type TensorDto = XeLocalAiEngineClientEndpointsLocalModelsV1ModelGgufTensorDto;

interface ModelInspectPanelProps {
	modelName: string;
	// Other installed variants of the same repo; none hides the comparison.
	compareCandidates: string[];
}

const blockKindColors: Record<BlockKind, string> = {
	linearAttention: "teal",
	fullAttention: "blue",
	mtp: "violet",
	other: "gray.6",
};

// The ring reads as a shape on any kind colour, in both colour schemes.
const expertMarker = "inset 0 0 0 2px var(--mantine-color-text)";

// Real files reach 14 types; past that the colours repeat, and the table's rows still name each type. Bare "gray" and
// "dark" shades sink into a dark background, so those two take fixed mid-tone shades that read in both schemes.
const tensorTypeColors = [
	"blue",
	"teal",
	"orange",
	"violet",
	"pink",
	"lime",
	"cyan",
	"gray.6",
	"red",
	"yellow",
	"green",
	"indigo",
	"grape",
	"orange.9",
];

function blockKindLabels(t: TFunction): Record<BlockKind, string> {
	return {
		linearAttention: t("pages.models.inspect.blockKinds.linearAttention", "Linear attention"),
		fullAttention: t("pages.models.inspect.blockKinds.fullAttention", "Full attention"),
		mtp: t("pages.models.inspect.blockKinds.mtp", "MTP"),
		other: t("pages.models.inspect.blockKinds.other", "Other"),
	};
}

function numberLabel(t: TFunction, value: number | null | undefined): string {
	return value === null || value === undefined ? t("pages.models.details.unknown", "Unknown") : value.toLocaleString();
}

// The exact count beside the same B/M scale the model list's parameter size uses: "26.9B (26,895,998,464)".
function parameterLabel(t: TFunction, count: number | null | undefined): string {
	if (count === null || count === undefined) {
		return t("pages.models.details.unknown", "Unknown");
	}
	const compact = count >= 1e9 ? `${(count / 1e9).toFixed(1)}B` : `${(count / 1e6).toFixed(0)}M`;
	return `${compact} (${count.toLocaleString()})`;
}

function TensorTable({ tensors, blockIndex }: { tensors: TensorDto[]; blockIndex?: number }) {
	const { t } = useTranslation();
	return (
		<Table striped={true} withTableBorder={true} fz="xs">
			<Table.Thead>
				<Table.Tr>
					<Table.Th>{t("pages.models.inspect.columns.name", "Name")}</Table.Th>
					<Table.Th>{t("pages.models.inspect.columns.shape", "Shape")}</Table.Th>
					<Table.Th>{t("pages.models.inspect.columns.type", "Type")}</Table.Th>
					<Table.Th>{t("pages.models.inspect.columns.size", "Size")}</Table.Th>
				</Table.Tr>
			</Table.Thead>
			<Table.Tbody>
				{tensors.map((tensor) => (
					<Table.Tr key={tensor.name}>
						<Table.Td ff="monospace">{shortTensorName(tensor.name, blockIndex)}</Table.Td>
						<Table.Td ff="monospace">{tensor.shape.join(" × ")}</Table.Td>
						<Table.Td>{tensorTypeLabel(t, tensor.ggmlType, tensor.ggmlTypeId)}</Table.Td>
						<Table.Td>{tensorSizeLabel(t, tensor.byteSize)}</Table.Td>
					</Table.Tr>
				))}
			</Table.Tbody>
		</Table>
	);
}

function blockLabel(t: TFunction, block: { index: number; tensors: TensorDto[] }): string {
	const kind = blockKindLabels(t)[blockKind(block.tensors)];
	return hasExperts(block.tensors)
		? t("pages.models.inspect.blockCellExpert", "Block {{index}}: {{kind}}, MoE expert", { index: block.index, kind })
		: t("pages.models.inspect.blockCell", "Block {{index}}: {{kind}}", { index: block.index, kind });
}

function BlockStrip({ blocks }: { blocks: { index: number; tensors: TensorDto[] }[] }) {
	const { t } = useTranslation();
	const labels = blockKindLabels(t);
	const cells = blocks.map((block) => ({
		index: block.index,
		kind: blockKind(block.tensors),
		expert: hasExperts(block.tensors),
		label: blockLabel(t, block),
	}));
	const kinds = (Object.keys(blockKindColors) as BlockKind[])
		.map((kind) => ({ kind, count: cells.filter((cell) => cell.kind === kind).length }))
		.filter(({ count }) => count > 0);
	const expertCount = cells.filter((cell) => cell.expert).length;
	return (
		<Stack gap="xs" data-testid="model-inspect-block-strip">
			<Text fw={600} size="sm">
				{t("pages.models.inspect.blockStrip", "Block types")}
			</Text>
			<Group gap={2}>
				{cells.map(({ index, kind, expert, label }) => (
					<Tooltip key={index} label={label} withArrow={true}>
						<Box
							w={12}
							h={12}
							bg={blockKindColors[kind]}
							role="img"
							aria-label={label}
							style={{ borderRadius: 2, boxShadow: expert ? expertMarker : undefined }}
						/>
					</Tooltip>
				))}
			</Group>
			<Group gap="md">
				{kinds.map(({ kind, count }) => (
					<Group key={kind} gap={6} wrap="nowrap" data-testid={`model-inspect-kind-${kind}`}>
						<Box w={8} h={8} bg={blockKindColors[kind]} style={{ borderRadius: 2 }} aria-hidden="true" />
						<Text size="xs">{labels[kind]}</Text>
						<Text size="xs" c="dimmed">
							{count}
						</Text>
					</Group>
				))}
				{expertCount > 0 ? (
					<Group gap={6} wrap="nowrap" data-testid="model-inspect-kind-moeExpert">
						<Box w={12} h={12} style={{ borderRadius: 2, boxShadow: expertMarker }} aria-hidden="true" />
						<Text size="xs">{t("pages.models.inspect.blockKinds.moeExpert", "MoE expert")}</Text>
						<Text size="xs" c="dimmed">
							{expertCount}
						</Text>
					</Group>
				) : null}
			</Group>
		</Stack>
	);
}

function TensorTypeBytes({ tensors }: { tensors: TensorDto[] }) {
	const { t } = useTranslation();
	const rows = tensorTypeRows(tensors);
	const unknown = t("pages.models.details.unknown", "Unknown");
	const colored = rows.map((row, position) => ({ ...row, color: tensorTypeColors[position % tensorTypeColors.length] }));
	const typeLabel = (row: (typeof rows)[number]) => tensorTypeLabel(t, row.ggmlType, row.ggmlTypeId);
	return (
		<Stack gap="xs" data-testid="model-inspect-type-bytes">
			<Text fw={600} size="sm">
				{t("pages.models.inspect.typeBytes", "Bytes per tensor type")}
			</Text>
			{colored.some((row) => row.bytes > 0) ? (
				<Progress.Root size="lg">
					{colored
						.filter((row) => row.bytes > 0)
						.map((row) => (
							<Progress.Section key={row.ggmlTypeId} value={row.percent} color={row.color} aria-label={typeLabel(row)} />
						))}
				</Progress.Root>
			) : null}
			<Table striped={true} withTableBorder={true} fz="xs">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.models.inspect.columns.type", "Type")}</Table.Th>
						<Table.Th>{t("pages.models.inspect.tensorCount", "Tensors")}</Table.Th>
						<Table.Th>{t("pages.models.inspect.columns.size", "Size")}</Table.Th>
						<Table.Th>{t("pages.models.inspect.columns.share", "Share")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{colored.map((row) => {
						const noneKnown = row.unknownSizeCount === row.tensorCount;
						return (
							<Table.Tr key={row.ggmlTypeId} data-testid={`model-inspect-type-${row.ggmlTypeId}`}>
								<Table.Td>
									<Group gap={6} wrap="nowrap">
										<Box w={8} h={8} bg={row.color} style={{ borderRadius: 2, flexShrink: 0 }} aria-hidden="true" />
										{typeLabel(row)}
									</Group>
								</Table.Td>
								<Table.Td>{row.tensorCount.toLocaleString()}</Table.Td>
								<Table.Td>
									{noneKnown || row.unknownSizeCount === 0
										? tensorSizeLabel(t, noneKnown ? null : row.bytes)
										: t("pages.models.inspect.partlyUnknownSize", "{{size}} + {{count}} of unknown size", {
												size: humanizeBytes(row.bytes),
												count: row.unknownSizeCount,
											})}
								</Table.Td>
								<Table.Td>{noneKnown ? unknown : `${row.percent.toFixed(1)}%`}</Table.Td>
							</Table.Tr>
						);
					})}
				</Table.Tbody>
			</Table>
		</Stack>
	);
}

// Read-only GGUF inspector: header metadata plus the tensor list grouped per transformer block. Self-contained like
// ModelLaunchArgumentsPanel, and mounted only while its tab is active, so the file is read on demand.
export function ModelInspectPanel({ modelName, compareCandidates }: ModelInspectPanelProps) {
	const { t } = useTranslation();
	const query = useQuery(getModelGgufInspectionOptions({ path: { modelName } }));

	if (query.isPending) {
		return <Loader size="sm" data-testid="model-inspect-loading" />;
	}
	if (query.isError) {
		return (
			<InlineErrorAlert
				message={apiErrorMessage(query.error, t("pages.models.inspect.loadError", "Failed to read the model file."))}
			/>
		);
	}

	const inspection = query.data;
	const unknown = t("pages.models.details.unknown", "Unknown");
	const heads =
		inspection.attentionHeadCount === null
			? unknown
			: `${inspection.attentionHeadCount} (${inspection.attentionHeadCountKv ?? unknown})`;
	const allTensors = [...inspection.nonBlockTensors, ...inspection.blocks.flatMap((block) => block.tensors)];
	const showInsights = !inspection.readFailure && allTensors.length > 0;

	return (
		<Stack gap="md" data-testid="model-inspect-panel">
			{inspection.readFailure ? (
				<Alert color="yellow">
					{t(
						"pages.models.inspect.readFailure",
						"The file could not be read completely ({{reason}}). Only the header metadata is shown.",
						{ reason: inspection.readFailure },
					)}
				</Alert>
			) : null}
			{inspection.splitCount ? (
				<Alert color="blue">
					{t(
						"pages.models.inspect.split",
						"This model is split into {{count}} files; only the first file's tensors are listed.",
						{ count: inspection.splitCount },
					)}
				</Alert>
			) : null}

			<Text fw={600} size="sm">
				{t("pages.models.inspect.metadata", "Metadata")}
			</Text>
			<SimpleGrid cols={{ base: 2, sm: 4 }} spacing="sm">
				<ModelInspectMetadataItem
					label={t("pages.models.inspect.architecture", "Architecture")}
					value={inspection.architecture ?? unknown}
				/>
				<ModelInspectMetadataItem
					label={t("pages.models.inspect.parameters", "Parameters")}
					value={parameterLabel(t, inspection.parameterCount)}
				/>
				<ModelInspectMetadataItem
					label={t("pages.models.inspect.layers", "Layers")}
					value={numberLabel(t, inspection.blockCount)}
				/>
				<ModelInspectMetadataItem label={t("pages.models.inspect.heads", "Attention heads (KV)")} value={heads} />
				<ModelInspectMetadataItem
					label={t("pages.models.inspect.embedding", "Embedding width")}
					value={numberLabel(t, inspection.embeddingLength)}
				/>
				<ModelInspectMetadataItem
					label={t("pages.models.inspect.context", "Context length")}
					value={numberLabel(t, inspection.contextLength)}
				/>
				<ModelInspectMetadataItem
					label={t("pages.models.inspect.quant", "Quantization")}
					value={inspection.fileType ?? unknown}
				/>
				<ModelInspectMetadataItem
					label={t("pages.models.inspect.tensorCount", "Tensors")}
					value={numberLabel(t, inspection.tensorCount)}
				/>
				<ModelInspectMetadataItem
					label={t("pages.models.inspect.tensorBytes", "Tensor data")}
					value={tensorSizeLabel(t, inspection.totalTensorBytes)}
				/>
				<ModelInspectMetadataItem
					label={t("pages.models.inspect.headerBytes", "Header size")}
					value={tensorSizeLabel(t, inspection.headerBytes)}
				/>
			</SimpleGrid>

			{showInsights && inspection.blocks.length > 0 ? <BlockStrip blocks={inspection.blocks} /> : null}
			{showInsights ? <TensorTypeBytes tensors={allTensors} /> : null}
			<ModelInspectComparison modelName={modelName} inspection={inspection} candidates={compareCandidates} />

			<Accordion multiple={true} variant="contained" chevronPosition="left">
				{inspection.chatTemplate ? (
					<Accordion.Item value="template">
						<Accordion.Control>{t("pages.models.inspect.chatTemplate", "Chat template")}</Accordion.Control>
						<Accordion.Panel>
							<CodeEditor
								value={inspection.chatTemplate}
								readOnly={true}
								wordWrap={true}
								height={280}
								aria-label={t("pages.models.inspect.chatTemplate", "Chat template")}
								data-testid="model-inspect-template"
							/>
						</Accordion.Panel>
					</Accordion.Item>
				) : null}
				{inspection.nonBlockTensors.length > 0 ? (
					<Accordion.Item value="non-block">
						<Accordion.Control>
							<Group justify="space-between" wrap="nowrap" pr="sm">
								<Text size="sm">{t("pages.models.inspect.nonBlock", "Outside blocks")}</Text>
								<Text size="xs" c="dimmed">
									{t("pages.models.inspect.blockSummary", "{{count}} tensors · {{size}}", {
										count: inspection.nonBlockTensors.length,
										size: tensorSizeLabel(
											t,
											inspection.nonBlockTensors.every((tensor) => tensor.byteSize !== null)
												? inspection.nonBlockTensors.reduce((sum, tensor) => sum + (tensor.byteSize ?? 0), 0)
												: null,
										),
									})}
								</Text>
							</Group>
						</Accordion.Control>
						<Accordion.Panel>
							<TensorTable tensors={inspection.nonBlockTensors} />
						</Accordion.Panel>
					</Accordion.Item>
				) : null}
				{inspection.blocks.map((block) => (
					<Accordion.Item key={block.index} value={`blk-${block.index}`} data-testid={`model-inspect-block-${block.index}`}>
						<Accordion.Control>
							<Group justify="space-between" wrap="nowrap" pr="sm">
								{/* The strip's cells stay out of the tab order: this row carries the same label for keyboard and touch. */}
								<Text size="sm">{blockLabel(t, block)}</Text>
								<Text size="xs" c="dimmed">
									{t("pages.models.inspect.blockSummary", "{{count}} tensors · {{size}}", {
										count: block.tensors.length,
										size: tensorSizeLabel(t, block.byteSize),
									})}
								</Text>
							</Group>
						</Accordion.Control>
						<Accordion.Panel>
							<TensorTable tensors={block.tensors} blockIndex={block.index} />
						</Accordion.Panel>
					</Accordion.Item>
				))}
			</Accordion>
		</Stack>
	);
}
