import { describe, expect, it } from "vitest";

import type { CustomHeaderDraft } from "@/core/http-headers/CustomHeaders";
import {
	type CloudSettingsFormValues,
	contextLengthToNumber,
	hasAtLeastOneModel,
	isHttpsAbsoluteUrl,
	isValidHostSuffix,
	parseApiSurface,
	parseEntraSignInMethod,
	shouldWarnManagedIdentityEgress,
	validateCloudSettingsForm,
} from "@/features/cloud-providers/azure/models/CloudSettingsModel";

function baseValues(overrides: Partial<CloudSettingsFormValues> = {}): CloudSettingsFormValues {
	return {
		endpoint: "https://example.openai.azure.com/",
		authMode: "ApiKey",
		apiSurface: "AzureDeployments",
		apiKey: "secret-key",
		models: [{ deploymentName: "gpt-4o", displayLabel: "", contextLength: "" }],
		headers: [],
		hostSuffixes: [],
		entraTenantId: "",
		entraClientId: "",
		entraClientSecret: "",
		entraTokenScope: "",
		entraSignInMethod: "DeviceCode",
		entraAuthCodeRedirectUri: "",
		...overrides,
	};
}

function header(overrides: Partial<CustomHeaderDraft> = {}): CustomHeaderDraft {
	return { name: "X-Custom", value: "v", isSecret: false, hasStoredSecret: false, ...overrides };
}

describe("cloud settings model", () => {
	it("accepts only absolute HTTPS URLs", () => {
		expect(isHttpsAbsoluteUrl("https://example.openai.azure.com/")).toBe(true);
		expect(isHttpsAbsoluteUrl("http://example.openai.azure.com/")).toBe(false);
		expect(isHttpsAbsoluteUrl("example.openai.azure.com")).toBe(false);
	});

	it("parses the API surface, falling back to AzureDeployments for an unrecognized or missing value", () => {
		expect(parseApiSurface("OpenAiV1")).toBe("OpenAiV1");
		expect(parseApiSurface("AzureDeployments")).toBe("AzureDeployments");
		expect(parseApiSurface("SomeFutureSurface")).toBe("AzureDeployments");
		expect(parseApiSurface(undefined)).toBe("AzureDeployments");
		expect(parseApiSurface(null)).toBe("AzureDeployments");
	});

	it("reports a models list valid only when a row carries a non-blank deployment name", () => {
		expect(hasAtLeastOneModel([{ deploymentName: "", displayLabel: "", contextLength: "" }])).toBe(false);
		expect(hasAtLeastOneModel([{ deploymentName: "  ", displayLabel: "x", contextLength: "" }])).toBe(false);
		expect(hasAtLeastOneModel([{ deploymentName: "gpt-4o", displayLabel: "", contextLength: "" }])).toBe(true);
	});

	it("requires endpoint, API key, and at least one model in API-key mode", () => {
		const errors = validateCloudSettingsForm(
			baseValues({
				endpoint: "http://example.openai.azure.com/",
				apiKey: "",
				models: [{ deploymentName: "", displayLabel: "", contextLength: "" }],
			}),
		);

		expect(errors.endpoint).toBeDefined();
		expect(errors.apiKey).toBeDefined();
		expect(errors.models).toBeDefined();
	});

	it("does not require the API key in managed-identity mode", () => {
		const errors = validateCloudSettingsForm(baseValues({ authMode: "ManagedIdentity", apiKey: "" }));

		expect(errors.apiKey).toBeUndefined();
		expect(errors.endpoint).toBeUndefined();
		expect(errors.models).toBeUndefined();
	});

	it("accepts a fully valid API-key connection", () => {
		expect(validateCloudSettingsForm(baseValues())).toEqual({});
	});

	it("maps a blank context length to null and a typed one to its number", () => {
		expect(contextLengthToNumber("")).toBeNull();
		expect(contextLengthToNumber("  ")).toBeNull();
		expect(contextLengthToNumber("128000")).toBe(128000);
	});
});

describe("cloud settings header validation", () => {
	// The rules themselves are covered in CustomHeaders.test.ts; this pins that the form surfaces them on headers.
	it("surfaces the first header problem on the headers field", () => {
		expect(validateCloudSettingsForm(baseValues({ headers: [header({ name: "Authorization" })] })).headers).toContain("reserved");
		expect(validateCloudSettingsForm(baseValues({ headers: [header()] })).headers).toBeUndefined();
	});
});

describe("EntraId field validation", () => {
	it("requires tenant, client id, and token scope in EntraId mode", () => {
		const errors = validateCloudSettingsForm(baseValues({ authMode: "EntraId", apiKey: "" }));

		expect(errors.entraTenantId).toBeDefined();
		expect(errors.entraClientId).toBeDefined();
		expect(errors.entraTokenScope).toBeDefined();
		// The API key is not EntraId's concern.
		expect(errors.apiKey).toBeUndefined();
	});

	it("does not require a client secret in EntraId mode", () => {
		const errors = validateCloudSettingsForm(
			baseValues({
				authMode: "EntraId",
				apiKey: "",
				entraTenantId: "tenant-id",
				entraClientId: "client-id",
				entraTokenScope: "api://backend-app-id/.default",
				entraClientSecret: "",
			}),
		);

		expect(errors).toEqual({});
	});

	it("ignores EntraId fields outside EntraId mode", () => {
		expect(validateCloudSettingsForm(baseValues()).entraTenantId).toBeUndefined();
	});

	it("rejects a delegated scope when a client secret is freshly typed", () => {
		const errors = validateCloudSettingsForm(
			baseValues({
				authMode: "EntraId",
				apiKey: "",
				entraTenantId: "tenant-id",
				entraClientId: "client-id",
				entraClientSecret: "fresh-secret",
				entraTokenScope: "api://backend-app-id/access_as_user",
			}),
		);

		expect(errors.entraTokenScope).toContain("/.default");
	});

	it("accepts a /.default scope when a client secret is freshly typed", () => {
		const errors = validateCloudSettingsForm(
			baseValues({
				authMode: "EntraId",
				apiKey: "",
				entraTenantId: "tenant-id",
				entraClientId: "client-id",
				entraClientSecret: "fresh-secret",
				entraTokenScope: "api://backend-app-id/.default",
			}),
		);

		expect(errors.entraTokenScope).toBeUndefined();
	});

	it("rejects a delegated scope when the client field is blank but a secret is already stored", () => {
		// entraClientSecret is write-only: a blank field on a saved EntraId+secret connection still resolves to the
		// app-only client-credentials flow server-side, so validation must catch it via hasStoredEntraClientSecret.
		const errors = validateCloudSettingsForm(
			baseValues({
				authMode: "EntraId",
				apiKey: "",
				entraTenantId: "tenant-id",
				entraClientId: "client-id",
				entraClientSecret: "",
				entraTokenScope: "api://backend-app-id/access_as_user",
			}),
			/* hasStoredEntraClientSecret */ true,
		);

		expect(errors.entraTokenScope).toContain("/.default");
	});

	it("accepts a delegated scope when the client field is blank and no secret is stored", () => {
		const errors = validateCloudSettingsForm(
			baseValues({
				authMode: "EntraId",
				apiKey: "",
				entraTenantId: "tenant-id",
				entraClientId: "client-id",
				entraClientSecret: "",
				entraTokenScope: "api://backend-app-id/access_as_user",
			}),
			/* hasStoredEntraClientSecret */ false,
		);

		expect(errors.entraTokenScope).toBeUndefined();
	});

	it("relaxes the /.default requirement for AuthorizationCode: a delegated scope with a secret is valid", () => {
		const errors = validateCloudSettingsForm(
			baseValues({
				authMode: "EntraId",
				apiKey: "",
				entraTenantId: "tenant-id",
				entraClientId: "client-id",
				entraClientSecret: "fresh-secret",
				entraTokenScope: "api://backend-app-id/access_as_user",
				entraSignInMethod: "AuthorizationCode",
			}),
		);

		expect(errors.entraTokenScope).toBeUndefined();
	});

	it("requires a client secret for AuthorizationCode sign-in", () => {
		const errors = validateCloudSettingsForm(
			baseValues({
				authMode: "EntraId",
				apiKey: "",
				entraTenantId: "tenant-id",
				entraClientId: "client-id",
				entraClientSecret: "",
				entraTokenScope: "api://backend-app-id/access_as_user",
				entraSignInMethod: "AuthorizationCode",
			}),
			/* hasStoredEntraClientSecret */ false,
		);

		expect(errors.entraClientSecret).toBeDefined();
	});

	it("accepts AuthorizationCode with a stored (not freshly typed) secret", () => {
		const errors = validateCloudSettingsForm(
			baseValues({
				authMode: "EntraId",
				apiKey: "",
				entraTenantId: "tenant-id",
				entraClientId: "client-id",
				entraClientSecret: "",
				entraTokenScope: "api://backend-app-id/access_as_user",
				entraSignInMethod: "AuthorizationCode",
			}),
			/* hasStoredEntraClientSecret */ true,
		);

		expect(errors.entraClientSecret).toBeUndefined();
	});

	it("accepts a blank AuthorizationCode redirect URI (selects the default)", () => {
		const errors = validateCloudSettingsForm(
			baseValues({
				authMode: "EntraId",
				apiKey: "",
				entraTenantId: "tenant-id",
				entraClientId: "client-id",
				entraClientSecret: "fresh-secret",
				entraTokenScope: "api://backend-app-id/access_as_user",
				entraSignInMethod: "AuthorizationCode",
				entraAuthCodeRedirectUri: "",
			}),
		);

		expect(errors.entraAuthCodeRedirectUri).toBeUndefined();
	});

	it("accepts a loopback AuthorizationCode redirect URI", () => {
		const errors = validateCloudSettingsForm(
			baseValues({
				authMode: "EntraId",
				apiKey: "",
				entraTenantId: "tenant-id",
				entraClientId: "client-id",
				entraClientSecret: "fresh-secret",
				entraTokenScope: "api://backend-app-id/access_as_user",
				entraSignInMethod: "AuthorizationCode",
				entraAuthCodeRedirectUri: "http://127.0.0.1:53682/signin-oidc",
			}),
		);

		expect(errors.entraAuthCodeRedirectUri).toBeUndefined();
	});

	it("rejects a non-loopback AuthorizationCode redirect URI", () => {
		const errors = validateCloudSettingsForm(
			baseValues({
				authMode: "EntraId",
				apiKey: "",
				entraTenantId: "tenant-id",
				entraClientId: "client-id",
				entraClientSecret: "fresh-secret",
				entraTokenScope: "api://backend-app-id/access_as_user",
				entraSignInMethod: "AuthorizationCode",
				entraAuthCodeRedirectUri: "http://example.com/signin-oidc",
			}),
		);

		expect(errors.entraAuthCodeRedirectUri).toBeDefined();
	});

	it("rejects a malformed AuthorizationCode redirect URI", () => {
		const errors = validateCloudSettingsForm(
			baseValues({
				authMode: "EntraId",
				apiKey: "",
				entraTenantId: "tenant-id",
				entraClientId: "client-id",
				entraClientSecret: "fresh-secret",
				entraTokenScope: "api://backend-app-id/access_as_user",
				entraSignInMethod: "AuthorizationCode",
				entraAuthCodeRedirectUri: "not-a-uri",
			}),
		);

		expect(errors.entraAuthCodeRedirectUri).toBeDefined();
	});
});

describe("parseEntraSignInMethod", () => {
	it("passes through each known sign-in method", () => {
		expect(parseEntraSignInMethod("ClientSecret")).toBe("ClientSecret");
		expect(parseEntraSignInMethod("DeviceCode")).toBe("DeviceCode");
		expect(parseEntraSignInMethod("InteractiveBrowser")).toBe("InteractiveBrowser");
		expect(parseEntraSignInMethod("AuthorizationCode")).toBe("AuthorizationCode");
	});

	it("falls back to DeviceCode for null, undefined, or an unrecognized value", () => {
		expect(parseEntraSignInMethod(null)).toBe("DeviceCode");
		expect(parseEntraSignInMethod(undefined)).toBe("DeviceCode");
		expect(parseEntraSignInMethod("SomeFutureMethod")).toBe("DeviceCode");
		expect(parseEntraSignInMethod("")).toBe("DeviceCode");
	});
});

describe("host suffix validation", () => {
	it("accepts a leading-dot suffix with at least two labels", () => {
		expect(isValidHostSuffix(".azure-api.net")).toBe(true);
		expect(isValidHostSuffix(".contoso.com")).toBe(true);
	});

	it("rejects bare TLDs, missing dot, and wildcards", () => {
		expect(isValidHostSuffix(".com")).toBe(false);
		expect(isValidHostSuffix("azure-api.net")).toBe(false);
		expect(isValidHostSuffix(".*.net")).toBe(false);
		expect(isValidHostSuffix(".")).toBe(false);
	});

	it("surfaces an inline error for a malformed suffix and caps the count", () => {
		expect(validateCloudSettingsForm(baseValues({ hostSuffixes: [".com"] })).hostSuffixes).toBeDefined();
		expect(validateCloudSettingsForm(baseValues({ hostSuffixes: [".azure-api.net"] })).hostSuffixes).toBeUndefined();
		const many = Array.from({ length: 17 }, () => ".azure-api.net");
		expect(validateCloudSettingsForm(baseValues({ hostSuffixes: many })).hostSuffixes).toBeDefined();
	});
});

describe("managed-identity egress warning", () => {
	it("warns when MI targets a non-Azure host matched by an operator suffix", () => {
		expect(
			shouldWarnManagedIdentityEgress(
				baseValues({
					authMode: "ManagedIdentity",
					apiKey: "",
					endpoint: "https://gw.azure-api.net/",
					hostSuffixes: [".azure-api.net"],
				}),
			),
		).toBe(true);
	});

	it("also warns for an EntraId connection targeting a non-Azure host matched by an operator suffix", () => {
		expect(
			shouldWarnManagedIdentityEgress(
				baseValues({
					authMode: "EntraId",
					apiKey: "",
					endpoint: "https://gw.azure-api.net/",
					hostSuffixes: [".azure-api.net"],
				}),
			),
		).toBe(true);
	});

	it("does not warn for a built-in Azure host or in API-key mode", () => {
		expect(
			shouldWarnManagedIdentityEgress(
				baseValues({ authMode: "ManagedIdentity", apiKey: "", endpoint: "https://x.openai.azure.com/" }),
			),
		).toBe(false);
		expect(
			shouldWarnManagedIdentityEgress(
				baseValues({ authMode: "ApiKey", endpoint: "https://gw.azure-api.net/", hostSuffixes: [".azure-api.net"] }),
			),
		).toBe(false);
	});
});
