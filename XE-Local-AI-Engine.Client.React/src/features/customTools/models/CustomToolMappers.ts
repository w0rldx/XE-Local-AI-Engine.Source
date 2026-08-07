import type {
	XeLocalAiEngineClientServicesCustomToolsCommandDefinition,
	XeLocalAiEngineClientServicesCustomToolsCustomToolDefinition,
	XeLocalAiEngineClientServicesCustomToolsCustomToolView,
	XeLocalAiEngineClientServicesCustomToolsHttpFetchDefinition,
} from "@/core/api/generated";
import type {
	CustomToolCommandDefinition,
	CustomToolFormValues,
	CustomToolHttpDefinition,
	CustomToolParameterType,
	CustomToolView,
} from "@/features/customTools/models/CustomToolModels";
import { CUSTOM_TOOL_PARAMETER_TYPES, toSlug } from "@/features/customTools/models/CustomToolModels";

// Maps the generated (OpenAPI) custom-tool response into the stricter domain view-model and projects the domain form
// back onto the generated request body. Generated fields are all optional (`x?: T`), so every response field is
// coalesced to a safe default. Secret header/env values arrive masked as the sentinel and ride straight through — the
// backend resolves an unchanged sentinel back to the stored secret, so only a value the operator edits is re-persisted.

function toParameterType(type: string | undefined): CustomToolParameterType {
	return CUSTOM_TOOL_PARAMETER_TYPES.includes(type as CustomToolParameterType) ? (type as CustomToolParameterType) : "string";
}

const emptyHttp: CustomToolHttpDefinition = { method: "GET", urlTemplate: "", headers: [], bodyTemplate: "", allowedHosts: [] };
const emptyCommand: CustomToolCommandDefinition = {
	executable: "",
	argsTemplate: [],
	workingDirectory: "",
	timeoutSeconds: 0,
	env: [],
};

function toHttpDefinition(dto: XeLocalAiEngineClientServicesCustomToolsHttpFetchDefinition | null | undefined): CustomToolHttpDefinition {
	if (!dto) {
		return emptyHttp;
	}
	return {
		method: dto.method ?? "GET",
		urlTemplate: dto.urlTemplate ?? "",
		headers: (dto.headers ?? []).map((header) => ({
			name: header.name ?? "",
			value: header.value ?? "",
			isSecret: header.isSecret ?? false,
		})),
		bodyTemplate: dto.bodyTemplate ?? "",
		allowedHosts: dto.allowedHosts ?? [],
	};
}

function toCommandDefinition(
	dto: XeLocalAiEngineClientServicesCustomToolsCommandDefinition | null | undefined,
): CustomToolCommandDefinition {
	if (!dto) {
		return emptyCommand;
	}
	return {
		executable: dto.executable ?? "",
		argsTemplate: dto.argsTemplate ?? [],
		workingDirectory: dto.workingDirectory ?? "",
		timeoutSeconds: dto.timeoutSeconds ?? 0,
		env: (dto.env ?? []).map((variable) => ({
			name: variable.name ?? "",
			value: variable.value ?? "",
			isSecret: variable.isSecret ?? false,
		})),
	};
}

export function toCustomToolView(dto: XeLocalAiEngineClientServicesCustomToolsCustomToolView): CustomToolView {
	return {
		id: dto.id ?? "",
		name: dto.name ?? "",
		description: dto.description ?? "",
		kind: dto.kind ?? "HttpFetch",
		mode: dto.mode ?? "Fixed",
		enabled: dto.enabled ?? false,
		acknowledged: dto.acknowledged ?? false,
		version: dto.version ?? 0,
		createdAtUtc: dto.createdAtUtc ?? 0,
		updatedAtUtc: dto.updatedAtUtc ?? 0,
		parameters: (dto.parameters ?? []).map((parameter) => ({
			name: parameter.name ?? "",
			type: toParameterType(parameter.type),
			description: parameter.description ?? "",
			required: parameter.required ?? false,
		})),
		http: dto.http ? toHttpDefinition(dto.http) : null,
		command: dto.command ? toCommandDefinition(dto.command) : null,
	};
}

// Seeds the editor form from a stored tool. The name is stripped to its slug (the `custom__` prefix is fixed adornment
// in the UI). Both kind blocks are populated (falling back to empty) so switching kind in the editor never loses data.
export function toFormValues(view: CustomToolView): CustomToolFormValues {
	return {
		name: toSlug(view.name),
		description: view.description,
		kind: view.kind,
		mode: view.mode,
		enabled: view.enabled,
		// Force a fresh acknowledgement on every edit: enabling a host-exec tool is a decision the operator re-affirms.
		acknowledged: false,
		parameters: view.parameters.map((parameter) => ({ ...parameter })),
		http: view.http ? { ...view.http, headers: [...view.http.headers], allowedHosts: [...view.http.allowedHosts] } : emptyHttp,
		command: view.command ? { ...view.command, argsTemplate: [...view.command.argsTemplate], env: [...view.command.env] } : emptyCommand,
	};
}

// Projects form values to the generated definition body. Only the active kind's block is sent; the inactive block is
// omitted so a draft in the hidden editor never reaches the server. Trimmed so a stored tool carries no stray
// whitespace. Secret values (including the round-tripped sentinel) are passed through verbatim.
export function toDefinition(form: CustomToolFormValues): XeLocalAiEngineClientServicesCustomToolsCustomToolDefinition {
	const isParameterized = form.mode === "Parameterized";
	return {
		name: form.name.trim(),
		description: form.description.trim(),
		kind: form.kind,
		mode: form.mode,
		enabled: form.enabled,
		acknowledged: form.acknowledged,
		// A Fixed tool must declare no parameters (the backend rejects them); drop any stale rows on submit.
		parameters: isParameterized
			? form.parameters.map((parameter) => ({
					name: parameter.name.trim(),
					type: parameter.type,
					description: parameter.description.trim(),
					required: parameter.required,
				}))
			: [],
		http:
			form.kind === "HttpFetch"
				? {
						method: form.http.method.trim(),
						urlTemplate: form.http.urlTemplate.trim(),
						headers: form.http.headers.map((header) => ({
							name: header.name.trim(),
							value: header.value,
							isSecret: header.isSecret,
						})),
						bodyTemplate: form.http.bodyTemplate.length > 0 ? form.http.bodyTemplate : null,
						allowedHosts: form.http.allowedHosts.map((host) => host.trim()).filter((host) => host.length > 0),
					}
				: null,
		command:
			form.kind === "Command"
				? {
						executable: form.command.executable.trim(),
						argsTemplate: [...form.command.argsTemplate],
						workingDirectory: form.command.workingDirectory.trim().length > 0 ? form.command.workingDirectory.trim() : null,
						timeoutSeconds: form.command.timeoutSeconds,
						env: form.command.env.map((variable) => ({
							name: variable.name.trim(),
							value: variable.value,
							isSecret: variable.isSecret,
						})),
					}
				: null,
	};
}
