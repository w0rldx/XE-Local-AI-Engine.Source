import { Alert, Badge, Group, Loader, Paper, ScrollArea, Stack, Table, Text } from "@mantine/core";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { getModelLabExperimentOptions, getModelLabExperimentTraceOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { humanizeBytes } from "@/core/formatting/BytesFormatting";
import { activeJobPollMs, toTraceTable } from "@/features/modelLab/models/ModelLabTrace";

// A saved experiment, read-only: what was asked, what came back, and what the hooks measured.
export function TraceExperimentView({ experimentId, isLive }: { experimentId: string; isLive: boolean }) {
	const { t } = useTranslation();
	const path = { path: { experimentId } };
	const experiment = useQuery({
		...getModelLabExperimentOptions(path),
		refetchInterval: (query) => (query.state.data?.status === "Running" ? activeJobPollMs(isLive) : false),
	});
	const succeeded = experiment.data?.status === "Succeeded";
	const trace = useQuery({ ...getModelLabExperimentTraceOptions(path), enabled: succeeded });

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
	const table = trace.data ? toTraceTable(trace.data.records ?? [], result?.tokens ?? []) : undefined;
	return (
		<Paper withBorder={true} p="md" data-testid="model-lab-trace-view">
			<Stack gap="sm">
				<Group gap="xs">
					{succeeded && (
						<Badge color="blue" data-testid="model-lab-trace-measured">
							{t("pages.modelLab.trace.measured", "Measured")}
						</Badge>
					)}
					<Text size="sm">{`${data.repoId} @ ${data.revision?.slice(0, 12)}`}</Text>
				</Group>
				{succeeded && (
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
				{result && (
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
									<Table.Tr key={row.step}>
										<Table.Td style={{ fontFamily: "monospace", whiteSpace: "pre" }}>{row.token ?? `#${row.step}`}</Table.Td>
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
			</Stack>
		</Paper>
	);
}
