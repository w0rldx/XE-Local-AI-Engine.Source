import type { XeLocalAiEngineClientServicesTrainingDatasetsDatasetDefinitionBodyV1 as DatasetDefinitionBody } from "@/core/api/generated";
import type { SampleLabel, TeacherOutputMode, TrainingDefinition } from "@/features/training/models/TrainingModels";

// Client-side bounds mirror DatasetDefinitionService.Validate. They are a courtesy, not the gate: the server
// re-validates everything and its rejection is surfaced through the same error slot.
export const minHoldoutPercent = 5;
export const maxHoldoutPercent = 30;
export const maxSampleKindLength = 64;
const maxTargetSampleCount = 2000;
// The backend record leaves Temperature at the CLR default (0); a teacher sampled at 0 emits the same sample every
// time, so a NEW definition starts at 0.7 and an existing one keeps whatever it was saved with.
const defaultTemperature = 0.7;

export interface SampleKindDraft {
	/** Render key only. A row has no natural identity — its kind name is the thing being typed. */
	id: string;
	kind: string;
	count: number;
	label: SampleLabel;
}

let nextKindId = 0;

export function newKind(): SampleKindDraft {
	nextKindId += 1;
	return { id: `kind-${nextKindId}`, kind: "", count: 10, label: "Good" };
}

export interface DefinitionDraft {
	name: string;
	teacherModelName: string;
	teacherOutputMode: TeacherOutputMode;
	systemInstructions: string;
	toolNames: string[];
	sampleKinds: SampleKindDraft[];
	holdoutPercent: number;
	temperature: number;
	baseSeed: string;
	criticEnabled: boolean;
	criticModelName: string;
}

export function emptyDraft(): DefinitionDraft {
	return {
		name: "",
		teacherModelName: "",
		teacherOutputMode: "Constrained",
		systemInstructions: "",
		toolNames: [],
		sampleKinds: [newKind()],
		holdoutPercent: 10,
		temperature: defaultTemperature,
		baseSeed: "",
		criticEnabled: false,
		criticModelName: "",
	};
}

export function toDraft(definition: TrainingDefinition): DefinitionDraft {
	return {
		name: definition.name,
		teacherModelName: definition.teacherModelName,
		teacherOutputMode: definition.teacherOutputMode,
		systemInstructions: definition.systemInstructions,
		toolNames: [...definition.toolNames],
		sampleKinds: definition.sampleKinds.map((kind) => ({ ...newKind(), ...kind })),
		holdoutPercent: Math.round(definition.holdoutFraction * 100),
		temperature: definition.temperature,
		baseSeed: definition.baseSeed ?? "",
		criticEnabled: definition.criticEnabled,
		criticModelName: definition.criticModelName ?? "",
	};
}

/** First validation failure, as a key into the dialog's message map, or null when the draft is submittable. */
export function firstDraftError(draft: DefinitionDraft): string | null {
	if (draft.name.trim().length === 0) {
		return "name";
	}
	if (draft.teacherModelName.trim().length === 0) {
		return "teacher";
	}
	if (draft.holdoutPercent < minHoldoutPercent || draft.holdoutPercent > maxHoldoutPercent) {
		return "holdout";
	}
	if (draft.temperature < 0 || draft.temperature > 2) {
		return "temperature";
	}
	if (draft.sampleKinds.length === 0) {
		return "sampleKinds";
	}
	let total = 0;
	for (const kind of draft.sampleKinds) {
		if (kind.kind.trim().length === 0 || kind.kind.trim().length > maxSampleKindLength) {
			return "sampleKind";
		}
		if (!Number.isInteger(kind.count) || kind.count < 1) {
			return "sampleCount";
		}
		total += kind.count;
	}
	if (total > maxTargetSampleCount) {
		return "totalSamples";
	}
	// NUL separator, spelled as an escape: no kind name can contain it, so "kind + label" cannot collide with a
	// different kind whose name happens to end in the separator plus a label.
	const pairs = new Set(draft.sampleKinds.map((kind) => `${kind.kind.trim()}\u0000${kind.label}`));
	if (pairs.size !== draft.sampleKinds.length) {
		return "duplicateKind";
	}
	// The seed is a 64-bit integer carried as a string (a JSON number would lose precision above 2^53).
	if (draft.baseSeed.trim().length > 0 && !/^-?\d+$/.test(draft.baseSeed.trim())) {
		return "baseSeed";
	}
	if (draft.criticEnabled && draft.criticModelName.trim().length === 0) {
		return "criticModel";
	}
	return null;
}

/** Only the tool NAME is sent — the server re-snapshots description, schema and approval from the live catalog. */
export function toBody(draft: DefinitionDraft): DatasetDefinitionBody {
	return {
		teacherModelName: draft.teacherModelName.trim(),
		teacherOutputMode: draft.teacherOutputMode,
		systemInstructions: draft.systemInstructions,
		tools: draft.toolNames.map((name) => ({ name })),
		sampleKinds: draft.sampleKinds.map((kind) => ({ kind: kind.kind.trim(), count: kind.count, label: kind.label })),
		holdoutFraction: draft.holdoutPercent / 100,
		temperature: draft.temperature,
		baseSeed: draft.baseSeed.trim().length === 0 ? null : draft.baseSeed.trim(),
		criticEnabled: draft.criticEnabled,
		criticModelName: draft.criticEnabled ? draft.criticModelName.trim() : null,
	};
}
