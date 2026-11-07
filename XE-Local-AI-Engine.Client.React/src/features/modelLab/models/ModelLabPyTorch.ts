import type { TFunction } from "i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import type {
	XeLocalAiEngineClientEndpointsModelLabV1ModelLabCheckpointPreflightResponse as CheckpointPreflight,
	XeLocalAiEngineClientServicesModelLabModelLabPyTorchBlock as PyTorchBlock,
	XeLocalAiEngineClientServicesModelLabModelLabPyTorchConfigSource as PyTorchConfigSource,
	XeLocalAiEngineClientServicesModelLabModelLabPyTorchGraph as PyTorchGraph,
	XeLocalAiEngineClientServicesModelLabModelLabPyTorchGraphNode as PyTorchGraphNode,
	XeLocalAiEngineClientServicesModelLabModelLabPyTorchIndexResponse as PyTorchIndexResponse,
	XeLocalAiEngineClientServicesModelLabModelLabPyTorchModule as PyTorchModule,
	XeLocalAiEngineClientServicesModelLabModelLabPyTorchRefusal as PyTorchRefusal,
} from "@/core/api/generated/types.gen";

export type {
	CheckpointPreflight,
	PyTorchBlock,
	PyTorchConfigSource,
	PyTorchGraph,
	PyTorchGraphNode,
	PyTorchIndexResponse,
	PyTorchModule,
	PyTorchRefusal,
};

// What the source viewer follows: a tensor group (through the join), one module, or one node of the PyTorch graph.
export type PyTorchCodeTarget =
	| { kind: "component"; componentId: string }
	| { kind: "module"; path: string }
	| { kind: "node"; nodeId: string };

export interface PyTorchCodeLocation {
	file: string;
	highlight: { start: number; end: number };
	reveal: number;
}

// The typed 409 body sits whole on the ApiError the interceptor throws.
export function pyTorchRefusal(error: unknown): PyTorchRefusal | null {
	if (!(error instanceof ApiError) || error.statusCode !== 409) {
		return null;
	}
	const reason = (error.apiProblemDetails as unknown as Record<string, unknown> | undefined)?.["reason"];
	return typeof reason === "string" ? (reason as PyTorchRefusal) : null;
}

export function isNotFound(error: unknown): boolean {
	return error instanceof ApiError && error.statusCode === 404;
}

export function pyTorchRefusalMessage(t: TFunction, reason: PyTorchRefusal): string {
	const messages: Record<PyTorchRefusal, string> = {
		Busy: t("pages.modelLab.pytorch.refusal.busy", "Another PyTorch index is being built. Try again when it has finished."),
		CheckpointNotFound: t("pages.modelLab.pytorch.refusal.checkpointNotFound", "The chosen checkpoint no longer exists."),
		CheckpointNotReady: t(
			"pages.modelLab.pytorch.refusal.checkpointNotReady",
			"The chosen checkpoint has not finished downloading.",
		),
		ConfigMissing: t("pages.modelLab.pytorch.refusal.configMissing", "The checkpoint or repository has no config.json."),
		ConfigTooLarge: t("pages.modelLab.pytorch.refusal.configTooLarge", "The config.json is larger than 1 MiB."),
		ConfigUnreadable: t("pages.modelLab.pytorch.refusal.configUnreadable", "The config.json could not be read as JSON."),
		IndexFailed: t("pages.modelLab.pytorch.refusal.indexFailed", "The PyTorch index could not be built."),
		NoBlocks: t("pages.modelLab.pytorch.refusal.noBlocks", "No repeated decoder blocks were found in the PyTorch model."),
		RemoteCode: t(
			"pages.modelLab.pytorch.refusal.remoteCode",
			"This model needs its own code from the repository (trust_remote_code). Such code is never run here.",
		),
		RepoGated: t(
			"pages.modelLab.pytorch.refusal.repoGated",
			"The repository is gated or private. Accept its terms on Hugging Face and save a token under Training.",
		),
		RepoUnreachable: t(
			"pages.modelLab.pytorch.refusal.repoUnreachable",
			"The repository or revision could not be reached on Hugging Face.",
		),
		ResultTooLarge: t("pages.modelLab.pytorch.refusal.resultTooLarge", "The PyTorch index is too large to keep."),
		RuntimeUnavailable: t(
			"pages.modelLab.pytorch.refusal.runtimeUnavailable",
			"The training runtime is not installed or not ready. Install it on the Training page.",
		),
		SourceChanged: t(
			"pages.modelLab.pytorch.refusal.sourceChanged",
			"The file changed since the index was built. Build the index again.",
		),
		SourceNotInIndex: t("pages.modelLab.pytorch.refusal.sourceNotInIndex", "This file is not part of the index."),
		SourceTooLarge: t("pages.modelLab.pytorch.refusal.sourceTooLarge", "This file is too large to show."),
		UnsupportedModelType: t(
			"pages.modelLab.pytorch.refusal.unsupportedModelType",
			"The installed transformers version does not know this model type.",
		),
		UnsupportedPlatform: t("pages.modelLab.pytorch.refusal.unsupportedPlatform", "The PyTorch index works on Linux only."),
	};
	return messages[reason] ?? t("pages.modelLab.pytorch.refusal.indexFailed", "The PyTorch index could not be built.");
}

// True only when a checkpoint's preflight against this GGUF shows another shape: hidden size or trunk block count. A check
// that has not answered, failed, or stopped before reading the shapes (a fit refusal) leaves the checkpoint offered.
export function checkpointShapeDiffers(preflight: CheckpointPreflight | undefined): boolean {
	const differs = (left: number | null | undefined, right: number | null | undefined): boolean =>
		left != null && right != null && left !== right;
	return (
		differs(preflight?.checkpointHiddenSize, preflight?.ggufEmbeddingLength) ||
		differs(preflight?.checkpointBlockCount, preflight?.steerableBlockCount)
	);
}

// The class the config declares when the index shows the model through another one (an encoder as a causal LM), else null.
export function declaredOtherClass(response: PyTorchIndexResponse): string | null {
	const declared = response.index.declaredArchitectures ?? [];
	return declared.length > 0 && !declared.includes(response.index.modelClass ?? "") ? declared.join(", ") : null;
}

// The PyTorch layer a GGUF block stands for: only when the join paired the blocks by index. Null for the tensors outside.
export function joinedPyTorchBlock(response: PyTorchIndexResponse, blockIndex: number): PyTorchBlock | undefined {
	return response.join.blocksJoined ? response.index.blocks?.find((block) => block.index === blockIndex) : undefined;
}

function moduleByPath(response: PyTorchIndexResponse, path: string): PyTorchModule | undefined {
	return [...(response.index.blocks ?? []).flatMap((block) => block.modules ?? []), ...(response.index.outside ?? [])].find(
		(module) => module.path === path,
	);
}

function moduleLocation(module: PyTorchModule | undefined): PyTorchCodeLocation | null {
	if (!module?.file || module.classStart == null || module.classEnd == null) {
		return null;
	}
	return {
		file: module.file,
		highlight: { start: module.classStart, end: module.classEnd },
		reveal: module.forwardLine ?? module.classStart,
	};
}

export function nodeLocation(node: PyTorchGraphNode | undefined): PyTorchCodeLocation | null {
	if (!node?.file || node.line == null) {
		return null;
	}
	return { file: node.file, highlight: { start: node.line, end: node.line }, reveal: node.line };
}

// Where a target's code is, or null for "no code link". A group opens the first module the join linked it to.
export function codeLocation(
	response: PyTorchIndexResponse,
	graph: PyTorchGraph | undefined,
	target: PyTorchCodeTarget,
): PyTorchCodeLocation | null {
	if (target.kind === "node") {
		return nodeLocation(graph?.nodes?.find((node) => node.id === target.nodeId));
	}
	const path =
		target.kind === "module"
			? target.path
			: response.join.components.find((link) => link.componentId === target.componentId)?.modulePaths[0];
	return path === undefined ? null : moduleLocation(moduleByPath(response, path));
}
