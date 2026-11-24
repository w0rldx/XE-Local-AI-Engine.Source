import type { TFunction } from "i18next";

import { type BlockKind, blockKindLabels } from "@/core/models/inspect/GgufInspectInsights";
import {
	type ArchitectureBlock,
	type ArchitectureSelection,
	architectureBlockKind,
	componentLabel,
} from "@/features/modelLab/models/ModelLabArchitecture";
import { type ArchitectureHierarchy, type BlockChip, chipFor } from "@/features/modelLab/models/ModelLabHierarchy";

function kindShortLabels(t: TFunction): Record<BlockKind, string> {
	return {
		linearAttention: t("pages.modelLab.architecture.hierarchy.kindShort.linearAttention", "linear"),
		fullAttention: t("pages.modelLab.architecture.hierarchy.kindShort.fullAttention", "full"),
		mtp: t("pages.modelLab.architecture.hierarchy.kindShort.mtp", "MTP"),
		other: t("pages.modelLab.architecture.hierarchy.kindShort.other", "other"),
	};
}

// "0–3 · 3 linear + 1 full" for a repetition of a unit, "0–15 · Full attention ×16" for one run.
export function chipLabel(t: TFunction, chip: BlockChip): string {
	const [only] = chip.runs;
	const runs =
		chip.runs.length === 1 && only
			? t("pages.modelLab.architecture.hierarchy.chipRun", "{{kind}} ×{{count}}", {
					kind: blockKindLabels(t)[architectureBlockKind(only.kind)],
					count: only.count,
				})
			: chip.runs
					.map((run) =>
						t("pages.modelLab.architecture.hierarchy.chipUnitRun", "{{count}} {{kind}}", {
							count: run.count,
							kind: kindShortLabels(t)[architectureBlockKind(run.kind)],
						}),
					)
					.join(" + ");
	return `${chip.first}–${chip.last} · ${runs}`;
}

// "Block 5: Full attention, traced, routed": a block's name with the markers the laid-over run gives it.
export function blockCellLabel(
	t: TFunction,
	block: ArchitectureBlock,
	tracedBlocks: ReadonlySet<number> | null,
	routedBlocks: ReadonlySet<number> | null,
): string {
	const values = { index: block.index, kind: blockKindLabels(t)[architectureBlockKind(block.kind)] };
	if (block.kind === "mtp") {
		return t("pages.modelLab.architecture.blockUntraceable", "Block {{index}}: {{kind}}, not traceable", values);
	}
	const traced = tracedBlocks?.has(block.index) === true;
	if (routedBlocks?.has(block.index) === true) {
		return traced
			? t("pages.modelLab.architecture.blockTracedRouted", "Block {{index}}: {{kind}}, traced, routed", values)
			: t("pages.modelLab.architecture.blockRouted", "Block {{index}}: {{kind}}, routed", values);
	}
	return traced
		? t("pages.modelLab.architecture.blockTraced", "Block {{index}}: {{kind}}, traced", values)
		: t("pages.models.inspect.blockCell", "Block {{index}}: {{kind}}", values);
}

// "2 traced", "3 routed" for a chip's blocks; a count only for a marker the laid-over run has at all.
export function chipCounts(
	t: TFunction,
	chip: BlockChip,
	tracedBlocks: ReadonlySet<number> | null,
	routedBlocks: ReadonlySet<number> | null,
): string[] {
	const count = (marked: ReadonlySet<number>) => chip.blocks.filter((block) => marked.has(block.index)).length;
	return [
		tracedBlocks && t("pages.modelLab.architecture.hierarchy.chipTraced", "{{count}} traced", { count: count(tracedBlocks) }),
		routedBlocks && t("pages.modelLab.architecture.hierarchy.chipRouted", "{{count}} routed", { count: count(routedBlocks) }),
	].filter((text): text is string => typeof text === "string");
}

export interface Crumb {
	label: string;
	selection: ArchitectureSelection | null;
}

// Model › Blocks › chip › Block; a one-block chip's block skips the chip, an MTP block sits under MTP.
export function breadcrumbFor(t: TFunction, hierarchy: ArchitectureHierarchy, selection: ArchitectureSelection | null): Crumb[] {
	const model: Crumb = { label: t("pages.modelLab.architecture.hierarchy.model", "Model"), selection: null };
	const blocks: Crumb = { label: t("pages.modelLab.architecture.hierarchy.blocks", "Blocks"), selection: "stack" };
	const mtp: Crumb = { label: t("pages.models.inspect.blockKinds.mtp", "MTP"), selection: "mtp" };
	if (selection === null) {
		return [model];
	}
	if (typeof selection === "number") {
		const block: Crumb = {
			label: t("pages.modelLab.architecture.hierarchy.block", "Block {{index}}", { index: selection }),
			selection,
		};
		if (hierarchy.mtp.some((candidate) => candidate.index === selection)) {
			return [model, mtp, block];
		}
		const chip = chipFor(hierarchy, selection);
		return chip && chip.blocks.length > 1
			? [model, blocks, { label: `${chip.first}–${chip.last}`, selection: chip.selection }, block]
			: [model, blocks, block];
	}
	switch (selection) {
		case "embedding":
		case "output":
		case "other":
			return [model, { label: componentLabel(t, selection), selection }];
		case "stack":
			return [model, blocks];
		case "mtp":
			return [model, mtp];
		default:
			return [model, blocks, { label: selection.replace("-", "–"), selection }];
	}
}
