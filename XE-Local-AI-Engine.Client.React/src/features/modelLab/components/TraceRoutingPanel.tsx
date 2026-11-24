import { Alert, Badge, Button, Group, Loader, Paper, Progress, Stack, Table, Text } from "@mantine/core";
import { IconArrowRight } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { getModelLabExperimentRoutingOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import type { XeLocalAiEngineClientServicesModelLabModelLabTraceResult as TraceResult } from "@/core/api/generated/types.gen";
import {
	BasisBadge,
	ExplanationEntry,
	ExplanationLevelSwitch,
	ExplanationList,
} from "@/features/modelLab/components/ArchitectureExplanation";
import { type ExplanationLevel, explanationKey, type routingTopics } from "@/features/modelLab/models/ModelLabExplanation";
import type { ModelLabJump } from "@/features/modelLab/models/ModelLabNavigation";
import { expertChoices, expertUsage } from "@/features/modelLab/models/ModelLabRouting";
import {
	routingProvenanceNote,
	routingUsageCount,
	routingUsageTitle,
} from "@/features/modelLab/models/ModelLabRoutingProvenance";

const notRecorded = ["stepper.routingNotRecorded"];

interface TraceRoutingPanelProps {
	experimentId: string;
	result: TraceResult;
	// 0-based predicted token.
	step: number;
	// The block picked in the stepper's block strip; its row is highlighted and its weights and usage shown in full.
	selectedBlock: number | undefined;
	layerKinds?: readonly string[];
	// The run's GGUF, which the Architecture tab opens with this run laid over it.
	modelName: string;
	onJump?: (to: ModelLabJump) => void;
}

// The experts each mixture-of-experts block chose at one step of a saved run, as its router measured them, and how often
// the selected block chose each over the run. The run's routing is fetched once, when the stepper first shows it.
export function TraceRoutingPanel({
	experimentId,
	result,
	step,
	selectedBlock,
	layerKinds,
	modelName,
	onJump,
}: TraceRoutingPanelProps) {
	const { t, i18n } = useTranslation();
	const [level, setLevel] = useState<ExplanationLevel>("overview");
	const [open, setOpen] = useState<string | null>(null);
	const recorded = result.routing != null;
	const routing = useQuery({
		...getModelLabExperimentRoutingOptions({ path: { experimentId } }),
		// A saved run's routing never changes.
		staleTime: Number.POSITIVE_INFINITY,
		enabled: recorded,
	});
	// Display rounding only: the response carries each weight exactly as the expert received it.
	const numbers = useMemo(
		() => new Intl.NumberFormat(i18n.language, { minimumFractionDigits: 2, maximumFractionDigits: 2 }),
		[i18n.language],
	);
	const title = (
		<Text size="sm" fw={600}>
			{t("pages.modelLab.trace.stepper.routing.title", "Routing used to predict this token")}
		</Text>
	);

	if (!recorded) {
		return (
			<Stack gap="xs" data-testid="model-lab-stepper-routing">
				<Group gap="xs">
					{title}
					<BasisBadge basis="unavailable" />
				</Group>
				<Text size="sm" c="dimmed" data-testid="model-lab-stepper-routing-unavailable">
					{t(
						"pages.modelLab.trace.stepper.routing.unavailable",
						"This run has no routing: it was saved before routing recording, it is a steering run, its checkpoint has no mixture-of-experts router, or its routing is past what a trace records (more than 64 blocks, more than 1024 experts or more than 16 experts per token).",
					)}
				</Text>
				{explanationKey(i18n, notRecorded, level) !== null && (
					<>
						<ExplanationLevelSwitch value={level} onChange={setLevel} />
						<ExplanationEntry candidates={notRecorded} level={level} basis="unavailable" />
					</>
				)}
			</Stack>
		);
	}

	const data = routing.data;
	const blocks = data?.steps.find((entry) => entry.step === step)?.blocks ?? [];
	const picked = blocks.find((block) => block.layer === selectedBlock);
	const usage =
		data && selectedBlock !== undefined && data.layers.includes(selectedBlock) ? expertUsage(data.steps, selectedBlock) : [];
	const chipLabel = (expert: number, weight: number) =>
		t("pages.modelLab.trace.stepper.routing.chip", "E{{expert}} {{weight}}", { expert, weight: numbers.format(weight) });
	const caption = t(
		"pages.modelLab.trace.stepper.routing.caption",
		"Experts each mixture-of-experts block chose at step {{step}}, with their weights",
		{ step: step + 1 },
	);
	// Every routing topic but the one for a run without routing, which the unavailable state shows instead.
	const labels: Record<Exclude<(typeof routingTopics)[number], "routingNotRecorded">, string> = {
		routing: t("pages.modelLab.trace.stepper.routing.explain.routing", "What the routing shows"),
		routingWeights: t("pages.modelLab.trace.stepper.routing.explain.weights", "The router weights"),
		routingUsage: t("pages.modelLab.trace.stepper.routing.explain.usage", "Counting picks over the run"),
		routingCaveats: t("pages.modelLab.trace.stepper.routing.explain.caveats", "What routing cannot tell"),
	};
	const explanations = Object.entries(labels).map(([id, label]) => ({ id, label, candidates: [`stepper.${id}`] }));

	return (
		<Stack gap="xs" data-testid="model-lab-stepper-routing">
			<Group gap="xs">
				{title}
				{/* Measured only once the routing was read: no claim while it loads or after it failed. */}
				{data && <BasisBadge basis="measuredTrace" />}
			</Group>
			<Text size="xs" c="dimmed" data-testid="model-lab-stepper-routing-note">
				{routingProvenanceNote(t)}
			</Text>
			{routing.status === "pending" && <Loader size="sm" />}
			{routing.status === "error" && (
				<Alert color="red" data-testid="model-lab-stepper-routing-failed">
					{routing.error instanceof ApiError && routing.error.statusCode === 404
						? t("pages.modelLab.trace.stepper.routing.notFound", "This run kept no routing.")
						: apiErrorMessage(routing.error, t("pages.modelLab.trace.stepper.routing.failed", "The routing could not be read."))}
				</Alert>
			)}
			{data?.truncated && (
				<Alert color="yellow" data-testid="model-lab-stepper-routing-truncated">
					{t(
						"pages.modelLab.trace.stepper.routing.truncated",
						"The routing reached its size limit: the first steps kept their rows, later steps were not kept. The generated tokens are not affected.",
					)}
				</Alert>
			)}
			{data && blocks.length === 0 && (
				<Text size="sm" c="dimmed" data-testid="model-lab-stepper-routing-missing">
					{t("pages.modelLab.trace.stepper.routing.stepMissing", "This step's routing was not kept.")}
				</Text>
			)}
			{data && blocks.length > 0 && (
				// Not a scroller: chips wrap on a narrow screen, so the table needs no Tab stop of its own.
				<Table striped={true} data-testid="model-lab-stepper-routing-table">
					<Table.Caption>{caption}</Table.Caption>
					<Table.Thead>
						<Table.Tr>
							<Table.Th>{t("pages.modelLab.trace.stepper.routing.block", "Block")}</Table.Th>
							<Table.Th>{t("pages.modelLab.trace.stepper.routing.experts", "Chosen experts and weights")}</Table.Th>
							{/* Renormalized weights always add up to 1, so their sum would say nothing. */}
							{!data.normalizedTopK && (
								<Table.Th ta="right">{t("pages.modelLab.trace.stepper.routing.share", "Sum of weights")}</Table.Th>
							)}
						</Table.Tr>
					</Table.Thead>
					<Table.Tbody>
						{blocks.map((block) => {
							const selected = block.layer === selectedBlock;
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
									data-testid="model-lab-stepper-routing-row"
								>
									{/* A row header, so a screen reader names the block in every cell of its row. */}
									<Table.Th scope="row" ff="monospace" fw={selected ? 700 : 400} style={{ whiteSpace: "nowrap" }}>
										{`${layerKinds?.[block.layer] ?? "?"} ${block.layer}`}
									</Table.Th>
									<Table.Td>
										<Group gap={4}>
											{expertChoices(block).map((choice) => (
												<Badge
													key={choice.expert}
													variant="light"
													ff="monospace"
													tt="none"
													// Never ellipsized: a squeezed column would otherwise cut the weight off.
													styles={{ label: { overflow: "visible" } }}
													data-testid="model-lab-stepper-routing-chip"
												>
													{chipLabel(choice.expert, choice.weight)}
												</Badge>
											))}
										</Group>
									</Table.Td>
									{!data.normalizedTopK && (
										<Table.Td ta="right" style={{ whiteSpace: "nowrap" }}>
											{numbers.format(block.weights.reduce((sum, weight) => sum + weight, 0))}
										</Table.Td>
									)}
								</Table.Tr>
							);
						})}
					</Table.Tbody>
				</Table>
			)}
			{picked && (
				<Paper withBorder={true} p="sm">
					<Stack gap="xs" data-testid="model-lab-stepper-routing-detail">
						<Text size="sm" fw={600}>
							{t("pages.modelLab.trace.stepper.routing.detailTitle", "Router weights of block {{index}}", {
								index: picked.layer,
							})}
						</Text>
						{expertChoices(picked).map((choice) => (
							<Stack key={choice.expert} gap={2} data-testid="model-lab-stepper-routing-bar">
								<Text size="sm" ff="monospace">
									{chipLabel(choice.expert, choice.weight)}
								</Text>
								{/* The text above carries the value; the bar repeats it for the eye. */}
								<Progress value={choice.weight * 100} size="sm" aria-hidden={true} />
							</Stack>
						))}
					</Stack>
				</Paper>
			)}
			{data && usage.length > 0 && (
				<Stack gap={4} data-testid="model-lab-stepper-routing-usage">
					<Group gap="xs">
						<Text size="sm" fw={600}>
							{routingUsageTitle(t, data)}
						</Text>
						<BasisBadge basis="derived" />
					</Group>
					{usage.slice(0, data.expertsPerToken).map((entry) => (
						<Text key={entry.expert} size="sm" ff="monospace" data-testid="model-lab-stepper-routing-usage-row">
							{routingUsageCount(t, entry, data)}
						</Text>
					))}
				</Stack>
			)}
			{/* Only a block the run routed has routing for Architecture to show. */}
			{onJump && selectedBlock !== undefined && data?.layers.includes(selectedBlock) && (
				<Group>
					<Button
						size="compact-sm"
						variant="light"
						rightSection={<IconArrowRight size={14} aria-hidden={true} />}
						onClick={() => onJump({ tab: "architecture", modelName, traceId: experimentId, blockIndex: selectedBlock })}
						data-testid="model-lab-stepper-routing-show-block"
					>
						{t("pages.modelLab.trace.showInArchitecture", "Show block {{index}} in Architecture", { index: selectedBlock })}
					</Button>
				</Group>
			)}
			<ExplanationLevelSwitch value={level} onChange={setLevel} />
			<ExplanationList
				items={explanations}
				level={level}
				basis="interpreted"
				open={open}
				onOpen={setOpen}
				data-testid="model-lab-stepper-routing-explain"
			/>
		</Stack>
	);
}
