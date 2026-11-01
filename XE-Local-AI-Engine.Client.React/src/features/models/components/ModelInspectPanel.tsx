import { Accordion, Alert, Group, Loader, SimpleGrid, Stack, Table, Text } from "@mantine/core";
import { useQuery } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import type { XeLocalAiEngineClientEndpointsLocalModelsV1ModelGgufTensorDto } from "@/core/api/generated";
import { getModelGgufInspectionOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { humanizeBytes } from "@/core/formatting/BytesFormatting";
import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";

type TensorDto = XeLocalAiEngineClientEndpointsLocalModelsV1ModelGgufTensorDto;

interface ModelInspectPanelProps {
	modelName: string;
}

function sizeLabel(t: TFunction, bytes: number | null | undefined): string {
	return bytes === null || bytes === undefined ? t("pages.models.details.unknown", "Unknown") : humanizeBytes(bytes);
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

// "blk.12.attn_q.weight" reads as "attn_q.weight" inside its own block row.
function shortName(name: string, blockIndex: number | undefined): string {
	const prefix = blockIndex === undefined ? "" : `blk.${blockIndex}.`;
	return prefix && name.startsWith(prefix) ? name.slice(prefix.length) : name;
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
						<Table.Td ff="monospace">{shortName(tensor.name, blockIndex)}</Table.Td>
						<Table.Td ff="monospace">{tensor.shape.join(" × ")}</Table.Td>
						<Table.Td>
							{tensor.ggmlType ?? t("pages.models.inspect.unknownType", "type {{id}}", { id: tensor.ggmlTypeId })}
						</Table.Td>
						<Table.Td>{sizeLabel(t, tensor.byteSize)}</Table.Td>
					</Table.Tr>
				))}
			</Table.Tbody>
		</Table>
	);
}

function MetadataItem({ label, value }: { label: string; value: string }) {
	return (
		<Stack gap={0}>
			<Text size="xs" c="dimmed">
				{label}
			</Text>
			<Text size="sm" fw={500}>
				{value}
			</Text>
		</Stack>
	);
}

// Read-only GGUF inspector: header metadata plus the tensor list grouped per transformer block. Self-contained like
// ModelLaunchArgumentsPanel, and mounted only while its tab is active, so the file is read on demand.
export function ModelInspectPanel({ modelName }: ModelInspectPanelProps) {
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
				<MetadataItem label={t("pages.models.inspect.architecture", "Architecture")} value={inspection.architecture ?? unknown} />
				<MetadataItem
					label={t("pages.models.inspect.parameters", "Parameters")}
					value={parameterLabel(t, inspection.parameterCount)}
				/>
				<MetadataItem label={t("pages.models.inspect.layers", "Layers")} value={numberLabel(t, inspection.blockCount)} />
				<MetadataItem label={t("pages.models.inspect.heads", "Attention heads (KV)")} value={heads} />
				<MetadataItem
					label={t("pages.models.inspect.embedding", "Embedding width")}
					value={numberLabel(t, inspection.embeddingLength)}
				/>
				<MetadataItem
					label={t("pages.models.inspect.context", "Context length")}
					value={numberLabel(t, inspection.contextLength)}
				/>
				<MetadataItem label={t("pages.models.inspect.quant", "Quantization")} value={inspection.fileType ?? unknown} />
				<MetadataItem label={t("pages.models.inspect.tensorCount", "Tensors")} value={numberLabel(t, inspection.tensorCount)} />
				<MetadataItem
					label={t("pages.models.inspect.tensorBytes", "Tensor data")}
					value={sizeLabel(t, inspection.totalTensorBytes)}
				/>
				<MetadataItem label={t("pages.models.inspect.headerBytes", "Header size")} value={sizeLabel(t, inspection.headerBytes)} />
			</SimpleGrid>

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
										size: sizeLabel(
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
								<Text size="sm">{t("pages.models.inspect.block", "Block {{index}}", { index: block.index })}</Text>
								<Text size="xs" c="dimmed">
									{t("pages.models.inspect.blockSummary", "{{count}} tensors · {{size}}", {
										count: block.tensors.length,
										size: sizeLabel(t, block.byteSize),
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
