import {
	Alert,
	Anchor,
	Button,
	Group,
	Loader,
	ScrollArea,
	SegmentedControl,
	Select,
	Stack,
	Table,
	Text,
	TextInput,
} from "@mantine/core";
import { useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { type ReactNode, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	buildModelLabPyTorchIndexMutation,
	getModelLabPyTorchIndexOptions,
	getModelLabPyTorchIndexQueryKey,
	getModelLabPyTorchSourceOptions,
	getModelLabPyTorchSourceQueryKey,
	listBaseArtifactsOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { preflightModelLabCheckpoint } from "@/core/api/generated/sdk.gen";
import { COMPACT_CONTROLS_BREAKPOINT } from "@/core/layout/constants/LayoutBreakpoints";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { ArchitecturePyTorchGraph } from "@/features/modelLab/components/ArchitecturePyTorchGraph";
import type { ArchitectureBlock } from "@/features/modelLab/models/ModelLabArchitecture";
import {
	checkpointShapeDiffers,
	codeLocation,
	declaredOtherClass,
	isNotFound,
	joinedPyTorchBlock,
	type PyTorchCodeTarget,
	type PyTorchConfigSource,
	type PyTorchIndexResponse,
	type PyTorchModule,
	pyTorchRefusal,
	pyTorchRefusalMessage,
} from "@/features/modelLab/models/ModelLabPyTorch";

interface ArchitecturePyTorchPanelProps {
	modelName: string;
	suggestedBaseRepo: string | null;
	// Undefined for the tensors outside every block.
	block: ArchitectureBlock | undefined;
	target: PyTorchCodeTarget | null;
	onTarget: (target: PyTorchCodeTarget) => void;
}

// A tinted box, not a pill: the provenance runs to three lines on a phone, and a pill's round ends clip a wrapped line.
const provenanceStyle = {
	background: "var(--mantine-color-grape-light)",
	color: "var(--mantine-color-grape-light-color)",
	borderRadius: "var(--mantine-radius-sm)",
	overflowWrap: "anywhere",
} as const;

function useRefusalText(): (error: unknown, fallback: string) => string {
	const { t } = useTranslation();
	return (error, fallback) => {
		const reason = pyTorchRefusal(error);
		return reason === null ? apiErrorMessage(error, fallback) : pyTorchRefusalMessage(t, reason);
	};
}

// The config the index is built from: a downloaded checkpoint, or only the config.json of a Hugging Face repository. It
// opens on the source the current index was built from.
function PyTorchBuildForm({
	modelName,
	suggestedBaseRepo,
	current,
}: {
	modelName: string;
	suggestedBaseRepo: string | null;
	current: PyTorchConfigSource | undefined;
}) {
	const { t } = useTranslation();
	const refusalText = useRefusalText();
	const queryClient = useQueryClient();
	const { width } = useWindowDimensions();
	const fromHub = current?.kind === "huggingface";
	const [source, setSource] = useState<string>(fromHub ? "huggingface" : "checkpoint");
	const [artifactId, setArtifactId] = useState<string | null>(current?.baseArtifactId ?? null);
	const [repoId, setRepoId] = useState((fromHub ? current.repoId : suggestedBaseRepo) ?? "");
	const [revision, setRevision] = useState((fromHub ? current.revision : undefined) ?? "");
	const artifacts = useQuery({ ...listBaseArtifactsOptions(), enabled: source === "checkpoint" });
	const readyArtifacts = (artifacts.data?.items ?? []).filter((item) => item.status === "Ready" && item.id);
	// The pairing check the trace form runs, offline for a downloaded checkpoint: a checkpoint of another shape would only
	// build a mismatch view, so it is not offered. The current index's checkpoint stays, whatever its shape.
	const pairings = useQueries({
		queries: readyArtifacts.map((item) => ({
			queryKey: ["model-lab-pytorch-pairing", modelName, item.id],
			queryFn: async ({ signal }: { signal: AbortSignal }) =>
				(
					await preflightModelLabCheckpoint({
						body: { baseArtifactId: item.id, installedModelName: modelName },
						signal,
						throwOnError: true,
					})
				).data,
			enabled: source === "checkpoint",
			staleTime: Number.POSITIVE_INFINITY,
			retry: false,
		})),
	});
	const artifactOptions = readyArtifacts.flatMap((item, position) =>
		item.id === artifactId || !checkpointShapeDiffers(pairings[position]?.data)
			? [{ value: item.id ?? "", label: `${item.repoId} @ ${item.revision?.slice(0, 12)}` }]
			: [],
	);
	const build = useMutation({
		...buildModelLabPyTorchIndexMutation(),
		onSuccess: (data) => queryClient.setQueryData(getModelLabPyTorchIndexQueryKey({ path: { modelName } }), data),
	});
	const ready = source === "checkpoint" ? artifactId !== null : repoId.trim().length > 0;
	const submit = (): void => {
		build.mutate({
			path: { modelName },
			body:
				source === "checkpoint"
					? { source, baseArtifactId: artifactId }
					: { source, repoId: repoId.trim(), revision: revision.trim() || null },
		});
	};
	const refusal = pyTorchRefusal(build.error);

	return (
		<Stack gap="xs">
			<SegmentedControl
				value={source}
				onChange={setSource}
				fullWidth={true}
				// On a phone two side-by-side labels clip; stacked, each reads on one line.
				orientation={width < COMPACT_CONTROLS_BREAKPOINT ? "vertical" : "horizontal"}
				data={[
					{ value: "checkpoint", label: t("pages.modelLab.pytorch.sourceCheckpoint", "Downloaded checkpoint") },
					{ value: "huggingface", label: t("pages.modelLab.pytorch.sourceHuggingFace", "Hugging Face config.json") },
				]}
			/>
			{source === "checkpoint" ? (
				<Select
					label={t("pages.modelLab.pytorch.checkpoint", "Checkpoint")}
					placeholder={t("pages.modelLab.pytorch.checkpointPlaceholder", "Choose a downloaded checkpoint")}
					data={artifactOptions}
					value={artifactId}
					onChange={setArtifactId}
					nothingFoundMessage={t("pages.modelLab.pytorch.noCheckpoints", "No downloaded checkpoint is ready.")}
				/>
			) : (
				<Group grow={true} align="flex-start" wrap="wrap">
					<TextInput
						label={t("pages.modelLab.pytorch.repo", "Repository")}
						placeholder="Qwen/Qwen3.5-0.8B"
						value={repoId}
						onChange={(event) => setRepoId(event.currentTarget.value)}
						miw={200}
					/>
					<TextInput
						label={t("pages.modelLab.pytorch.revision", "Revision (optional)")}
						value={revision}
						onChange={(event) => setRevision(event.currentTarget.value)}
						miw={160}
					/>
				</Group>
			)}
			<Group gap="xs">
				<Button size="xs" onClick={submit} disabled={!ready} loading={build.isPending}>
					{t("pages.modelLab.pytorch.build", "Build PyTorch index")}
				</Button>
			</Group>
			{build.isError && (
				<Alert color={refusal === "UnsupportedPlatform" ? "yellow" : "red"} data-testid="model-lab-pytorch-refusal">
					{refusalText(build.error, t("pages.modelLab.pytorch.refusal.indexFailed", "The PyTorch index could not be built."))}
					{refusal === "RuntimeUnavailable" && (
						<>
							{" "}
							<Anchor component={Link} to="/training" size="sm">
								{t("pages.modelLab.pytorch.openTraining", "Open the Training page")}
							</Anchor>
						</>
					)}
				</Alert>
			)}
		</Stack>
	);
}

function PyTorchIndexSummary({ response }: { response: PyTorchIndexResponse }) {
	const { t } = useTranslation();
	const { index, join, source } = response;
	const declared = declaredOtherClass(response);
	const indexedFrom = {
		modelClass: index.modelClass,
		repo: source.repoId,
		revision: source.revision?.slice(0, 12),
	};
	return (
		<Stack gap={4}>
			<Text size="xs" fw={600} px="xs" py={4} style={provenanceStyle} data-testid="model-lab-pytorch-provenance">
				{t(
					"pages.modelLab.pytorch.provenance",
					"transformers {{transformers}}, torch {{torch}} in the installed runtime · PyTorch reference path; GPU runs may use fused kernels · {{exportMode}}",
					{
						transformers: index.transformersVersion,
						torch: index.torchVersion,
						exportMode:
							index.exportMode === "full"
								? t("pages.modelLab.pytorch.exportFull", "exported at full depth")
								: t("pages.modelLab.pytorch.exportRepresentative", "exported one block per layer kind"),
					},
				)}
			</Text>
			<Text size="xs" c="dimmed" data-testid="model-lab-pytorch-source-summary">
				{source.kind === "checkpoint"
					? t(
							"pages.modelLab.pytorch.indexedFromCheckpoint",
							"{{modelClass}} from the config of the downloaded checkpoint {{repo}} @ {{revision}}",
							indexedFrom,
						)
					: t(
							"pages.modelLab.pytorch.indexedFromHuggingFace",
							"{{modelClass}} from the Hugging Face config.json of {{repo}} @ {{revision}}",
							indexedFrom,
						)}
			</Text>
			{declared !== null && (
				<Text size="xs" c="yellow" data-testid="model-lab-pytorch-declared-class">
					{t(
						"pages.modelLab.pytorch.declaredOtherClass",
						"The config declares {{declared}}; shown through {{modelClass}}, so its head differs.",
						{ declared, modelClass: index.modelClass },
					)}
				</Text>
			)}
			{index.anchoredLines === false && (
				<Text size="xs" c="dimmed" data-testid="model-lab-pytorch-call-site-note">
					{t("pages.modelLab.pytorch.callSiteLines", "Lines inside helper functions are shown at their call site.")}
				</Text>
			)}
			{index.exportError && (
				<Alert color="yellow" data-testid="model-lab-pytorch-export-error">
					{t(
						"pages.modelLab.pytorch.exportError",
						"No operation graph: the export failed ({{error}}). Modules and their classes still link to code.",
						{ error: index.exportError },
					)}
				</Alert>
			)}
			{!join.blocksJoined && (
				<Alert color="yellow" data-testid="model-lab-pytorch-join-reason">
					{join.reason === "ggufUnreadable"
						? t("pages.modelLab.pytorch.joinUnreadable", "The GGUF file could not be read, so its blocks are not paired.")
						: t(
								"pages.modelLab.pytorch.joinMismatch",
								"The GGUF has {{gguf}} blocks and the PyTorch model {{pytorch}}, so they are not paired by index. Choose a PyTorch block to view.",
								{ gguf: join.ggufTrunkBlockCount, pytorch: join.pyTorchBlockCount },
							)}
				</Alert>
			)}
		</Stack>
	);
}

function PyTorchModuleList({
	modules,
	basePath,
	selectedPath,
	onSelect,
}: {
	modules: PyTorchModule[];
	basePath: string | undefined;
	selectedPath: string | null;
	onSelect: (path: string) => void;
}) {
	const { t } = useTranslation();
	const relative = (path: string): string =>
		basePath && path.startsWith(`${basePath}.`)
			? path.slice(basePath.length + 1)
			: path === basePath
				? t("pages.modelLab.pytorch.blockRoot", "(block)")
				: path;
	return (
		<ScrollArea.Autosize mah={240} type="auto">
			<Table fz="xs" withRowBorders={false} data-testid="model-lab-pytorch-modules">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.modelLab.pytorch.module", "Module")}</Table.Th>
						<Table.Th>{t("pages.modelLab.pytorch.class", "Class")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{modules.map((module) => (
						<Table.Tr key={module.path}>
							<Table.Td>
								<Button
									variant={module.path === selectedPath ? "light" : "subtle"}
									size="compact-xs"
									ff="monospace"
									aria-pressed={module.path === selectedPath}
									disabled={!module.file}
									onClick={() => onSelect(module.path ?? "")}
								>
									{relative(module.path ?? "")}
								</Button>
							</Table.Td>
							<Table.Td ff="monospace">{module.class}</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</ScrollArea.Autosize>
	);
}

function PyTorchSourceViewer({
	indexId,
	file,
	sha256,
	highlight,
	reveal,
}: {
	indexId: string;
	file: string;
	// The hash the index lists for the file. A rebuild after a changed file keeps the indexId but lists a new hash.
	sha256: string | undefined;
	highlight: { start: number; end: number };
	reveal: number;
}) {
	const { t } = useTranslation();
	const refusalText = useRefusalText();
	const options = { query: { index: indexId, file } };
	const generated = getModelLabPyTorchSourceOptions(options);
	const source = useQuery({
		...generated,
		// The generated queryFn reads only the key's first entry, so the hash appended after it changes the cache entry,
		// not the request; the cast keeps the queryFn's key type.
		queryKey: [...getModelLabPyTorchSourceQueryKey(options), sha256] as unknown as typeof generated.queryKey,
		// A file is pinned by the index's sha256, which is in the key, so a fetched one never changes.
		staleTime: Number.POSITIVE_INFINITY,
		retry: false,
	});
	return (
		<Stack gap={4}>
			<Text size="xs" ff="monospace" data-testid="model-lab-pytorch-file">
				{highlight.start === highlight.end
					? t("pages.modelLab.pytorch.fileLine", "{{file}}, line {{start}}", { file, ...highlight })
					: t("pages.modelLab.pytorch.fileLines", "{{file}}, lines {{start}}–{{end}}", { file, ...highlight })}
			</Text>
			{source.isPending && <Loader size="sm" />}
			{source.isError && (
				<Alert color="red" data-testid="model-lab-pytorch-source-failed">
					{refusalText(source.error, t("pages.modelLab.pytorch.sourceFailed", "The source file could not be loaded."))}
				</Alert>
			)}
			{source.data && (
				<CodeEditor
					value={source.data.text}
					language="python"
					readOnly={true}
					height={360}
					highlightLines={[highlight]}
					revealLine={reveal}
					aria-label={file}
					data-testid="model-lab-pytorch-code"
				/>
			)}
		</Stack>
	);
}

// The PyTorch side of one selection: how the index was built, the block's PyTorch graph and modules, and their source.
export function ArchitecturePyTorchPanel({
	modelName,
	suggestedBaseRepo,
	block,
	target,
	onTarget,
}: ArchitecturePyTorchPanelProps) {
	const { t } = useTranslation();
	const refusalText = useRefusalText();
	const indexQuery = useQuery({ ...getModelLabPyTorchIndexOptions({ path: { modelName } }), retry: false });
	const [chosenBlock, setChosenBlock] = useState<string | null>(null);
	const response = indexQuery.data;

	let body: ReactNode = null;
	if (response && block?.kind === "mtp") {
		body = (
			<Text size="sm" c="dimmed" data-testid="model-lab-pytorch-mtp">
				{t("pages.modelLab.pytorch.mtp", "The MTP block has no PyTorch layer.")}
			</Text>
		);
	} else if (response) {
		const blocks = response.index.blocks ?? [];
		const pyBlock =
			block === undefined
				? undefined
				: (joinedPyTorchBlock(response, block.index) ?? blocks.find((candidate) => String(candidate.index) === chosenBlock));
		const graphId = block === undefined ? response.index.outsideGraphId : pyBlock?.graphId;
		const graph = response.index.graphs?.find((candidate) => candidate.graphId === graphId);
		const modules = block === undefined ? (response.index.outside ?? []) : (pyBlock?.modules ?? []);
		const location = target ? codeLocation(response, graph, target) : null;
		body = (
			<Stack gap="sm">
				{block !== undefined && !response.join.blocksJoined && (
					<Select
						label={t("pages.modelLab.pytorch.chooseBlock", "PyTorch block")}
						data={blocks.map((candidate) => ({
							value: String(candidate.index),
							label: `${candidate.index}: ${candidate.class}${candidate.layerType ? ` (${candidate.layerType})` : ""}`,
						}))}
						value={chosenBlock}
						onChange={setChosenBlock}
					/>
				)}
				{graph && (
					<ArchitecturePyTorchGraph
						graph={graph}
						rootName={(block === undefined ? response.index.modelClass : pyBlock?.class) ?? ""}
						selectedNodeId={target?.kind === "node" ? target.nodeId : null}
						onSelectNode={(nodeId) => onTarget({ kind: "node", nodeId })}
					/>
				)}
				{modules.length > 0 && (
					<PyTorchModuleList
						modules={modules}
						basePath={pyBlock?.path}
						selectedPath={target?.kind === "module" ? target.path : null}
						onSelect={(path) => onTarget({ kind: "module", path })}
					/>
				)}
				{target === null ? (
					<Text size="xs" c="dimmed">
						{t("pages.modelLab.pytorch.pickHint", "Select a tensor group, a module or a graph node to see its code.")}
					</Text>
				) : location === null ? (
					<Text size="sm" c="dimmed" data-testid="model-lab-pytorch-no-link">
						{t("pages.modelLab.pytorch.noLink", "No code link for this selection.")}
					</Text>
				) : (
					<PyTorchSourceViewer
						indexId={response.indexId}
						file={location.file}
						sha256={response.index.files?.find((entry) => entry.file === location.file)?.sha256}
						highlight={location.highlight}
						reveal={location.reveal}
					/>
				)}
			</Stack>
		);
	}

	return (
		<Stack gap="sm" data-testid="model-lab-pytorch">
			<Text size="sm" fw={600}>
				{t("pages.modelLab.pytorch.title", "PyTorch")}
			</Text>
			{indexQuery.isPending && <Loader size="sm" />}
			{indexQuery.isError && isNotFound(indexQuery.error) && (
				<Text size="sm" c="dimmed" data-testid="model-lab-pytorch-not-built">
					{t(
						"pages.modelLab.pytorch.notBuilt",
						"No PyTorch index for this model yet. Build one from a checkpoint or from a Hugging Face config.json.",
					)}
				</Text>
			)}
			{indexQuery.isError && !isNotFound(indexQuery.error) && (
				<Alert color="red">
					{refusalText(indexQuery.error, t("pages.modelLab.pytorch.loadFailed", "The PyTorch index could not be loaded."))}
				</Alert>
			)}
			{response && <PyTorchIndexSummary response={response} />}
			{!indexQuery.isPending && (
				<PyTorchBuildForm modelName={modelName} suggestedBaseRepo={suggestedBaseRepo} current={response?.source} />
			)}
			{body}
		</Stack>
	);
}
