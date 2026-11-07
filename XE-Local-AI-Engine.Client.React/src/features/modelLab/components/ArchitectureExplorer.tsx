import { Alert, Loader, Select, Stack, Text } from "@mantine/core";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	getModelLabArchitectureOptions,
	getModelLabExperimentTraceOptions,
	listLocalModelsOptions,
	listModelLabExperimentsOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { LLAMACPP_PROVIDER } from "@/core/models/LocalModelProviders";
import { ArchitectureBlockDetail } from "@/features/modelLab/components/ArchitectureBlockDetail";
import { ArchitectureOverview } from "@/features/modelLab/components/ArchitectureOverview";
import { ModelPickerNotice } from "@/features/modelLab/components/ModelPickerNotice";
import {
	type ArchitectureSelection,
	formatBlockRanges,
	outsideGroups,
	promptExcerpt,
	summarizeTraceByBlock,
} from "@/features/modelLab/models/ModelLabArchitecture";

// One installed model's structure from its GGUF header, with an optional saved trace of that model laid over its blocks.
export function ArchitectureExplorer() {
	const { t } = useTranslation();
	const models = useQuery({ ...listLocalModelsOptions(), staleTime: 30_000 });
	const modelNames = (models.data?.items ?? []).flatMap((item) =>
		item.provider === LLAMACPP_PROVIDER && item.modelName ? [item.modelName] : [],
	);
	const [modelName, setModelName] = useState<string | null>(null);
	const [selection, setSelection] = useState<ArchitectureSelection | null>(null);
	const [traceId, setTraceId] = useState<string | null>(null);

	const descriptor = useQuery({
		...getModelLabArchitectureOptions({ path: { modelName: modelName ?? "" } }),
		enabled: modelName !== null,
	});
	const experiments = useQuery({ ...listModelLabExperimentsOptions(), enabled: modelName !== null });
	const traceOptions = (experiments.data?.experiments ?? []).flatMap((experiment) =>
		experiment.kind === "Trace" && experiment.status === "Succeeded" && experiment.installedModelName === modelName
			? [
					{
						value: experiment.id,
						label: `${promptExcerpt(experiment.prompt)} · ${experiment.repoId} · ${formatTimestamp(experiment.createdAtUtc)}`,
					},
				]
			: [],
	);
	const trace = useQuery({
		...getModelLabExperimentTraceOptions({ path: { experimentId: traceId ?? "" } }),
		enabled: traceId !== null,
	});
	const traceSummaries = trace.data ? summarizeTraceByBlock(trace.data.records) : undefined;

	const data = descriptor.data;
	const block = typeof selection === "number" ? data?.blocks.find((candidate) => candidate.index === selection) : undefined;
	const template =
		block && data?.support === "detailed" ? data.templates.find((candidate) => candidate.kind === block.kind) : undefined;

	return (
		<Stack gap="md">
			<ModelPickerNotice status={models.status} modelCount={modelNames.length} />
			<Select
				label={t("pages.modelLab.inspectModel", "Installed model")}
				placeholder={t("pages.directInference.modelPlaceholder", "Choose a model")}
				data={modelNames}
				value={modelName}
				onChange={(value) => {
					setModelName(value);
					setSelection(null);
					setTraceId(null);
				}}
				searchable={true}
			/>
			{modelName !== null && traceOptions.length > 0 && (
				<Select
					label={t("pages.modelLab.architecture.trace", "Trace to lay over the blocks")}
					description={t(
						"pages.modelLab.architecture.traceNote",
						"Measured on the Hugging Face checkpoint, joined by block index. The checkpoint was paired with this GGUF by shape only.",
					)}
					placeholder={t("pages.modelLab.architecture.noTraceChosen", "No trace")}
					data={traceOptions}
					value={traceId}
					onChange={setTraceId}
					clearable={true}
				/>
			)}
			{modelName !== null && experiments.isSuccess && traceOptions.length === 0 && (
				<Text size="sm" c="dimmed" data-testid="model-lab-architecture-no-traces">
					{t(
						"pages.modelLab.architecture.noTraces",
						"No finished trace of this model yet. A trace started under Trace and steer can be laid over its blocks here.",
					)}
				</Text>
			)}
			{traceSummaries && data && (
				<Text size="sm" data-testid="model-lab-architecture-traced-blocks">
					{t("pages.modelLab.architecture.tracedBlocks", "Traced blocks: {{blocks}} ({{traced}} of {{total}}).", {
						blocks: formatBlockRanges(traceSummaries.keys()) || "–",
						traced: traceSummaries.size,
						// The MTP block has no checkpoint layer, so it is not one a trace could cover.
						total: data.blocks.filter((candidate) => candidate.kind !== "mtp").length,
					})}
				</Text>
			)}
			{trace.isError && (
				<Alert color="red">
					{apiErrorMessage(trace.error, t("pages.modelLab.trace.traceFailed", "The trace could not be loaded."))}
				</Alert>
			)}
			{descriptor.isFetching && descriptor.isPending && <Loader size="sm" />}
			{descriptor.isError && (
				<Alert color="red">
					{apiErrorMessage(
						descriptor.error,
						t("pages.modelLab.architecture.loadFailed", "The model's structure could not be read."),
					)}
				</Alert>
			)}
			{data?.support === "unreadable" && (
				<Alert color="yellow" data-testid="model-lab-architecture-unreadable">
					{t(
						"pages.modelLab.architecture.unreadable",
						"The file could not be read ({{reason}}), so there is no structure to show.",
						{ reason: data.readFailure ?? t("pages.models.details.unknown", "Unknown") },
					)}
				</Alert>
			)}
			{data?.support === "overview" && (
				<Alert color="blue" data-testid="model-lab-architecture-overview-only">
					{t(
						"pages.modelLab.architecture.overviewOnly",
						"No operation graph is shown for the {{architecture}} architecture: only the tensor groups read from the file.",
						{ architecture: data.architecture ?? t("pages.models.details.unknown", "Unknown") },
					)}
				</Alert>
			)}
			{data && data.support !== "unreadable" && (
				<>
					<ArchitectureOverview
						blocks={data.blocks}
						hasOutputGroup={outsideGroups(data.nonBlock, "output").length > 0}
						hasOutputTensor={data.nonBlock.some((group) => group.component === "output")}
						selection={selection}
						tracedBlocks={traceSummaries ? new Set(traceSummaries.keys()) : null}
						onSelect={setSelection}
					/>
					{selection !== null && (
						<ArchitectureBlockDetail
							key={selection}
							modelName={data.modelName}
							suggestedBaseRepo={data.suggestedBaseRepo}
							block={block}
							outside={selection === "output" ? "output" : "embedding"}
							groups={block?.groups ?? outsideGroups(data.nonBlock, selection === "output" ? "output" : "embedding")}
							template={template}
							traceSummaries={traceSummaries}
						/>
					)}
				</>
			)}
		</Stack>
	);
}
