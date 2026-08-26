import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import type { SaveCloudSettingsResponse } from "@/core/api/generated";
import { toast } from "@/core/ui/notifications/Toast";
import {
	type CloudApiSurface,
	type CloudAuthMode,
	type CloudFoundryModelDraft,
	type CloudSettingsFormValues,
	type EntraSignInMethod,
	parseApiSurface,
	parseEntraSignInMethod,
} from "@/features/cloud-settings/models/CloudSettingsModel";

// The generated cloud-settings responses share one shape, so both the save and clear mutations resolve the same
// view; this alias keeps the onSuccess handlers readable.
type CloudSettings = SaveCloudSettingsResponse;

export function errorMessage(error: unknown): string {
	return apiErrorMessage(error, "Unexpected cloud settings error");
}

// Always show at least one (blank) deployment row so the user has somewhere to type on a fresh connection.
function withAtLeastOneRow(models: CloudFoundryModelDraft[]): CloudFoundryModelDraft[] {
	return models.length > 0 ? models : [{ deploymentName: "", displayLabel: "" }];
}

// Save and clear return the same view; both reset the form to the stored (redacted) values. The API key is never
// echoed back, so it always resets to empty. Module-scoped because it uses no component state.
export function settingsToFormValues(settings: CloudSettings): CloudSettingsFormValues {
	const azure = settings.azureFoundry;
	return {
		endpoint: azure?.endpoint ?? "",
		authMode: (azure?.authMode as CloudAuthMode) ?? "ApiKey",
		apiSurface: parseApiSurface(azure?.apiSurface),
		apiKey: "",
		models: withAtLeastOneRow(
			(azure?.models ?? []).map((model) => ({
				deploymentName: model.deploymentName ?? "",
				displayLabel: model.displayLabel ?? "",
			})),
		),
		// Secret header values are write-only: they load blank with a "stored" hint driven by hasStoredValue;
		// non-secret values round-trip for inline editing.
		headers: (azure?.headers ?? []).map((header) => ({
			name: header.name ?? "",
			value: header.isSecret ? "" : (header.value ?? ""),
			isSecret: header.isSecret ?? false,
			hasStoredValue: header.hasStoredValue ?? false,
		})),
		hostSuffixes: azure?.additionalAllowedHostSuffixes ?? [],
		entraTenantId: azure?.entraTenantId ?? "",
		entraClientId: azure?.entraClientId ?? "",
		// Write-only, like apiKey: always loads blank; a "stored" hint is driven by hasStoredEntraClientSecret.
		entraClientSecret: "",
		entraTokenScope: azure?.entraTokenScope ?? "",
		entraSignInMethod: parseEntraSignInMethod(azure?.entraSignInMethod),
		entraAuthCodeRedirectUri: azure?.entraAuthCodeRedirectUri ?? "",
	};
}

export function toastSettingsResult(settings: CloudSettings): void {
	toast.success(settings.azureFoundry ? "Cloud settings saved. Capability reporting was requested." : "Cloud settings cleared.");
}

const emptyFormValues: CloudSettingsFormValues = {
	endpoint: "",
	authMode: "ApiKey",
	apiSurface: "AzureDeployments",
	apiKey: "",
	models: [{ deploymentName: "", displayLabel: "" }],
	headers: [],
	hostSuffixes: [],
	entraTenantId: "",
	entraClientId: "",
	entraClientSecret: "",
	entraTokenScope: "",
	entraSignInMethod: "DeviceCode",
	entraAuthCodeRedirectUri: "",
};

let cloudRowSequence = 0;
export function nextCloudRowId(prefix: string): string {
	cloudRowSequence += 1;
	return `${prefix}-${cloudRowSequence}`;
}

function cloudRowIds(prefix: string, count: number): string[] {
	return Array.from({ length: count }, () => nextCloudRowId(prefix));
}

interface FormRowIds {
	models: string[];
	headers: string[];
	hostSuffixes: string[];
}

export function createFormRowIds(values: CloudSettingsFormValues): FormRowIds {
	return {
		models: cloudRowIds("cloud-model", values.models.length),
		headers: cloudRowIds("cloud-header", values.headers.length),
		hostSuffixes: cloudRowIds("cloud-host", values.hostSuffixes.length),
	};
}

// The form values, the per-field "touched" map, and the submit flag always reset together when the
// stored settings load or a save/clear completes. Grouping them under one reducer lets a single
// dispatch reset all three at once, replacing the cascading set-state calls that previously fired
// three separate updates from one effect.
export interface CloudSettingsFormState {
	values: CloudSettingsFormValues;
	touched: Partial<Record<keyof CloudSettingsFormValues, true>>;
	submitted: boolean;
	modelRowIds: string[];
	headerRowIds: string[];
	hostSuffixRowIds: string[];
}

export type CloudSettingsFormAction =
	| { type: "reset"; values: CloudSettingsFormValues; rowIds: FormRowIds }
	| { type: "setValues"; values: CloudSettingsFormValues; rowIds: FormRowIds }
	| {
			type: "setField";
			field:
				| "endpoint"
				| "apiKey"
				| "entraTenantId"
				| "entraClientId"
				| "entraClientSecret"
				| "entraTokenScope"
				| "entraAuthCodeRedirectUri";
			value: string;
	  }
	| { type: "setAuthMode"; value: CloudAuthMode }
	| { type: "setApiSurface"; value: CloudApiSurface }
	| { type: "setEntraSignInMethod"; value: EntraSignInMethod }
	| { type: "addModel"; rowId: string }
	| { type: "removeModel"; index: number; replacementRowId: string }
	| { type: "setModelField"; index: number; field: keyof CloudFoundryModelDraft; value: string }
	| { type: "addHeader"; rowId: string }
	| { type: "removeHeader"; index: number }
	| { type: "setHeaderField"; index: number; field: "name" | "value"; value: string }
	| { type: "toggleHeaderSecret"; index: number }
	| { type: "addHostSuffix"; rowId: string }
	| { type: "removeHostSuffix"; index: number }
	| { type: "setHostSuffix"; index: number; value: string }
	| { type: "touchField"; field: keyof CloudSettingsFormValues }
	| { type: "submit" };

export const initialFormState: CloudSettingsFormState = {
	values: emptyFormValues,
	touched: {},
	submitted: false,
	modelRowIds: cloudRowIds("cloud-model", emptyFormValues.models.length),
	headerRowIds: [],
	hostSuffixRowIds: [],
};

function resetRowIds(state: CloudSettingsFormState, values: CloudSettingsFormValues, rowIds: FormRowIds): CloudSettingsFormState {
	return {
		...state,
		values,
		modelRowIds: rowIds.models,
		headerRowIds: rowIds.headers,
		hostSuffixRowIds: rowIds.hostSuffixes,
	};
}

export function formReducer(state: CloudSettingsFormState, action: CloudSettingsFormAction): CloudSettingsFormState {
	switch (action.type) {
		// Loading stored settings and a successful save both replace the values and clear the
		// touched/submitted interaction flags in one step.
		case "reset":
			return resetRowIds({ ...state, touched: {}, submitted: false }, action.values, action.rowIds);
		// Clearing credentials replaces only the values, leaving any existing interaction flags intact
		// (matches the original clear handler, which never reset touched/submitted).
		case "setValues":
			return resetRowIds(state, action.values, action.rowIds);
		case "setField":
			return { ...state, values: { ...state.values, [action.field]: action.value } };
		case "setAuthMode":
			return { ...state, values: { ...state.values, authMode: action.value } };
		case "setApiSurface":
			return { ...state, values: { ...state.values, apiSurface: action.value } };
		case "setEntraSignInMethod":
			return { ...state, values: { ...state.values, entraSignInMethod: action.value } };
		case "addModel":
			return {
				...state,
				values: { ...state.values, models: [...state.values.models, { deploymentName: "", displayLabel: "" }] },
				modelRowIds: [...state.modelRowIds, action.rowId],
			};
		case "removeModel": {
			// Never drop the last row — keep one blank row so the list is always editable.
			const next = state.values.models.filter((_, index) => index !== action.index);
			const models = withAtLeastOneRow(next);
			const remainingIds = state.modelRowIds.filter((_, index) => index !== action.index);
			return {
				...state,
				values: { ...state.values, models },
				modelRowIds: remainingIds.length > 0 ? remainingIds : [action.replacementRowId],
			};
		}
		case "setModelField": {
			const next = state.values.models.map((model, index) =>
				index === action.index ? { ...model, [action.field]: action.value } : model,
			);
			return { ...state, values: { ...state.values, models: next } };
		}
		case "addHeader":
			return {
				...state,
				values: {
					...state.values,
					headers: [...state.values.headers, { name: "", value: "", isSecret: false, hasStoredValue: false }],
				},
				headerRowIds: [...state.headerRowIds, action.rowId],
			};
		case "removeHeader":
			return {
				...state,
				values: { ...state.values, headers: state.values.headers.filter((_, index) => index !== action.index) },
				headerRowIds: state.headerRowIds.filter((_, index) => index !== action.index),
			};
		case "setHeaderField": {
			const next = state.values.headers.map((header, index) =>
				index === action.index ? { ...header, [action.field]: action.value } : header,
			);
			return { ...state, values: { ...state.values, headers: next } };
		}
		case "toggleHeaderSecret": {
			const next = state.values.headers.map((header, index) =>
				index === action.index ? { ...header, isSecret: !header.isSecret } : header,
			);
			return { ...state, values: { ...state.values, headers: next } };
		}
		case "addHostSuffix":
			return {
				...state,
				values: { ...state.values, hostSuffixes: [...state.values.hostSuffixes, ""] },
				hostSuffixRowIds: [...state.hostSuffixRowIds, action.rowId],
			};
		case "removeHostSuffix":
			return {
				...state,
				values: {
					...state.values,
					hostSuffixes: state.values.hostSuffixes.filter((_, index) => index !== action.index),
				},
				hostSuffixRowIds: state.hostSuffixRowIds.filter((_, index) => index !== action.index),
			};
		case "setHostSuffix": {
			const next = state.values.hostSuffixes.map((suffix, index) => (index === action.index ? action.value : suffix));
			return { ...state, values: { ...state.values, hostSuffixes: next } };
		}
		case "touchField":
			return { ...state, touched: { ...state.touched, [action.field]: true } };
		case "submit":
			return { ...state, submitted: true };
		default:
			return state;
	}
}
