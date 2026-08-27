import { ApiError } from "@/core/api/errors/ApiError";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import type {
	XeLocalAiEngineClientEndpointsExternalProvidersV1ExternalProviderConnectionResponse,
	XeLocalAiEngineClientEndpointsExternalProvidersV1ExternalProviderConnectionsResponse,
	XeLocalAiEngineClientEndpointsExternalProvidersV1SaveExternalProviderConnectionRequest,
} from "@/core/api/generated";
import type { ReasoningEffort } from "@/core/models/ReasoningEffort";
import {
	type ExternalProviderFormValues,
	type ExternalProviderLocality,
	type ExternalProviderModelDraft,
	parseLocality,
} from "@/features/external-providers/models/ExternalProviderModel";

// Local aliases for the generated wire types (the backend OpenAPI is the single source of truth); the generated names
// are unreadable at every use site.
export type ExternalProviderConnectionDto = XeLocalAiEngineClientEndpointsExternalProvidersV1ExternalProviderConnectionResponse;
export type ExternalProviderConnectionsDto = XeLocalAiEngineClientEndpointsExternalProvidersV1ExternalProviderConnectionsResponse;
type SaveConnectionBody = XeLocalAiEngineClientEndpointsExternalProvidersV1SaveExternalProviderConnectionRequest;

export function errorMessage(error: unknown): string {
	return apiErrorMessage(error, "Unexpected external provider error");
}

const HTTP_CONFLICT = 409;

/**
 * Recovers the whole current configuration a 409 carries.
 *
 * Save and delete answer a lost revision race with what is ACTUALLY stored rather than a bare status, so the editor
 * can re-render the real state instead of refetching and hoping. The shared axios interceptor wraps every non-2xx body
 * into an `ApiError.apiProblemDetails` untouched, so the connections response is sitting there — recognized by its
 * `revision`, which no ProblemDetails carries. Returns null for anything else, which the caller then reports as an
 * ordinary error.
 */
export function parseConnectionsConflict(error: unknown): ExternalProviderConnectionsDto | null {
	if (!(error instanceof ApiError) || error.statusCode !== HTTP_CONFLICT) {
		return null;
	}
	const body = error.apiProblemDetails as unknown as Record<string, unknown> | undefined;
	const revision = body?.["revision"];
	if (typeof revision !== "string") {
		return null;
	}
	const connections = body?.["connections"];
	return {
		revision,
		connections: Array.isArray(connections) ? (connections as ExternalProviderConnectionDto[]) : [],
	};
}

export const emptyModelDraft: ExternalProviderModelDraft = {
	wireId: "",
	displayName: "",
	contextLength: "",
	supportsTools: false,
	supportsVision: false,
	supportsReasoning: false,
	supportsReasoningEffort: false,
	defaultReasoningEffort: "",
};

export const emptyFormValues: ExternalProviderFormValues = {
	connectionId: "",
	displayName: "",
	baseUrl: "",
	// A new connection starts declared Cloud: the safe half of the flag, so an operator who never touches the control
	// gets the restrictive gating rather than full local trust by default.
	locality: "Cloud",
	apiKey: "",
	clearApiKey: false,
	timeoutSeconds: "",
	models: [emptyModelDraft],
};

function numberToField(value: number | null | undefined): string {
	return value === null || value === undefined ? "" : String(value);
}

// Loads a stored connection into the editor. The API key is never returned by the backend, so it always loads blank
// and `clearApiKey` always loads false — the operator has to ask for removal again on every fresh edit.
export function connectionToFormValues(connection: ExternalProviderConnectionDto): ExternalProviderFormValues {
	const models = (connection.models ?? []).map((model) => ({
		wireId: model.wireId,
		displayName: model.displayName ?? "",
		contextLength: numberToField(model.contextLength),
		supportsTools: model.supportsTools,
		supportsVision: model.supportsVision,
		supportsReasoning: model.supportsReasoning,
		supportsReasoningEffort: model.supportsReasoningEffort,
		defaultReasoningEffort: (model.defaultReasoningEffort ?? "") as ReasoningEffort | "",
	}));

	return {
		connectionId: connection.id,
		displayName: connection.displayName,
		baseUrl: connection.baseUrl,
		locality: parseLocality(connection.locality),
		apiKey: "",
		clearApiKey: false,
		timeoutSeconds: numberToField(connection.timeoutSeconds),
		// Always leave one row to type into, the same affordance the Azure deployment list gives.
		models: models.length > 0 ? models : [emptyModelDraft],
	};
}

function fieldToNumber(value: string): number | undefined {
	const trimmed = value.trim();
	return trimmed.length > 0 ? Number(trimmed) : undefined;
}

/**
 * Builds the save body. Three contracts live here and nowhere else:
 *
 * - A blank API-key field omits `apiKey` entirely, which PRESERVES the stored key. Sending `""` would not clear it,
 *   it would just fail the store's own merge differently, so the field is never sent empty.
 * - `clearApiKey: true` is the only route back to a keyless connection, and it is set only by the explicit
 *   "Remove key" action.
 * - A model's default reasoning effort is dropped unless the model declares BOTH reasoning and effort support, so
 *   unchecking either box cannot leave a stale effort behind in the store.
 */
export function toSaveRequestBody(values: ExternalProviderFormValues, expectedRevision: string | undefined): SaveConnectionBody {
	const apiKey = values.apiKey.trim();
	return {
		displayName: values.displayName.trim(),
		baseUrl: values.baseUrl.trim(),
		locality: values.locality,
		...(apiKey.length > 0 ? { apiKey: values.apiKey } : {}),
		...(values.clearApiKey ? { clearApiKey: true } : {}),
		timeoutSeconds: fieldToNumber(values.timeoutSeconds),
		models: values.models
			.filter((model) => model.wireId.trim().length > 0)
			.map((model) => {
				const displayName = model.displayName.trim();
				const effortDeclared = model.supportsReasoning && model.supportsReasoningEffort;
				return {
					wireId: model.wireId.trim(),
					displayName: displayName.length > 0 ? displayName : undefined,
					contextLength: fieldToNumber(model.contextLength),
					supportsTools: model.supportsTools,
					supportsVision: model.supportsVision,
					supportsReasoning: model.supportsReasoning,
					supportsReasoningEffort: effortDeclared,
					defaultReasoningEffort:
						effortDeclared && model.defaultReasoningEffort.length > 0 ? model.defaultReasoningEffort : undefined,
				};
			}),
		expectedRevision,
	};
}

let externalRowSequence = 0;
export function nextExternalRowId(prefix: string): string {
	externalRowSequence += 1;
	return `${prefix}-${externalRowSequence}`;
}

export function createModelRowIds(values: ExternalProviderFormValues): string[] {
	return Array.from({ length: values.models.length }, () => nextExternalRowId("external-model"));
}

// Values, the per-field "touched" map, the submit flag and the row keys always reset together (a connection is
// loaded, or a save commits), so one dispatch replaces all of them — the same grouping the cloud-settings reducer uses
// and for the same reason.
export interface ExternalProviderFormState {
	values: ExternalProviderFormValues;
	touched: Partial<Record<keyof ExternalProviderFormValues, true>>;
	submitted: boolean;
	modelRowIds: string[];
}

// The boolean capability flags a model row carries, named as one type so the toggle action cannot address a
// non-boolean field.
export type ExternalProviderModelFlag = "supportsTools" | "supportsVision" | "supportsReasoning" | "supportsReasoningEffort";

export type ExternalProviderFormAction =
	| { type: "reset"; values: ExternalProviderFormValues; rowIds: string[] }
	| { type: "setField"; field: "connectionId" | "displayName" | "baseUrl" | "apiKey" | "timeoutSeconds"; value: string }
	| { type: "setLocality"; value: ExternalProviderLocality }
	| { type: "removeApiKey" }
	| { type: "keepApiKey" }
	| { type: "addModel"; rowId: string }
	| { type: "removeModel"; index: number; replacementRowId: string }
	| { type: "setModelField"; index: number; field: "wireId" | "displayName" | "contextLength"; value: string }
	| { type: "toggleModelFlag"; index: number; flag: ExternalProviderModelFlag }
	| { type: "setModelEffort"; index: number; value: ReasoningEffort | "" }
	| { type: "addProbedModel"; wireId: string; contextLength: number | null | undefined; rowId: string }
	| { type: "touchField"; field: keyof ExternalProviderFormValues }
	| { type: "submit" };

export const initialFormState: ExternalProviderFormState = {
	values: emptyFormValues,
	touched: {},
	submitted: false,
	modelRowIds: createModelRowIds(emptyFormValues),
};

function mapModel(
	state: ExternalProviderFormState,
	index: number,
	update: (model: ExternalProviderModelDraft) => ExternalProviderModelDraft,
): ExternalProviderFormState {
	const models = state.values.models.map((model, modelIndex) => (modelIndex === index ? update(model) : model));
	return { ...state, values: { ...state.values, models } };
}

export function formReducer(state: ExternalProviderFormState, action: ExternalProviderFormAction): ExternalProviderFormState {
	switch (action.type) {
		case "reset":
			return { values: action.values, touched: {}, submitted: false, modelRowIds: action.rowIds };
		case "setField":
			return { ...state, values: { ...state.values, [action.field]: action.value } };
		case "setLocality":
			return { ...state, values: { ...state.values, locality: action.value } };
		// Asking for removal also empties the field: a typed key and a removal request are contradictory instructions,
		// and the request can only carry one of them.
		case "removeApiKey":
			return { ...state, values: { ...state.values, apiKey: "", clearApiKey: true } };
		case "keepApiKey":
			return { ...state, values: { ...state.values, clearApiKey: false } };
		case "addModel":
			return {
				...state,
				values: { ...state.values, models: [...state.values.models, emptyModelDraft] },
				modelRowIds: [...state.modelRowIds, action.rowId],
			};
		case "removeModel": {
			// Never drop the last row — an empty list would leave nowhere to type.
			const remaining = state.values.models.filter((_, index) => index !== action.index);
			const remainingIds = state.modelRowIds.filter((_, index) => index !== action.index);
			return {
				...state,
				values: { ...state.values, models: remaining.length > 0 ? remaining : [emptyModelDraft] },
				modelRowIds: remainingIds.length > 0 ? remainingIds : [action.replacementRowId],
			};
		}
		case "setModelField":
			return mapModel(state, action.index, (model) => ({ ...model, [action.field]: action.value }));
		case "toggleModelFlag":
			return mapModel(state, action.index, (model) => ({ ...model, [action.flag]: !model[action.flag] }));
		case "setModelEffort":
			return mapModel(state, action.index, (model) => ({ ...model, defaultReasoningEffort: action.value }));
		case "addProbedModel": {
			const wireId = action.wireId.trim();
			// The probe list stays clickable after a pick, so adding the same id twice has to be a no-op rather than a
			// duplicate row the save would then reject.
			if (state.values.models.some((model) => model.wireId.trim().toLowerCase() === wireId.toLowerCase())) {
				return state;
			}
			const added: ExternalProviderModelDraft = {
				...emptyModelDraft,
				wireId,
				contextLength: action.contextLength ? String(action.contextLength) : "",
			};
			// Fill the first blank row rather than appending under it — a fresh editor opens with one, and leaving it
			// stranded above the pick reads as a failed add.
			const blankIndex = state.values.models.findIndex((model) => model.wireId.trim().length === 0);
			if (blankIndex >= 0) {
				return mapModel(state, blankIndex, () => added);
			}
			return {
				...state,
				values: { ...state.values, models: [...state.values.models, added] },
				modelRowIds: [...state.modelRowIds, action.rowId],
			};
		}
		case "touchField":
			return { ...state, touched: { ...state.touched, [action.field]: true } };
		case "submit":
			return { ...state, submitted: true };
		default:
			return state;
	}
}
