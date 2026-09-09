import { describe, expect, it } from "vitest";

import {
	type DefinitionDraft,
	emptyDraft,
	firstDraftError,
	newKind,
	toBody,
	toDraft,
} from "@/features/training/models/DefinitionEditorModels";
import type { SampleLabel, TrainingDefinition } from "@/features/training/models/TrainingModels";

const definition: TrainingDefinition = {
	id: "def-1",
	name: "Tool calls",
	teacherModelName: "teacher:Q4",
	teacherOutputMode: "Constrained",
	systemInstructions: "Answer with a tool call.",
	toolNames: ["get_time"],
	sampleKinds: [{ kind: "single-tool-call", count: 12, label: "Good" }],
	holdoutFraction: 0.2,
	temperature: 0.5,
	baseSeed: "42",
	criticEnabled: false,
	criticModelName: null,
	definitionVersion: 3,
	version: 7,
	updatedAtUtc: 1,
};

function validDraft(overrides: Partial<DefinitionDraft> = {}): DefinitionDraft {
	return {
		...emptyDraft(),
		name: "Set",
		teacherModelName: "teacher:Q4",
		sampleKinds: [{ ...newKind(), kind: "a" }],
		...overrides,
	};
}

describe("DefinitionEditorModels", () => {
	it("gives every sample-kind row a distinct render key", () => {
		expect(newKind().id).not.toBe(newKind().id);
	});

	it("starts a new draft at a non-zero teacher temperature", () => {
		// A teacher sampled at 0 emits the same sample every time, so the CLR default is not usable as a UI default.
		expect(emptyDraft().temperature).toBeGreaterThan(0);
	});

	it("converts the stored hold-out fraction to a percentage and keeps a null seed as an empty field", () => {
		expect(toDraft(definition).holdoutPercent).toBe(20);
		expect(toDraft({ ...definition, baseSeed: null }).baseSeed).toBe("");
	});

	it("accepts a complete draft", () => {
		expect(firstDraftError(validDraft())).toBeNull();
	});

	it.each([
		["name", validDraft({ name: "  " })],
		["teacher", validDraft({ teacherModelName: "" })],
		["holdout", validDraft({ holdoutPercent: 4 })],
		["holdout", validDraft({ holdoutPercent: 31 })],
		["temperature", validDraft({ temperature: 2.1 })],
		["sampleKinds", validDraft({ sampleKinds: [] })],
		["sampleKind", validDraft({ sampleKinds: [{ ...newKind(), kind: "x".repeat(65) }] })],
		["sampleCount", validDraft({ sampleKinds: [{ ...newKind(), kind: "a", count: 0 }] })],
		["totalSamples", validDraft({ sampleKinds: [{ ...newKind(), kind: "a", count: 2001 }] })],
		[
			"duplicateKind",
			validDraft({
				sampleKinds: [
					{ ...newKind(), kind: "a" },
					{ ...newKind(), kind: " a " },
				],
			}),
		],
		["baseSeed", validDraft({ baseSeed: "12x" })],
		["criticModel", validDraft({ criticEnabled: true, criticModelName: "" })],
	])("rejects the draft with %s", (expected, draft) => {
		expect(firstDraftError(draft)).toBe(expected);
	});

	// The kind+label key is joined with a NUL, which no kind name can contain. A printable separator (or none) would
	// let one row's kind absorb the separator and the next row's label, reporting two distinct rows as duplicates.
	// The labels are cast because the collision needs kind/label texts the SampleLabel union does not offer.
	it("separates kind from label so distinct rows never collide into a false duplicate", () => {
		const draft = validDraft({
			sampleKinds: [
				{ ...newKind(), kind: "a b", label: "c" as SampleLabel },
				{ ...newKind(), kind: "a", label: "b c" as SampleLabel },
			],
		});
		expect(firstDraftError(draft)).toBeNull();
	});

	it("still reports a genuine duplicate kind and label", () => {
		const draft = validDraft({
			sampleKinds: [
				{ ...newKind(), kind: "a b", label: "c" as SampleLabel },
				{ ...newKind(), kind: "a b", label: "c" as SampleLabel },
			],
		});
		expect(firstDraftError(draft)).toBe("duplicateKind");
	});

	it("sends tool names only, the hold-out as a fraction, and trims the seed", () => {
		const body = toBody(validDraft({ toolNames: ["get_time"], holdoutPercent: 20, baseSeed: " 42 " }));
		expect(body.tools).toEqual([{ name: "get_time" }]);
		expect(body.holdoutFraction).toBe(0.2);
		expect(body.baseSeed).toBe("42");
	});

	it("drops the critic model when the critic is off", () => {
		expect(toBody(validDraft({ criticEnabled: false, criticModelName: "critic" })).criticModelName).toBeNull();
	});

	it("sends no seed when the field is blank", () => {
		expect(toBody(validDraft({ baseSeed: "   " })).baseSeed).toBeNull();
	});
});
