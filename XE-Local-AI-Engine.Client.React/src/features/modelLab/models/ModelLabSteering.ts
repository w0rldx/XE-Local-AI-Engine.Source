import type { TFunction } from "i18next";

// The starter set an operator edits before a run: matched pairs of plain sentences, one feeling apart. Lives in this route's chunk.
const starterPositivePrompts = [
	"I love this film. It made me so happy and I smiled the whole time.",
	"The meal was wonderful, the staff were kind and we had a great evening.",
	"What a beautiful day. Everything went well and I feel fantastic.",
	"This phone is excellent. The battery is great and I enjoy using it.",
	"My new job is a delight. My colleagues are friendly and the work is rewarding.",
	"The hotel was lovely, the room was clean and the view was amazing.",
	"The concert was brilliant. The band played perfectly and the crowd was joyful.",
	"Our holiday was perfect. The weather was warm and everyone was cheerful.",
];

const starterNegativePrompts = [
	"I hate this film. It made me so miserable and I frowned the whole time.",
	"The meal was awful, the staff were rude and we had a terrible evening.",
	"What a horrible day. Everything went wrong and I feel dreadful.",
	"This phone is useless. The battery is terrible and I regret using it.",
	"My new job is a nightmare. My colleagues are hostile and the work is pointless.",
	"The hotel was disgusting, the room was filthy and the view was depressing.",
	"The concert was dreadful. The band played badly and the crowd was angry.",
	"Our holiday was a disaster. The weather was cold and everyone was upset.",
];

const starterTestPrompts = [
	"Write one sentence about the weather today.",
	"Describe a restaurant you visited recently in two sentences.",
	"What do you think about Mondays? Answer briefly.",
	"Tell me about a film in two sentences.",
	"Describe a neighbour in one sentence.",
	"How was the trip? Answer as a traveller, in two sentences.",
];

export type LayerKind = "full" | "linear" | "unknown";

export function layerKindLabel(t: TFunction, kind: LayerKind): string {
	switch (kind) {
		case "full":
			return t("pages.modelLab.steer.kindFull", "full attention");
		case "linear":
			return t("pages.modelLab.steer.kindLinear", "linear attention");
		default:
			return t("pages.modelLab.steer.kindUnknown", "kind not stated");
	}
}

// The server's own bounds (ModelLabTraceSpec), so a run it would refuse does not get as far as asking.
const maxContrastPrompts = 32;
const maxContrastCharacters = 2000;
const maxTestPrompts = 8;
const maxTestCharacters = 8000;
const minScale = 0.001;
const maxScale = 20;

// What the form holds. It lives with the job-kind switch, so edits outlast it the way the trace prompt does.
export interface SteeringDraft {
	positive: string;
	negative: string;
	tests: string;
	layer: number | string;
	scale: number | string;
}

export const starterSteeringDraft: SteeringDraft = {
	positive: starterPositivePrompts.join("\n"),
	negative: starterNegativePrompts.join("\n"),
	tests: starterTestPrompts.join("\n"),
	layer: "",
	scale: 1,
};

function fits(lines: string[], maxLines: number, maxCharacters: number): boolean {
	return lines.length > 0 && lines.length <= maxLines && lines.every((line) => line.length <= maxCharacters);
}

// Why the server would refuse this draft, or null.
export function steeringProblem(t: TFunction, draft: SteeringDraft): string | null {
	if (
		!fits(promptLines(draft.positive), maxContrastPrompts, maxContrastCharacters) ||
		!fits(promptLines(draft.negative), maxContrastPrompts, maxContrastCharacters)
	) {
		return t("pages.modelLab.steer.problemSets", "Each set takes 1 to {{lines}} prompts of up to {{characters}} characters.", {
			lines: maxContrastPrompts,
			characters: maxContrastCharacters,
		});
	}
	if (!fits(promptLines(draft.tests), maxTestPrompts, maxTestCharacters)) {
		return t("pages.modelLab.steer.problemTests", "Use 1 to {{lines}} test prompts of up to {{characters}} characters.", {
			lines: maxTestPrompts,
			characters: maxTestCharacters,
		});
	}
	const scale = typeof draft.scale === "number" ? Math.abs(draft.scale) : 0;
	if (scale < minScale || scale > maxScale) {
		return t("pages.modelLab.steer.problemScale", "The scale must be between {{min}} and {{max}}, in either sign.", {
			min: minScale,
			max: maxScale,
		});
	}
	return null;
}

// One prompt per line, in the form's text boxes.
export function promptLines(text: string): string[] {
	return text
		.split("\n")
		.map((line) => line.trim())
		.filter((line) => line.length > 0);
}

// From the checkpoint's own config; the kind on a saved row comes from the block itself.
export function layerKindOf(layerTypes: string[] | null | undefined, layer: number): LayerKind {
	const type = layerTypes?.[layer];
	if (type === undefined) {
		return "unknown";
	}
	return type === "linear_attention" ? "linear" : "full";
}

// What a saved row calls a block's kind.
export function savedLayerKind(letter: string | null | undefined): LayerKind {
	if (letter === "F") {
		return "full";
	}
	return letter === "L" ? "linear" : "unknown";
}
