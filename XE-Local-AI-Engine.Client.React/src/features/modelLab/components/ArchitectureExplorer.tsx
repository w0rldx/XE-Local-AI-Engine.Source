import { Alert, Button, Group, Loader, Select, Stack, Switch, Text } from "@mantine/core";
import { IconArrowRight } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { Fragment, type ReactNode, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	getModelLabArchitectureOptions,
	getModelLabExperimentRoutingOptions,
	getModelLabExperimentTraceOptions,
	listModelLabExperimentsOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { ArchitectureBlockDetail } from "@/features/modelLab/components/ArchitectureBlockDetail";
import {
	ArchitectureBreadcrumb,
	ArchitectureMtpDetail,
	ArchitectureRangeDetail,
	ArchitectureRootDetail,
	ArchitectureStackDetail,
} from "@/features/modelLab/components/ArchitectureHierarchyDetail";
import { ArchitectureModelGraph } from "@/features/modelLab/components/ArchitectureModelGraph";
import { ArchitectureOverview, type RoutingFailure } from "@/features/modelLab/components/ArchitectureOverview";
import { ArchitectureValues } from "@/features/modelLab/components/ArchitectureValues";
import {
	type ArchitectureGroup,
	type ArchitectureSelection,
	formatBlockRanges,
	type OutsideEnd,
	outsideGroups,
	promptExcerpt,
	summarizeTraceByBlock,
} from "@/features/modelLab/models/ModelLabArchitecture";
import type { ExplanationLevel } from "@/features/modelLab/models/ModelLabExplanation";
import {
	type ArchitectureHierarchy,
	buildArchitectureHierarchy,
	chipFor,
	vocabularySize,
} from "@/features/modelLab/models/ModelLabHierarchy";
import { breadcrumbFor } from "@/features/modelLab/models/ModelLabHierarchyLabels";
import { type ArchitectureDetailPatch, type ModelLabJump, sameExperimentId } from "@/features/modelLab/models/ModelLabNavigation";

interface ArchitectureExplorerProps {
	// The page's model, block, laid-over trace and explanation level, all from the URL.
	modelName: string | null;
	selection: ArchitectureSelection | null;
	traceId: string | null;
	// What the block detail shows inside the selection: its open operation, tensor group and tensor.
	inside: ArchitectureDetailPatch;
	// Changes each time the tab shows this explorer again: the block detail then scrolls its open operation into view.
	arrival: number;
	// Runs an arrival's scroll once the router has restored its own scroll positions.
	afterNavigation: (run: () => void) => void;
	level: ExplanationLevel;
	// Null selects the whole model. `onShown` runs only once the page shows that selection: never for a blocked change.
	onSelect: (selection: ArchitectureSelection | null, onShown?: () => void) => void;
	// A selection inside the block detail; `onShown` as for `onSelect`.
	onDetailChange: (patch: ArchitectureDetailPatch, onShown?: () => void) => void;
	onTraceChange: (traceId: string | null) => void;
	onLevelChange: (level: ExplanationLevel) => void;
	onJump?: (to: ModelLabJump) => void;
}

// One installed model's structure from its GGUF header, with an optional saved trace of that model laid over its blocks.
export function ArchitectureExplorer({
	modelName,
	selection,
	traceId,
	inside,
	arrival,
	afterNavigation,
	level,
	onSelect,
	onDetailChange,
	onTraceChange,
	onLevelChange,
	onJump,
}: ArchitectureExplorerProps) {
	const { t, i18n } = useTranslation();
	const traceInput = useRef<HTMLInputElement>(null);
	const [graphShown, setGraphShown] = useState(false);

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
	// Only a finished trace of this model in the held list is laid over: a link's id is nothing until the list confirms it,
	// and a run deleted since drops out of a refreshed list. A later failed refetch keeps that data.
	const overlayId =
		traceId === null ? null : (traceOptions.find((option) => sameExperimentId(option.value, traceId))?.value ?? null);
	const trace = useQuery({
		...getModelLabExperimentTraceOptions({ path: { experimentId: overlayId ?? "" } }),
		enabled: overlayId !== null,
	});
	const traceSummaries = trace.data ? summarizeTraceByBlock(trace.data.records) : undefined;

	const data = descriptor.data;
	// Only a family whose graph has a router can have routing: a dense model's overlay never asks for it.
	const routedArchitecture = data?.templates.some((candidate) => candidate.nodes.some((node) => node.id === "router")) ?? false;
	const routedOverlay = overlayId !== null && routedArchitecture;
	// The block detail's and the stepper's query: one cache entry per run, whichever view asks first.
	const routing = useQuery({
		...getModelLabExperimentRoutingOptions({ path: { experimentId: overlayId ?? "" } }),
		// A saved run's routing never changes.
		staleTime: Number.POSITIVE_INFINITY,
		// A dense or unrouted run answers 404; the stepper is where a real failure is shown and retried.
		retryOnMount: false,
		enabled: routedOverlay,
	});
	const hierarchy = data ? buildArchitectureHierarchy(data) : null;
	const displayed = new Set(data?.blocks.map((candidate) => candidate.index));
	// Only a confirmed 404 means the run recorded no routing; any other failure is unreadable routing.
	const routingFailure: RoutingFailure | null =
		routedOverlay && routing.isError
			? routing.error instanceof ApiError && routing.error.statusCode === 404
				? "missing"
				: "unreadable"
			: null;
	// The one routed set the row, the graph and the block detail share: the blocks a saved routing step names that this
	// file shows. None while the routing fails, even when data cached before a failed refetch survives.
	const routedBlocks =
		routedOverlay && routing.data && routingFailure === null
			? new Set(
					routing.data.steps.flatMap((step) => step.blocks.map((entry) => entry.layer)).filter((index) => displayed.has(index)),
				)
			: null;
	const templates = data?.support === "detailed" ? data.templates : [];
	const values = data?.hyperparameters ?? null;
	const tracedBlocks = traceSummaries ? new Set(traceSummaries.keys()) : null;

	// A selection made from a control inside a detail (which unmounts with it) or from an unfocusable graph node moves
	// focus to the new heading; a row pick keeps focus on the row. The intent is armed only once the page shows the
	// selection, so a blocked change leaves none behind for a later Back or Forward to the same selection.
	const focusIntent = useRef<ArchitectureSelection | null | undefined>(undefined);
	// The mounted heading and the selection it was rendered for, refreshed on every render.
	const heading = useRef<{ element: HTMLElement; selection: ArchitectureSelection | null } | null>(null);
	const selectFromRow = (next: ArchitectureSelection | null): void => {
		focusIntent.current = undefined;
		onSelect(next);
	};
	const selectFromDetail = (next: ArchitectureSelection | null): void => {
		focusIntent.current = undefined;
		onSelect(next, () => {
			// Shown: the heading may already be up for it, else it takes focus when it mounts.
			if (heading.current?.selection === next) {
				heading.current.element.focus();
			} else {
				focusIntent.current = next;
			}
		});
	};
	const headingRef = (element: HTMLElement | null): void => {
		heading.current = element === null ? null : { element, selection };
		if (element !== null && focusIntent.current !== undefined && focusIntent.current === selection) {
			focusIntent.current = undefined;
			element.focus();
		}
	};

	const missing = (text: string) => (
		<Alert color="yellow" tabIndex={-1} ref={headingRef} data-testid="model-lab-architecture-block-missing">
			{text}
		</Alert>
	);
	const outsideDetail = (end: OutsideEnd, groups: ArchitectureGroup[]) => (
		<ArchitectureBlockDetail
			modelName={data?.modelName ?? ""}
			architecture={data?.architecture ?? null}
			suggestedBaseRepo={data?.suggestedBaseRepo ?? null}
			block={undefined}
			outside={end}
			groups={groups}
			template={undefined}
			traceSummaries={traceSummaries}
			traceId={overlayId}
			routed={false}
			selection={inside}
			arrival={arrival}
			afterNavigation={afterNavigation}
			onSelect={onDetailChange}
			explanationLevel={level}
			onExplanationLevel={onLevelChange}
			headingRef={headingRef}
		/>
	);
	// One branch per selection kind; a link or a jump can name something the file does not hold, which is said, never guessed.
	const detail = (shown: ArchitectureHierarchy, file: NonNullable<typeof data>): ReactNode => {
		if (selection === null) {
			return <ArchitectureRootDetail hierarchy={shown} values={values} headingRef={headingRef} />;
		}
		if (typeof selection === "number") {
			const block = file.blocks.find((candidate) => candidate.index === selection);
			if (block === undefined) {
				return missing(
					t(
						"pages.modelLab.architecture.blockMissing",
						"Block {{index}} is not in this file. One possible reason: the file is one part of a split GGUF and the block is in another part.",
						{ index: selection },
					),
				);
			}
			return (
				<ArchitectureBlockDetail
					modelName={file.modelName}
					architecture={file.architecture}
					suggestedBaseRepo={file.suggestedBaseRepo}
					block={block}
					outside="embedding"
					groups={block.groups}
					template={templates.find((candidate) => candidate.kind === block.kind)}
					traceSummaries={traceSummaries}
					traceId={overlayId}
					routed={routedBlocks?.has(block.index) === true}
					selection={inside}
					arrival={arrival}
					afterNavigation={afterNavigation}
					onSelect={onDetailChange}
					explanationLevel={level}
					onExplanationLevel={onLevelChange}
					headingRef={headingRef}
				/>
			);
		}
		switch (selection) {
			case "embedding": {
				const groups = outsideGroups(file.nonBlock, "embedding");
				return groups.length === 0 ? (
					missing(
						t(
							"pages.modelLab.architecture.outsideMissing.embedding",
							"Embedding is not in this file. One possible reason: the file is one part of a split GGUF and it is in another part.",
						),
					)
				) : (
					<Stack gap="sm">
						<ArchitectureValues
							values={[
								{ key: "vocabulary", value: vocabularySize(file), basis: "derived" },
								{ key: "embeddingLength", value: values?.embeddingLength, basis: "structural" },
							]}
							mtpDeclared={values?.nextnPredictLayers != null}
						/>
						{outsideDetail("embedding", groups)}
					</Stack>
				);
			}
			case "output": {
				const groups = outsideGroups(file.nonBlock, "output");
				return groups.length === 0
					? missing(
							t(
								"pages.modelLab.architecture.outsideMissing.output",
								"Output is not in this file. One possible reason: the file is one part of a split GGUF and it is in another part.",
							),
						)
					: outsideDetail("output", groups);
			}
			case "other": {
				const groups = outsideGroups(file.nonBlock, "other");
				return groups.length === 0
					? missing(
							t("pages.modelLab.architecture.outsideMissing.other", "No other tensors outside the blocks are in this file."),
						)
					: outsideDetail("other", groups);
			}
			case "stack":
				return shown.trunkBlocks.length === 0 ? (
					missing(
						t(
							"pages.modelLab.architecture.outsideMissing.stack",
							"No blocks of the main stack are in this file. One possible reason: the file is one part of a split GGUF and these blocks are in other parts.",
						),
					)
				) : (
					<ArchitectureStackDetail hierarchy={shown} values={values} headingRef={headingRef} onOpen={selectFromDetail} />
				);
			case "mtp":
				return shown.mtp.length === 0 ? (
					missing(t("pages.modelLab.architecture.outsideMissing.mtp", "No multi-token prediction blocks are in this file."))
				) : (
					<ArchitectureMtpDetail hierarchy={shown} values={values} headingRef={headingRef} onOpen={selectFromDetail} />
				);
			default: {
				const chip = chipFor(shown, selection);
				if (chip === undefined) {
					const [first, last] = selection.split("-");
					return missing(
						t(
							"pages.modelLab.architecture.hierarchy.rangeMissing",
							"Blocks {{first}}–{{last}} are not one group in this file. Choose a group from the blocks above.",
							{ first, last },
						),
					);
				}
				return (
					<ArchitectureRangeDetail
						chip={chip}
						architecture={file.architecture}
						templates={templates}
						values={values}
						tracedBlocks={tracedBlocks}
						routedBlocks={routedBlocks}
						level={level}
						onLevelChange={onLevelChange}
						headingRef={headingRef}
						onOpen={selectFromDetail}
					/>
				);
			}
		}
	};

	return (
		<Stack gap="md">
			{modelName !== null && traceOptions.length > 0 && (
				<Select
					label={t("pages.modelLab.architecture.trace", "Trace to lay over the blocks")}
					description={t(
						"pages.modelLab.architecture.traceNote",
						"Measured on the Hugging Face checkpoint, joined by block index. The checkpoint was paired with this GGUF by shape only.",
					)}
					placeholder={t("pages.modelLab.architecture.noTraceChosen", "No trace")}
					data={traceOptions}
					value={overlayId}
					onChange={onTraceChange}
					clearable={true}
					ref={traceInput}
					// Mantine leaves the clear button out of the Tab order and unnamed; a keyboard user must reach it too.
					clearButtonProps={{
						"aria-label": t("pages.modelLab.architecture.clearTrace", "Clear the laid-over trace"),
						tabIndex: 0,
						"aria-hidden": false,
					}}
					// The clear button unmounts with the value, so focus goes back to the field instead of the page.
					onClear={() => traceInput.current?.focus()}
				/>
			)}
			{onJump && overlayId !== null && (
				<Group>
					<Button
						size="compact-sm"
						variant="light"
						rightSection={<IconArrowRight size={14} aria-hidden={true} />}
						onClick={() => onJump({ tab: "trace", experimentId: overlayId })}
						data-testid="model-lab-architecture-open-trace"
					>
						{t("pages.modelLab.architecture.openTrace", "Open trace")}
					</Button>
				</Group>
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
			{/* A file with no blocks (a tensorless first shard) has nothing a template could have drawn. */}
			{data?.support === "detailed" && data.templates.length === 0 && data.blocks.length > 0 && (
				<Alert color="blue" data-testid="model-lab-architecture-no-template">
					{t(
						"pages.modelLab.architecture.noTemplate",
						"No operation graph is drawn for this file: its layout, for example mixture-of-experts, is not one the {{architecture}} template describes. The tensor groups are read from the file as usual.",
						{ architecture: data.architecture ?? t("pages.models.details.unknown", "Unknown") },
					)}
				</Alert>
			)}
			{/* Only families whose Hugging Face names rest on an assumed source layout have a note; the rest show none. */}
			{data?.support === "detailed" &&
				data.architecture !== null &&
				i18n.exists(`pages.modelLab.architecture.layoutNote.${data.architecture}`) && (
					// A module path is one long word: it breaks anywhere on a narrow screen instead of running out of the box.
					<Alert color="gray" style={{ overflowWrap: "anywhere" }} data-testid="model-lab-architecture-layout-note">
						{t(`pages.modelLab.architecture.layoutNote.${data.architecture}`)}
					</Alert>
				)}
			{data && hierarchy && (
				<>
					<Switch
						size="xs"
						label={t("pages.modelLab.architecture.hierarchy.graph.show", "Show graph")}
						checked={graphShown}
						onChange={(event) => setGraphShown(event.currentTarget.checked)}
						data-testid="model-lab-architecture-graph-switch"
					/>
					{graphShown && (
						<ArchitectureModelGraph
							// React Flow fits the view only on mount: another model gets a fresh fit, a block pick keeps the view.
							key={data.modelName}
							hierarchy={hierarchy}
							selection={selection}
							tracedBlocks={tracedBlocks}
							routedBlocks={routedBlocks}
							templates={templates}
							// A node takes no focus, so focus follows the pick to the new heading as from a detail control.
							onSelect={selectFromDetail}
						/>
					)}
					<ArchitectureOverview
						hierarchy={hierarchy}
						hasOutputTensor={data.nonBlock.some((group) => group.component === "output")}
						split={(values?.splitCount ?? 1) > 1}
						selection={selection}
						tracedBlocks={tracedBlocks}
						routedBlocks={routedBlocks}
						routingFailure={routingFailure}
						onSelect={selectFromRow}
					/>
					<ArchitectureBreadcrumb crumbs={breadcrumbFor(t, hierarchy, selection)} onOpen={selectFromDetail} />
					{/* Keyed: a same-block jump to another model must not keep the last model's form state. */}
					<Fragment key={`${modelName}:${selection}`}>{detail(hierarchy, data)}</Fragment>
				</>
			)}
		</Stack>
	);
}
