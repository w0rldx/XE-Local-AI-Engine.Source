import type {
	XeLocalAiEngineClientEndpointsMcpV1CreateMcpServerRequest,
	XeLocalAiEngineClientEndpointsMcpV1McpServerResponse,
	XeLocalAiEngineClientEndpointsMcpV1McpServerToolsResponse,
} from "@/core/api/generated";
import type {
	McpEnvEntry,
	McpServerFormValues,
	McpServerRegistration,
	McpTransportKind,
	McpTrustTier,
} from "@/features/mcp/models/McpServerModels";
import type { McpConnectionStatus, McpServerToolsView } from "@/features/mcp/models/McpServerToolsModels";

// Maps the generated (OpenAPI) MCP response types to the stricter domain view-models the components depend on.
// The generated types are the single source of truth for the wire shape; their fields are all optional (`x?: T`),
// so each mapper coalesces every field to a required value with a sensible default. The generated transportKind
// enum is a string union with the SAME values as the domain McpTransportKind, so it maps through unchanged.
//
// REDACTION: the backend already redacts the MCP server response — secret-bearing fields are returned only as the
// operator is permitted to see them. These mappers only ever surface what the API actually returns; they never
// reconstruct or infer a sensitive field. The tools status/error are likewise whatever the redacting backend chose
// to expose (a redacted connection error string, never raw transport internals).

const DEFAULT_TRANSPORT_KIND: McpTransportKind = "Stdio";
// An omitted tier means the secure default, never the privileged one — the same fail-closed reading the
// backend's DTO default applies.
const DEFAULT_TRUST_TIER: McpTrustTier = "Sandboxed";
// The tools response status is a plain string on the wire; the domain narrows it to a known union but the panel
// renders any unrecognized value gracefully, so an omitted status falls back to the neutral "disabled" state.
const DEFAULT_CONNECTION_STATUS: McpConnectionStatus = "disabled";

function envMapToEntries(env: { [key: string]: string } | null | undefined): McpEnvEntry[] {
	if (!env) {
		return [];
	}
	return Object.entries(env).map(([key, value]) => ({ key, value }));
}

function envEntriesToMap(entries: readonly McpEnvEntry[]): Record<string, string> {
	// Only persist rows with a non-empty key; last write wins on a duplicate key (the form allows transient
	// duplicates while typing — the stored map can never carry a blank key).
	const result: Record<string, string> = {};
	for (const entry of entries) {
		const key = entry.key.trim();
		if (key.length > 0) {
			result[key] = entry.value;
		}
	}
	return result;
}

export function toMcpServerRegistration(
	dto: XeLocalAiEngineClientEndpointsMcpV1McpServerResponse,
): McpServerRegistration {
	return {
		id: dto.id ?? "",
		name: dto.name ?? "",
		description: dto.description ?? "",
		transportKind: dto.transportKind ?? DEFAULT_TRANSPORT_KIND,
		command: dto.command ?? null,
		arguments: dto.arguments ?? [],
		workingDirectory: dto.workingDirectory ?? null,
		env: envMapToEntries(dto.env),
		url: dto.url ?? null,
		trustTier: dto.trustTier ?? DEFAULT_TRUST_TIER,
		enabled: dto.enabled ?? false,
		version: dto.version ?? 0,
		createdAtUtc: dto.createdAtUtc ?? 0,
		updatedAtUtc: dto.updatedAtUtc ?? 0,
	};
}

// Projects form values to the generated create/update request body. Create and update share the same wire shape
// (verified against the generated CreateMcpServerRequest / UpdateMcpServerRequest — structurally identical), so one
// mapper serves both. Only the transport-relevant fields are sent so a stored row never carries cross-transport
// leftovers (e.g. a command on an HTTP server). A blank optional field becomes null.
export function toSaveMcpServerRequest(
	form: McpServerFormValues,
): XeLocalAiEngineClientEndpointsMcpV1CreateMcpServerRequest {
	const trimmedDescription = form.description.trim();
	const isStdio = form.transportKind === "Stdio";

	return {
		name: form.name.trim(),
		description: trimmedDescription.length > 0 ? trimmedDescription : null,
		transportKind: form.transportKind,
		command: isStdio && form.command.trim().length > 0 ? form.command.trim() : null,
		arguments: isStdio ? form.arguments.filter((argument) => argument.length > 0) : [],
		workingDirectory: isStdio && form.workingDirectory.trim().length > 0 ? form.workingDirectory.trim() : null,
		env: isStdio ? envEntriesToMap(form.env) : {},
		url: !isStdio && form.url.trim().length > 0 ? form.url.trim() : null,
		// The tier is inert for HTTP (this node launches nothing), and the backend normalizes it away; send the
		// secure default rather than whatever the form happened to be carrying when the transport was switched.
		trustTier: isStdio ? form.trustTier : "Sandboxed",
	};
}

export function toMcpServerToolsView(
	dto: XeLocalAiEngineClientEndpointsMcpV1McpServerToolsResponse,
): McpServerToolsView {
	return {
		// status is a plain string on the wire; cast to the domain union (the panel handles unknown values).
		status: (dto.status as McpConnectionStatus | undefined) ?? DEFAULT_CONNECTION_STATUS,
		error: dto.error ?? null,
		tools: (dto.tools ?? []).map((tool) => ({
			name: tool.name ?? "",
			description: tool.description ?? "",
			requiresApproval: tool.requiresApproval ?? false,
		})),
	};
}
