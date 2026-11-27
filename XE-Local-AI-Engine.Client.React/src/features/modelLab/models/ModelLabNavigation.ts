import { z } from "zod";

import { LLAMACPP_PROVIDER } from "@/core/models/LocalModelProviders";
import type { BlockRangeToken } from "@/features/modelLab/models/ModelLabArchitecture";

// The page's tabs, in display order. `?tab=` on the route takes one of these, so a tab is linkable and survives a reload.
export const modelLabTabs = ["architecture", "compare", "nextToken", "inspect", "trace", "playground"] as const;
export type ModelLabTab = (typeof modelLabTabs)[number];
export const defaultModelLabTab: ModelLabTab = "architecture";

// A GGUF tensor name holds at most 256 UTF-8 bytes (GgufTensorBlockReader.MaxTensorNameBytes); ids stay under it too.
const maxNameBytes = 256;
const detailName = z.string().min(1).max(maxNameBytes);

// The investigation context in the URL. Each field is checked alone: an invalid one is dropped, the rest are kept.
export const modelLabSearchSchema = z.object({
	tab: z.enum(modelLabTabs).optional().catch(undefined),
	model: z.string().min(1).optional().catch(undefined),
	block: z
		.union([
			z.number().int().nonnegative().max(Number.MAX_SAFE_INTEGER),
			z.enum(["embedding", "output", "other", "stack", "mtp"]),
			// A chip of blocks "first-last": no leading zeros, first < last, both in the int range of a block index.
			z
				.string()
				.regex(/^(0|[1-9]\d{0,9})-(0|[1-9]\d{0,9})$/)
				.refine((token) => {
					const [first = 0, last = 0] = token.split("-").map(Number);
					return first < last && last <= 2_147_483_647;
				})
				.transform((token) => token as BlockRangeToken),
		])
		.optional()
		.catch(undefined),
	trace: z.guid().optional().catch(undefined),
	experiment: z.guid().optional().catch(undefined),
	// The training stepper's open saved run.
	run: z.guid().optional().catch(undefined),
	level: z.enum(["overview", "detail", "math"]).optional().catch(undefined),
	// The block detail's open operation (a template node id), tensor group (a component id) and tensor (a GGUF name).
	op: detailName.optional().catch(undefined),
	group: detailName.optional().catch(undefined),
	tensor: detailName
		.refine((name) => new TextEncoder().encode(name).length <= maxNameBytes)
		.optional()
		.catch(undefined),
});
export type ModelLabSearch = z.infer<typeof modelLabSearchSchema>;

// The fields to change: an omitted field is kept, one set to `undefined` is removed.
export type ModelLabPatch = Partial<ModelLabSearch>;
// A patch, or one computed from the latest search when it must not act on a stale one.
export type ModelLabUpdate = ModelLabPatch | ((previous: ModelLabSearch) => ModelLabPatch);
// What the block detail shows inside a block; another model or block drops it.
export type ArchitectureDetailPatch = Pick<ModelLabPatch, "op" | "group" | "tensor">;

// One transition for the route and the router-free page. Another model drops the block and the overlaid trace of the
// last one, and another model or block drops the detail's selection, before the patch applies, so a jump's own values
// survive. Defaults are stored as absent.
export function applyModelLabPatch(previous: ModelLabSearch, update: ModelLabUpdate): ModelLabSearch {
	const patch = typeof update === "function" ? update(previous) : update;
	const next: ModelLabSearch = { ...previous };
	const otherModel = "model" in patch && patch.model !== previous.model;
	if (otherModel) {
		delete next.block;
		delete next.trace;
	}
	if (otherModel || ("block" in patch && patch.block !== previous.block)) {
		delete next.op;
		delete next.group;
		delete next.tensor;
	}
	for (const [field, value] of Object.entries(patch) as [keyof ModelLabSearch, unknown][]) {
		if (value === undefined) {
			delete next[field];
		} else {
			Object.assign(next, { [field]: value });
		}
	}
	if (next.tab === defaultModelLabTab) {
		delete next.tab;
	}
	if (next.level === "overview") {
		delete next.level;
	}
	return next;
}

// Saved-run ids are GUIDs: a link may spell one in another case than the list does.
export function sameExperimentId(left: string | undefined, right: string | undefined): boolean {
	return left !== undefined && right !== undefined && left.toLowerCase() === right.toLowerCase();
}

// How long the Model Lab views reuse one read of the installed models.
export const installedModelsStaleMs = 30_000;

// Every installed llama.cpp GGUF: the page picker lists them, and Inspect compares a model with its repo's other files.
// The label is the one the chat pickers show (toModelOption's display name), else the model name.
export function installedGgufModels(
	items: readonly { modelName?: string | null; provider?: string | null; displayLabel?: string | null }[] | null | undefined,
): { modelName: string; provider: string; label: string }[] {
	return (items ?? []).flatMap((item) =>
		item.provider === LLAMACPP_PROVIDER && item.modelName
			? [{ modelName: item.modelName, provider: item.provider, label: item.displayLabel ?? item.modelName }]
			: [],
	);
}

// A jump from one Model Lab tab to another, with what the target tab should open.
export type ModelLabJump =
	| { tab: "architecture"; modelName: string; traceId: string; blockIndex: number }
	| { tab: "trace"; experimentId: string }
	| { tab: "nextToken"; modelName: string; prompt: string; disableThinking: boolean };

// What a jump writes to the URL. The Next token prompt is not part of it: it never enters the URL or history state.
export function jumpPatch(to: ModelLabJump): ModelLabPatch {
	if (to.tab === "architecture") {
		return { tab: to.tab, model: to.modelName, trace: to.traceId, block: to.blockIndex };
	}
	return to.tab === "trace" ? { tab: to.tab, experiment: to.experimentId } : { tab: to.tab, model: to.modelName };
}

// The page numbers every Next token jump, so the same jump twice applies twice. The panel applies each nonce once,
// during render, and a later edit in the panel wins over it.
export type ModelLabIntent<Tab extends ModelLabJump["tab"] = ModelLabJump["tab"]> = Extract<ModelLabJump, { tab: Tab }> & {
	nonce: number;
};
