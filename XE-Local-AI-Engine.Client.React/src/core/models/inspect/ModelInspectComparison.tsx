import { Accordion, Alert, Group, Loader, Select, SimpleGrid, Stack, Table, Text } from "@mantine/core";
import { useQuery } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import type {
	XeLocalAiEngineClientEndpointsLocalModelsV1ModelGgufInspectionResponse,
	XeLocalAiEngineClientEndpointsLocalModelsV1ModelGgufTensorDto,
} from "@/core/api/generated";
import { getModelGgufInspectionOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { humanizeBytes } from "@/core/formatting/BytesFormatting";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { ModelInspectMetadataItem } from "@/core/models/inspect/ModelInspectMetadataItem";
import { type ComparedGroup, compareInspections } from "@/core/models/inspect/GgufInspectComparison";
import { shortTensorName, tensorSizeLabel, tensorTypeLabel } from "@/core/models/inspect/GgufInspectInsights";
import { modelVariantLabel } from "@/core/models/ModelVariants";

type Inspection = XeLocalAiEngineClientEndpointsLocalModelsV1ModelGgufInspectionResponse;

function differenceLabel(t: TFunction, difference: number | null): string {
	if (difference === null) {
		return t("pages.models.details.unknown", "Unknown");
	}
	return `${difference > 0 ? "+" : difference < 0 ? "−" : ""}${humanizeBytes(Math.abs(difference))}`;
}

function sideType(t: TFunction, tensor: XeLocalAiEngineClientEndpointsLocalModelsV1ModelGgufTensorDto | null): string {
	return tensor === null
		? t("pages.models.inspect.compare.absent", "not in this file")
		: tensorTypeLabel(t, tensor.ggmlType, tensor.ggmlTypeId);
}

function ChangedTensors({ group }: { group: ComparedGroup }) {
	const { t } = useTranslation();
	return (
		<Table striped={true} withTableBorder={true} fz="xs">
			<Table.Thead>
				<Table.Tr>
					<Table.Th>{t("pages.models.inspect.columns.name", "Name")}</Table.Th>
					<Table.Th>{t("pages.models.inspect.columns.type", "Type")}</Table.Th>
					<Table.Th>{t("pages.models.inspect.columns.size", "Size")}</Table.Th>
				</Table.Tr>
			</Table.Thead>
			<Table.Tbody>
				{group.changed.map((row) => (
					<Table.Tr key={row.name}>
						<Table.Td ff="monospace">{shortTensorName(row.name, group.blockIndex ?? undefined)}</Table.Td>
						<Table.Td>{`${sideType(t, row.a)} → ${sideType(t, row.b)}`}</Table.Td>
						<Table.Td>
							{`${row.a ? tensorSizeLabel(t, row.a.byteSize) : "—"} → ${row.b ? tensorSizeLabel(t, row.b.byteSize) : "—"}`}
						</Table.Td>
					</Table.Tr>
				))}
			</Table.Tbody>
		</Table>
	);
}

function ComparisonResult({
	modelName,
	inspection,
	otherName,
}: {
	modelName: string;
	inspection: Inspection;
	otherName: string;
}) {
	const { t } = useTranslation();
	const query = useQuery(getModelGgufInspectionOptions({ path: { modelName: otherName } }));
	const other = query.data;
	const failed = inspection.readFailure
		? { model: modelName, reason: inspection.readFailure }
		: other?.readFailure
			? { model: otherName, reason: other.readFailure }
			: null;
	const comparison = useMemo(
		() => (other && !inspection.readFailure && !other.readFailure ? compareInspections(inspection, other) : null),
		[inspection, other],
	);

	if (query.isPending) {
		return <Loader size="sm" data-testid="model-inspect-compare-loading" />;
	}
	if (query.isError) {
		return (
			<InlineErrorAlert
				message={apiErrorMessage(query.error, t("pages.models.inspect.loadError", "Failed to read the model file."))}
			/>
		);
	}
	if (failed !== null) {
		return (
			<Alert color="yellow" data-testid="model-inspect-compare-failure">
				{t(
					"pages.models.inspect.compare.readFailure",
					"{{model}} could not be read completely ({{reason}}), so nothing is compared.",
					failed,
				)}
			</Alert>
		);
	}
	if (comparison === null) {
		return null;
	}

	const quantA = modelVariantLabel(modelName);
	const quantB = modelVariantLabel(otherName);
	return (
		<Stack gap="sm" data-testid="model-inspect-compare-result">
			{inspection.splitCount || other?.splitCount ? (
				<Alert color="blue">{t("pages.models.inspect.compare.split", "A split model is compared by its first file only.")}</Alert>
			) : null}
			<SimpleGrid cols={{ base: 2, sm: 4 }} spacing="sm">
				<ModelInspectMetadataItem label={quantA} value={tensorSizeLabel(t, comparison.bytesA)} />
				<ModelInspectMetadataItem label={quantB} value={tensorSizeLabel(t, comparison.bytesB)} />
				<ModelInspectMetadataItem
					label={t("pages.models.inspect.compare.difference", "Difference")}
					value={differenceLabel(t, comparison.difference)}
				/>
				<ModelInspectMetadataItem
					label={t("pages.models.inspect.compare.changedTypes", "Tensors that changed type")}
					value={comparison.changedTypeCount.toLocaleString()}
				/>
				{comparison.onlyInACount > 0 ? (
					<ModelInspectMetadataItem
						label={t("pages.models.inspect.compare.onlyIn", "Only in {{model}}", { model: quantA })}
						value={comparison.onlyInACount.toLocaleString()}
					/>
				) : null}
				{comparison.onlyInBCount > 0 ? (
					<ModelInspectMetadataItem
						label={t("pages.models.inspect.compare.onlyIn", "Only in {{model}}", { model: quantB })}
						value={comparison.onlyInBCount.toLocaleString()}
					/>
				) : null}
			</SimpleGrid>
			{/* Unmounted while closed, so a large file's changed tensors are not rendered until asked for. */}
			<Accordion multiple={true} variant="contained" chevronPosition="left" keepMounted={false}>
				{comparison.groups.map((group) => {
					const value = group.blockIndex === null ? "non-block" : `blk-${group.blockIndex}`;
					return (
						<Accordion.Item key={value} value={value} data-testid={`model-inspect-compare-${value}`}>
							<Accordion.Control disabled={group.changed.length === 0}>
								<Group justify="space-between" wrap="nowrap" pr="sm">
									<Text size="sm">
										{group.blockIndex === null
											? t("pages.models.inspect.nonBlock", "Outside blocks")
											: t("pages.models.inspect.compare.block", "Block {{index}}", { index: group.blockIndex })}
									</Text>
									<Text size="xs" c="dimmed">
										{t("pages.models.inspect.compare.groupSummary", "{{difference}} · {{count}} changed", {
											difference: differenceLabel(t, group.difference),
											count: group.changed.length,
										})}
									</Text>
								</Group>
							</Accordion.Control>
							<Accordion.Panel>
								<ChangedTensors group={group} />
							</Accordion.Panel>
						</Accordion.Item>
					);
				})}
			</Accordion>
		</Stack>
	);
}

interface ModelInspectComparisonProps {
	modelName: string;
	inspection: Inspection;
	candidates: string[];
}

// Compares this file's tensors with another installed variant of the same repo. The other file is read only once picked.
export function ModelInspectComparison({ modelName, inspection, candidates }: ModelInspectComparisonProps) {
	const { t } = useTranslation();
	const [otherName, setOtherName] = useState<string | null>(null);

	if (candidates.length === 0) {
		return null;
	}

	return (
		<Stack gap="sm" data-testid="model-inspect-compare">
			<Select
				label={t("pages.models.inspect.compare.label", "Compare with")}
				placeholder={t("pages.models.inspect.compare.placeholder", "Choose a variant")}
				data={candidates.map((name) => ({ value: name, label: modelVariantLabel(name) }))}
				value={otherName}
				onChange={setOtherName}
				clearable={true}
				w={260}
				data-testid="model-inspect-compare-select"
			/>
			{otherName ? (
				<ComparisonResult key={otherName} modelName={modelName} inspection={inspection} otherName={otherName} />
			) : null}
		</Stack>
	);
}
