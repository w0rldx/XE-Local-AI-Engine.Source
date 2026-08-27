import { describe, expect, it } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";
import {
	connectionToFormValues,
	createModelRowIds,
	emptyFormValues,
	emptyModelDraft,
	type ExternalProviderConnectionDto,
	type ExternalProviderFormState,
	formReducer,
	initialFormState,
	parseConnectionsConflict,
	toSaveRequestBody,
} from "@/features/external-providers/models/ExternalProviderFormState";
import type { ExternalProviderModelDraft } from "@/features/external-providers/models/ExternalProviderModel";

function storedConnection(overrides: Partial<ExternalProviderConnectionDto> = {}): ExternalProviderConnectionDto {
	return {
		id: "unsloth-box",
		displayName: "Unsloth box",
		baseUrl: "http://127.0.0.1:8080/v1",
		locality: "Local",
		hasApiKey: true,
		timeoutSeconds: 120,
		models: [
			{
				wireId: "qwen3-27b",
				modelId: "ext:unsloth-box/qwen3-27b",
				displayName: "Qwen3 27B",
				contextLength: 32_768,
				supportsTools: true,
				supportsVision: false,
				supportsReasoning: true,
				supportsReasoningEffort: true,
				defaultReasoningEffort: "medium",
			},
		],
		...overrides,
	};
}

function stateWith(values: ExternalProviderFormState["values"]): ExternalProviderFormState {
	return { ...initialFormState, values, modelRowIds: createModelRowIds(values) };
}

// The editor always keeps at least one row, so a missing first row is a broken fixture rather than a case to handle.
function firstModel(values: ExternalProviderFormState["values"]): ExternalProviderModelDraft {
	const [model] = values.models;
	if (model === undefined) {
		throw new Error("expected at least one model row");
	}
	return model;
}

describe("connectionToFormValues", () => {
	it("loads a stored connection with the key blank and no pending removal", () => {
		const values = connectionToFormValues(storedConnection());

		expect(values.apiKey).toBe("");
		expect(values.clearApiKey).toBe(false);
		expect(values.connectionId).toBe("unsloth-box");
		expect(values.locality).toBe("Local");
		expect(values.timeoutSeconds).toBe("120");
	});

	it("loads the model rows with their declared capabilities and numeric fields as text", () => {
		const [model] = connectionToFormValues(storedConnection()).models;

		expect(model).toMatchObject({
			wireId: "qwen3-27b",
			displayName: "Qwen3 27B",
			contextLength: "32768",
			supportsTools: true,
			supportsReasoning: true,
			supportsReasoningEffort: true,
			defaultReasoningEffort: "medium",
		});
	});

	it("gives a connection with no registered models one blank row to type into", () => {
		expect(connectionToFormValues(storedConnection({ models: [] })).models).toHaveLength(1);
	});
});

describe("toSaveRequestBody — API key contract", () => {
	it("omits apiKey entirely when the field is untouched, which preserves the stored key", () => {
		const body = toSaveRequestBody(connectionToFormValues(storedConnection()), "rev-1");

		expect("apiKey" in body).toBe(false);
		expect(body.clearApiKey).toBeUndefined();
	});

	it("omits apiKey for a whitespace-only field rather than sending a blank string", () => {
		const values = { ...connectionToFormValues(storedConnection()), apiKey: "   " };

		expect("apiKey" in toSaveRequestBody(values, "rev-1")).toBe(false);
	});

	it("sends the typed key verbatim when one was entered", () => {
		const values = { ...connectionToFormValues(storedConnection()), apiKey: "sk-unsloth-abc" };

		expect(toSaveRequestBody(values, "rev-1").apiKey).toBe("sk-unsloth-abc");
	});

	it("sends clearApiKey only after the explicit removal action", () => {
		const removed = formReducer(stateWith(connectionToFormValues(storedConnection())), { type: "removeApiKey" });
		const body = toSaveRequestBody(removed.values, "rev-1");

		expect(body.clearApiKey).toBe(true);
		expect("apiKey" in body).toBe(false);
	});

	it("carries the expected revision so a lost race is answered with a 409 rather than a silent overwrite", () => {
		expect(toSaveRequestBody(connectionToFormValues(storedConnection()), "rev-7").expectedRevision).toBe("rev-7");
	});
});

describe("toSaveRequestBody — model rows", () => {
	it("drops rows with no backing model id", () => {
		const loaded = connectionToFormValues(storedConnection());
		const values = { ...loaded, models: [...loaded.models, emptyModelDraft] };

		expect(toSaveRequestBody(values, "rev-1").models.map((model) => model.wireId)).toEqual(["qwen3-27b"]);
	});

	it("drops the default effort when the model does not declare effort support", () => {
		const loaded = connectionToFormValues(storedConnection());
		const withoutEffort = { ...loaded, models: [{ ...firstModel(loaded), supportsReasoningEffort: false }] };
		const [model] = toSaveRequestBody(withoutEffort, "rev-1").models;

		expect(model?.supportsReasoningEffort).toBe(false);
		expect(model?.defaultReasoningEffort).toBeUndefined();
	});

	it("drops both the effort support flag and its default when reasoning itself is unchecked", () => {
		const loaded = connectionToFormValues(storedConnection());
		const withoutReasoning = { ...loaded, models: [{ ...firstModel(loaded), supportsReasoning: false }] };
		const [model] = toSaveRequestBody(withoutReasoning, "rev-1").models;

		expect(model?.supportsReasoning).toBe(false);
		expect(model?.supportsReasoningEffort).toBe(false);
		expect(model?.defaultReasoningEffort).toBeUndefined();
	});

	it("sends a blank optional display name, context length and timeout as undefined", () => {
		const loaded = connectionToFormValues(storedConnection());
		const blanked = {
			...loaded,
			timeoutSeconds: "",
			models: [{ ...firstModel(loaded), displayName: "", contextLength: "" }],
		};
		const body = toSaveRequestBody(blanked, "rev-1");

		expect(body.timeoutSeconds).toBeUndefined();
		expect(body.models[0]?.displayName).toBeUndefined();
		expect(body.models[0]?.contextLength).toBeUndefined();
	});
});

describe("parseConnectionsConflict", () => {
	function conflict(body: unknown): ApiError {
		return new ApiError(409, body as ProblemDetails);
	}

	it("recovers the stored configuration a 409 carries", () => {
		const parsed = parseConnectionsConflict(conflict({ revision: "rev-9", connections: [storedConnection()] }));

		expect(parsed?.revision).toBe("rev-9");
		expect(parsed?.connections).toHaveLength(1);
	});

	it("treats a 409 without connections as an empty configuration rather than a parse failure", () => {
		expect(parseConnectionsConflict(conflict({ revision: "rev-9" }))?.connections).toEqual([]);
	});

	it("returns null for a 409 that is not a connections response, so it reports as an ordinary error", () => {
		expect(parseConnectionsConflict(conflict({ title: "Conflict", detail: "something else" }))).toBeNull();
	});

	it("returns null for any other status and for a non-API error", () => {
		expect(parseConnectionsConflict(new ApiError(400, { revision: "rev-9" } as unknown as ProblemDetails))).toBeNull();
		expect(parseConnectionsConflict(new Error("boom"))).toBeNull();
	});
});

describe("formReducer", () => {
	it("clears the typed key when removal is requested — the request can only carry one instruction", () => {
		const typed = formReducer(initialFormState, { type: "setField", field: "apiKey", value: "sk-abc" });
		const removed = formReducer(typed, { type: "removeApiKey" });

		expect(removed.values.apiKey).toBe("");
		expect(removed.values.clearApiKey).toBe(true);
	});

	it("lets the operator take back a removal", () => {
		const removed = formReducer(initialFormState, { type: "removeApiKey" });

		expect(formReducer(removed, { type: "keepApiKey" }).values.clearApiKey).toBe(false);
	});

	it("keeps one model row when the last one is removed", () => {
		const state = formReducer(initialFormState, { type: "removeModel", index: 0, replacementRowId: "row-x" });

		expect(state.values.models).toHaveLength(1);
		expect(state.modelRowIds).toEqual(["row-x"]);
	});

	it("fills the blank row when a probed model is picked, pre-filling the reported context length", () => {
		const state = formReducer(initialFormState, {
			type: "addProbedModel",
			wireId: "qwen3-27b",
			contextLength: 32_768,
			rowId: "row-1",
		});

		expect(state.values.models).toHaveLength(1);
		expect(state.values.models[0]).toMatchObject({ wireId: "qwen3-27b", contextLength: "32768" });
	});

	it("appends a probed model when every row is already filled", () => {
		const first = formReducer(initialFormState, { type: "addProbedModel", wireId: "a", contextLength: null, rowId: "row-1" });
		const second = formReducer(first, { type: "addProbedModel", wireId: "b", contextLength: null, rowId: "row-2" });

		expect(second.values.models.map((model) => model.wireId)).toEqual(["a", "b"]);
		expect(second.modelRowIds).toHaveLength(2);
	});

	it("ignores a probed model that is already registered, case-insensitively", () => {
		const first = formReducer(initialFormState, { type: "addProbedModel", wireId: "a", contextLength: null, rowId: "row-1" });
		const again = formReducer(first, { type: "addProbedModel", wireId: "A", contextLength: null, rowId: "row-2" });

		expect(again).toBe(first);
	});

	it("leaves the context length blank when the probe reported none", () => {
		const state = formReducer(initialFormState, {
			type: "addProbedModel",
			wireId: "qwen3-27b",
			contextLength: undefined,
			rowId: "row-1",
		});

		expect(state.values.models[0]?.contextLength).toBe("");
	});

	it("toggles one model's capability flag without touching its neighbours", () => {
		const added = formReducer(initialFormState, { type: "addModel", rowId: "row-2" });
		const toggled = formReducer(added, { type: "toggleModelFlag", index: 1, flag: "supportsTools" });

		expect(toggled.values.models[0]?.supportsTools).toBe(false);
		expect(toggled.values.models[1]?.supportsTools).toBe(true);
	});

	it("clears the touched and submitted flags on reset", () => {
		const dirty = formReducer(formReducer(initialFormState, { type: "submit" }), {
			type: "touchField",
			field: "baseUrl",
		});
		const reset = formReducer(dirty, { type: "reset", values: emptyFormValues, rowIds: ["row-1"] });

		expect(reset.submitted).toBe(false);
		expect(reset.touched).toEqual({});
	});
});
