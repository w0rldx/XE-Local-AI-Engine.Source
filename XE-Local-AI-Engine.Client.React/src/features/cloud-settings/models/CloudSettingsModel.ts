// Auth modes accepted by the Azure Foundry connection. Mirrors the backend `authMode` string
// ("ApiKey" | "ManagedIdentity" | "EntraId"); managed identity uses DefaultAzureCredential and needs no key.
// EntraId requests its own bearer token (app-only client-credentials when a secret is configured, otherwise
// interactive user sign-in) and is aimed at gateways — e.g. an Azure APIM AI gateway — that validate an Entra
// ID token rather than an Azure-native credential.
export type CloudAuthMode = "ApiKey" | "ManagedIdentity" | "EntraId";

// The wire surface an Azure Foundry connection targets. Mirrors the backend `AzureFoundryApiSurface` enum name.
// "AzureDeployments" is the classic Azure OpenAI deployments path; "OpenAiV1" is the OpenAI-compatible v1 surface
// (model name in the request body) for a gateway that only routes /openai/v1/*.
export type CloudApiSurface = "AzureDeployments" | "OpenAiV1";

// Interactive sign-in method for an EntraId connection. Mirrors the backend `EntraSignInMethod` enum name.
// `DeviceCode` / `InteractiveBrowser` apply with no configured client secret; `AuthorizationCode` is the one method
// that both REQUIRES a secret (to authenticate code redemption) and uses a delegated scope — every other value with
// a secret present is coerced to `ClientSecret` server-side.
export type EntraSignInMethod = "ClientSecret" | "DeviceCode" | "InteractiveBrowser" | "AuthorizationCode";

const ENTRA_SIGN_IN_METHODS: readonly EntraSignInMethod[] = [
	"ClientSecret",
	"DeviceCode",
	"InteractiveBrowser",
	"AuthorizationCode",
];

function isEntraSignInMethod(value: string): value is EntraSignInMethod {
	return (ENTRA_SIGN_IN_METHODS as readonly string[]).includes(value);
}

// Narrows an arbitrary string (a saved settings response, or a SegmentedControl's onChange value) to a known
// `EntraSignInMethod`, falling back to "DeviceCode" for anything unrecognized — e.g. a future backend enum value
// this build doesn't know about yet — instead of an unchecked cast that would let bad data flow into form state.
export function parseEntraSignInMethod(value: string | null | undefined): EntraSignInMethod {
	return value !== null && value !== undefined && isEntraSignInMethod(value) ? value : "DeviceCode";
}

const CLOUD_API_SURFACES: readonly CloudApiSurface[] = ["AzureDeployments", "OpenAiV1"];

function isCloudApiSurface(value: string): value is CloudApiSurface {
	return (CLOUD_API_SURFACES as readonly string[]).includes(value);
}

// Narrows an arbitrary string (a saved settings response, or a SegmentedControl's onChange value) to a known
// `CloudApiSurface`, falling back to "AzureDeployments" for anything unrecognized — mirrors the backend mapper's
// same fallback (CloudSettingsEndpointDtoMapper.ParseApiSurface).
export function parseApiSurface(value: string | null | undefined): CloudApiSurface {
	return value !== null && value !== undefined && isCloudApiSurface(value) ? value : "AzureDeployments";
}

// One editable deployment row in the models list. `deploymentName` is the Foundry portal deployment
// name (not the model family); `displayLabel` is an optional friendly label shown in the picker.
export interface CloudFoundryModelDraft {
	deploymentName: string;
	displayLabel: string;
}

// One editable custom-header row. Secret values are write-only: the backend never returns them, so
// `value` is blank on load for a secret row and `hasStoredValue` records that a secret is stored
// server-side (drives the "stored" hint and the no-resurrection guard when Secret is turned off).
export interface CloudHeaderDraft {
	name: string;
	value: string;
	isSecret: boolean;
	hasStoredValue: boolean;
}

export interface CloudSettingsFormValues {
	endpoint: string;
	authMode: CloudAuthMode;
	apiSurface: CloudApiSurface;
	apiKey: string;
	models: CloudFoundryModelDraft[];
	headers: CloudHeaderDraft[];
	hostSuffixes: string[];
	entraTenantId: string;
	entraClientId: string;
	// Write-only, like apiKey: blank on an existing EntraId connection keeps the stored secret; blank with no
	// stored secret selects interactive user sign-in instead of app-only client-credentials.
	entraClientSecret: string;
	entraTokenScope: string;
	entraSignInMethod: EntraSignInMethod;
	// Loopback redirect URI for `AuthorizationCode` sign-in. Blank selects the default. Ignored for every other
	// sign-in method.
	entraAuthCodeRedirectUri: string;
}

// Built-in Azure host suffixes that are always allowed for a managed-identity connection. A host that
// matches none of these but matches an operator-added suffix triggers the Entra-token egress warning.
export const AZURE_BUILTIN_HOST_SUFFIXES = [
	".openai.azure.com",
	".services.ai.azure.com",
	".cognitiveservices.azure.com",
] as const;

// Reserved header names (lower-cased for case-insensitive compare) that must never be operator-set —
// they would override credentials or transport framing. Mirrors the backend reserved set (Locked #8).
const RESERVED_HEADER_NAMES = new Set<string>([
	"api-key",
	"authorization",
	"host",
	"content-type",
	"content-length",
	"content-encoding",
	"cookie",
	"proxy-authorization",
	"transfer-encoding",
	"connection",
	"expect",
]);

// RFC 7230 header-name token charset (Locked #6).
const HEADER_NAME_TOKEN = /^[A-Za-z0-9!#$%&'*+\-.^_`|~]+$/;

// DNS label charset for an operator host suffix (alphanumerics + hyphen, no leading/trailing hyphen).
const DNS_LABEL = /^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?$/;

const MAX_HEADERS = 32;
const MAX_HEADER_NAME_LENGTH = 128;
const MAX_HEADER_VALUE_LENGTH = 4096;
const MAX_HOST_SUFFIXES = 16;
const MAX_HOST_SUFFIX_LENGTH = 253;

export function isHttpsAbsoluteUrl(value: string): boolean {
	try {
		return new URL(value).protocol === "https:";
	} catch {
		return false;
	}
}

// A models list is valid once at least one row carries a non-blank deployment name.
export function hasAtLeastOneModel(models: CloudFoundryModelDraft[]): boolean {
	return models.some((model) => model.deploymentName.trim().length > 0);
}

// RFC 7230 field-value guard (Locked #7): reject CR/LF/NUL and any control char except HTAB (0x09),
// plus DEL (0x7F). Implemented char-by-char to avoid a control-char regex literal (biome lint).
function hasControlCharacter(value: string): boolean {
	for (let index = 0; index < value.length; index += 1) {
		const code = value.charCodeAt(index);
		if (code === 0x09) {
			continue;
		}
		if (code <= 0x1f || code === 0x7f) {
			return true;
		}
	}
	return false;
}

// Shape guard for an operator-added host suffix (Locked #14): leading '.', ≥ 2 non-empty DNS labels
// (so a bare TLD like `.com` is rejected), no wildcard, valid label chars, within the length cap.
export function isValidHostSuffix(suffix: string): boolean {
	if (!suffix.startsWith(".") || suffix.length > MAX_HOST_SUFFIX_LENGTH || suffix.includes("*")) {
		return false;
	}
	const labels = suffix.slice(1).split(".");
	if (labels.length < 2) {
		return false;
	}
	return labels.every((label) => label.length > 0 && label.length <= 63 && DNS_LABEL.test(label));
}

// Validates the custom-header rows, returning the first problem as a single message (mirrors the
// backend guards so the operator sees an inline error before save). Blank-name rows are dropped, so
// only a blank name that carries a value is an error.
function validateHeaders(headers: CloudHeaderDraft[]): string | undefined {
	if (headers.length > MAX_HEADERS) {
		return `Remove some headers — at most ${MAX_HEADERS} custom headers are allowed.`;
	}

	const seenNames = new Set<string>();
	for (const header of headers) {
		const name = header.name.trim();
		const hasValue = header.value.trim().length > 0;

		if (name.length === 0) {
			if (hasValue) {
				return "Enter a header name for every row that has a value.";
			}
			continue;
		}

		if (name.length > MAX_HEADER_NAME_LENGTH) {
			return `Header name "${name}" is too long (max ${MAX_HEADER_NAME_LENGTH} characters).`;
		}
		if (!HEADER_NAME_TOKEN.test(name)) {
			return `Header name "${name}" contains characters that are not allowed in an HTTP header name.`;
		}
		const nameKey = name.toLowerCase();
		if (RESERVED_HEADER_NAMES.has(nameKey)) {
			return `Header name "${name}" is reserved and cannot be set.`;
		}
		if (seenNames.has(nameKey)) {
			return `Header name "${name}" is duplicated.`;
		}
		seenNames.add(nameKey);

		if (header.value.length > MAX_HEADER_VALUE_LENGTH) {
			return `Value for header "${name}" is too long (max ${MAX_HEADER_VALUE_LENGTH} characters).`;
		}
		if (hasControlCharacter(header.value)) {
			return `Value for header "${name}" contains a line break or control character.`;
		}

		// Secret rows keep the stored value only while both secret and blank. Turning "Secret" off on a
		// stored-secret row with a blank value must not silently reuse the stored value (Locked #10).
		if (!header.isSecret && header.hasStoredValue && !hasValue) {
			return `Enter a new value for header "${name}" before saving — the stored secret is not reused once "Secret" is turned off.`;
		}
		// A secret row with no stored value must carry a fresh value to resolve to anything.
		if (header.isSecret && !header.hasStoredValue && !hasValue) {
			return `Enter a value for the secret header "${name}".`;
		}
	}

	return undefined;
}

// Client-credentials (app-only) token requests are rejected by Entra ID (AADSTS1002012) unless the scope ends in
// "/.default" — mirrors the backend's AzureFoundryChatClientFactory.ValidateClientCredentialsScope fail-fast so the
// operator sees the problem before saving rather than on the next chat send. Does NOT apply to AuthorizationCode
// sign-in: that method also carries a secret, but authenticates code redemption rather than requesting an app-only
// token, so a delegated (non-/.default) scope is the EXPECTED shape there.
const CLIENT_CREDENTIALS_SCOPE_SUFFIX = "/.default";

// Mirrors the backend's EntraAuthCodeDefaults.TryValidateRedirectUri: absolute http(s) on a loopback host. A blank
// value is valid (it resolves to the default redirect URI server-side).
export function isValidLoopbackRedirectUri(value: string): boolean {
	const trimmed = value.trim();
	if (trimmed.length === 0) {
		return true;
	}

	let url: URL;
	try {
		url = new URL(trimmed);
	} catch {
		return false;
	}

	if (url.protocol !== "http:" && url.protocol !== "https:") {
		return false;
	}

	const host = url.hostname.toLowerCase();
	return host === "localhost" || host === "127.0.0.1" || host === "::1";
}

// Validates the EntraId-only fields. Tenant, client id, and token scope are always required in this mode; the
// client secret is intentionally optional (blank keeps a stored secret, or selects interactive sign-in when
// none is stored — both are valid states, so the secret itself carries no error) EXCEPT for AuthorizationCode,
// which requires one (see below).
//
// `hasStoredClientSecret` mirrors the same "hasSecret" check EntraConnectionFields.tsx makes for its sign-in-method
// picker: `entraClientSecret` is write-only (blank on load never means "no secret" — it can mean "keep the stored
// one"), so a blank field with a stored secret still resolves to the app-only client-credentials flow server-side
// and must be validated against the same /.default requirement.
function validateEntraFields(
	values: CloudSettingsFormValues,
	hasStoredClientSecret: boolean,
): Partial<Record<keyof CloudSettingsFormValues, string>> {
	if (values.authMode !== "EntraId") {
		return {};
	}

	const errors: Partial<Record<keyof CloudSettingsFormValues, string>> = {};
	if (values.entraTenantId.trim().length === 0) {
		errors.entraTenantId = "Enter the Entra ID tenant id.";
	}
	if (values.entraClientId.trim().length === 0) {
		errors.entraClientId = "Enter the Entra ID application (client) id.";
	}
	const tokenScope = values.entraTokenScope.trim();
	const hasSecret = values.entraClientSecret.trim().length > 0 || hasStoredClientSecret;
	const isAuthorizationCode = values.entraSignInMethod === "AuthorizationCode";
	if (tokenScope.length === 0) {
		errors.entraTokenScope = "Enter the token scope, e.g. api://<backend-app-id>/.default.";
	} else if (hasSecret && !isAuthorizationCode && !tokenScope.toLowerCase().endsWith(CLIENT_CREDENTIALS_SCOPE_SUFFIX)) {
		errors.entraTokenScope =
			"A client secret uses the app-only client-credentials flow, which requires a token scope ending in /.default (e.g. api://<backend-app-id>/.default) — or remove the secret to use a delegated scope, or choose the Authorization code sign-in method to use the secret with a delegated scope.";
	}

	if (isAuthorizationCode) {
		if (!hasSecret) {
			errors.entraClientSecret = "Authorization code sign-in requires a client secret.";
		}
		if (!isValidLoopbackRedirectUri(values.entraAuthCodeRedirectUri)) {
			errors.entraAuthCodeRedirectUri =
				"The redirect URI must be an absolute http(s) URL on a loopback host (localhost or 127.0.0.1), or blank to use the default.";
		}
	}

	return errors;
}

// Validates the operator-added allowed host suffixes. Blank rows are dropped on save.
function validateHostSuffixes(hostSuffixes: string[]): string | undefined {
	if (hostSuffixes.length > MAX_HOST_SUFFIXES) {
		return `Remove some host suffixes — at most ${MAX_HOST_SUFFIXES} are allowed.`;
	}
	for (const suffix of hostSuffixes) {
		const trimmed = suffix.trim();
		if (trimmed.length === 0) {
			continue;
		}
		if (!isValidHostSuffix(trimmed)) {
			return `"${trimmed}" is not a valid host suffix. Use a leading dot and at least two labels, e.g. .azure-api.net.`;
		}
	}
	return undefined;
}

// Resolves the endpoint hostname (lower-cased) or null when the endpoint is not a valid URL.
export function endpointHost(endpoint: string): string | null {
	try {
		return new URL(endpoint).hostname.toLowerCase();
	} catch {
		return null;
	}
}

function hostMatchesSuffix(host: string, suffix: string): boolean {
	const normalized = suffix.trim().toLowerCase();
	if (normalized.length === 0 || !normalized.startsWith(".")) {
		return false;
	}
	return host === normalized.slice(1) || host.endsWith(normalized);
}

// True when managed identity or EntraId is selected and the endpoint host is non-Azure (matched only by an
// operator-added suffix). Drives the orange Entra-token egress warning (Locked #14). Both modes send a
// bearer token obtained from Entra ID to the endpoint host, so the same non-Microsoft-host reminder applies to
// EntraId connections (commonly an APIM gateway) as well as managed identity.
export function shouldWarnManagedIdentityEgress(values: CloudSettingsFormValues): boolean {
	if (values.authMode !== "ManagedIdentity" && values.authMode !== "EntraId") {
		return false;
	}
	const host = endpointHost(values.endpoint.trim());
	if (host === null) {
		return false;
	}
	const isBuiltInAzureHost = AZURE_BUILTIN_HOST_SUFFIXES.some((suffix) => hostMatchesSuffix(host, suffix));
	if (isBuiltInAzureHost) {
		return false;
	}
	return values.hostSuffixes.some((suffix) => hostMatchesSuffix(host, suffix));
}

export function validateCloudSettingsForm(
	values: CloudSettingsFormValues,
	// True when the backend has a stored Entra client secret for this connection — see validateEntraFields for why
	// this can't be derived from `values` alone (the field is write-only).
	hasStoredEntraClientSecret = false,
): Partial<Record<keyof CloudSettingsFormValues, string>> {
	const errors: Partial<Record<keyof CloudSettingsFormValues, string>> = {};

	if (!isHttpsAbsoluteUrl(values.endpoint.trim())) {
		errors.endpoint = "Enter an absolute HTTPS Azure OpenAI endpoint.";
	}

	// The API key is only required for API-key auth; managed identity is keyless (DefaultAzureCredential).
	if (values.authMode === "ApiKey" && values.apiKey.trim().length === 0) {
		errors.apiKey = "Enter the API key. Saved keys are never returned to this page.";
	}

	if (!hasAtLeastOneModel(values.models)) {
		errors.models = "Add at least one deployment name.";
	}

	Object.assign(errors, validateEntraFields(values, hasStoredEntraClientSecret));

	const headerError = validateHeaders(values.headers);
	if (headerError !== undefined) {
		errors.headers = headerError;
	}

	const hostSuffixError = validateHostSuffixes(values.hostSuffixes);
	if (hostSuffixError !== undefined) {
		errors.hostSuffixes = hostSuffixError;
	}

	return errors;
}
