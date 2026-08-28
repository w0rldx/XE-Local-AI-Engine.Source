import { describe, expect, it } from "vitest";

import { emptyFormValues, emptyModelDraft } from "@/features/external-providers/models/ExternalProviderFormState";
import {
	baseUrlOrigin,
	type ExternalProviderFormValues,
	isTrustedLocalHost,
	MAX_TIMEOUT_SECONDS,
	MIN_TIMEOUT_SECONDS,
	parseLocality,
	requiresApiKeyReentry,
	shouldWarnLocalDeclaration,
	validateExternalProviderForm,
} from "@/features/external-providers/models/ExternalProviderModel";

function values(overrides: Partial<ExternalProviderFormValues> = {}): ExternalProviderFormValues {
	return {
		...emptyFormValues,
		connectionId: "unsloth-box",
		displayName: "Unsloth box",
		baseUrl: "http://127.0.0.1:8080/v1",
		models: [{ ...emptyModelDraft, wireId: "qwen3-27b" }],
		...overrides,
	};
}

describe("parseLocality", () => {
	it("accepts the two declared localities", () => {
		expect(parseLocality("Local")).toBe("Local");
		expect(parseLocality("Cloud")).toBe("Cloud");
	});

	it("resolves anything unrecognized to Cloud, never to the more privileged Local", () => {
		expect(parseLocality("local")).toBe("Cloud");
		expect(parseLocality(undefined)).toBe("Cloud");
		expect(parseLocality("Onprem")).toBe("Cloud");
	});
});

describe("isTrustedLocalHost", () => {
	it.each([
		"localhost",
		"dev.localhost",
		"127.0.0.1",
		"127.1.2.3",
		"::1",
		"10.0.0.4",
		"192.168.1.50",
		"172.16.0.9",
		"172.31.255.1",
		"169.254.1.1",
		"printer.local",
		"fd12::1",
		// Bracketed, as `URL.hostname` hands them over.
		"[fd00::1]",
		"[fe80::1]",
		"[::1]",
	])("treats %s as reachable only from this machine or network", (host) => {
		expect(isTrustedLocalHost(host)).toBe(true);
	});

	it.each([
		"api.openai.com",
		"8.8.8.8",
		"172.32.0.1",
		"172.15.0.1",
		"example.com",
		"203.0.113.7",
		// A public IPv6 literal: inside the brackets, but in neither private range.
		"[2001:db8::1]",
		// Ordinary DNS names that merely BEGIN like an fc00::/7 or fe80::/10 address. They resolve wherever DNS says,
		// so only a parsed IP literal may satisfy the CIDR rules.
		"fd-api.example.com",
		"fcgateway.example.com",
		"fe80.example.com",
	])("treats %s as off-network", (host) => {
		expect(isTrustedLocalHost(host)).toBe(false);
	});
});

describe("baseUrlOrigin", () => {
	it("keeps scheme, host and port and drops the path — a credential is bound to the endpoint, not the route", () => {
		expect(baseUrlOrigin("http://127.0.0.1:8080/v1")).toBe("http://127.0.0.1:8080");
		expect(baseUrlOrigin("http://127.0.0.1:8080/openai/v1")).toBe("http://127.0.0.1:8080");
		expect(baseUrlOrigin("https://gw.example.com/v1")).toBe("https://gw.example.com");
	});

	it("separates a different host, port or scheme", () => {
		expect(baseUrlOrigin("http://127.0.0.1:8081/v1")).not.toBe(baseUrlOrigin("http://127.0.0.1:8080/v1"));
		expect(baseUrlOrigin("https://127.0.0.1:8080/v1")).not.toBe(baseUrlOrigin("http://127.0.0.1:8080/v1"));
		expect(baseUrlOrigin("http://evil.example.com/v1")).not.toBe(baseUrlOrigin("http://127.0.0.1:8080/v1"));
	});

	it("returns null for an unparseable address", () => {
		expect(baseUrlOrigin("not a url")).toBeNull();
	});
});

describe("shouldWarnLocalDeclaration (D1)", () => {
	it("warns when Local is declared for a host that is neither loopback nor private", () => {
		expect(shouldWarnLocalDeclaration(values({ locality: "Local", baseUrl: "https://api.example.com/v1" }))).toBe(true);
	});

	it("stays quiet for a loopback or private address declared Local", () => {
		expect(shouldWarnLocalDeclaration(values({ locality: "Local", baseUrl: "http://127.0.0.1:8080/v1" }))).toBe(false);
		expect(shouldWarnLocalDeclaration(values({ locality: "Local", baseUrl: "http://192.168.1.50:8000" }))).toBe(false);
	});

	it("stays quiet for a Cloud declaration whatever the address", () => {
		expect(shouldWarnLocalDeclaration(values({ locality: "Cloud", baseUrl: "https://api.example.com/v1" }))).toBe(false);
		expect(shouldWarnLocalDeclaration(values({ locality: "Cloud", baseUrl: "http://127.0.0.1:8080" }))).toBe(false);
	});

	it("stays quiet while the address is unparseable — the base-URL error already says that", () => {
		expect(shouldWarnLocalDeclaration(values({ locality: "Local", baseUrl: "not a url" }))).toBe(false);
	});

	it("stays quiet for a unique-local or link-local IPv6 literal declared Local", () => {
		expect(shouldWarnLocalDeclaration(values({ locality: "Local", baseUrl: "http://[fd00::1]:8080/v1" }))).toBe(false);
		expect(shouldWarnLocalDeclaration(values({ locality: "Local", baseUrl: "http://[fe80::1]:8080/v1" }))).toBe(false);
	});

	it("warns for a public IPv6 literal declared Local", () => {
		expect(shouldWarnLocalDeclaration(values({ locality: "Local", baseUrl: "http://[2001:db8::1]:8080/v1" }))).toBe(true);
	});

	it("warns for a hostname that only LOOKS like a private IPv6 prefix", () => {
		// The whole point: `fd-api.example.com` is a public DNS name. Suppressing the warning for it would hide that
		// declaring Local hands this endpoint workspace files, the knowledge base, custom tools and run_python.
		expect(shouldWarnLocalDeclaration(values({ locality: "Local", baseUrl: "https://fd-api.example.com/v1" }))).toBe(true);
	});
});

describe("requiresApiKeyReentry (a stored key is bound to the origin it was issued for)", () => {
	const stored = { baseUrl: "http://127.0.0.1:8080/v1", hasApiKey: true };

	it("flags a move to a different origin with the key field untouched", () => {
		expect(requiresApiKeyReentry(values({ baseUrl: "https://evil.example.com/v1" }), stored)).toBe(true);
	});

	it("accepts a path-only change — the same server still holds the same key", () => {
		expect(requiresApiKeyReentry(values({ baseUrl: "http://127.0.0.1:8080/openai/v1" }), stored)).toBe(false);
	});

	it("is answered by typing a new key or by asking for removal", () => {
		expect(requiresApiKeyReentry(values({ baseUrl: "https://evil.example.com/v1", apiKey: "sk-new" }), stored)).toBe(false);
		expect(requiresApiKeyReentry(values({ baseUrl: "https://evil.example.com/v1", clearApiKey: true }), stored)).toBe(false);
	});

	it("does not apply to a keyless connection, or while creating one", () => {
		expect(requiresApiKeyReentry(values({ baseUrl: "https://evil.example.com/v1" }), { ...stored, hasApiKey: false })).toBe(
			false,
		);
		expect(requiresApiKeyReentry(values({ baseUrl: "https://evil.example.com/v1" }), undefined)).toBe(false);
	});

	it("surfaces on the key field, so the form blocks the save the backend would reject", () => {
		const moved = validateExternalProviderForm(values({ baseUrl: "https://evil.example.com/v1" }), false, stored);

		expect(moved.apiKey).toBeDefined();
		expect(validateExternalProviderForm(values(), false, stored).apiKey).toBeUndefined();
	});
});

describe("validateExternalProviderForm", () => {
	it("accepts a complete new connection", () => {
		expect(validateExternalProviderForm(values(), true)).toEqual({});
	});

	it("requires a connection id in the slug grammar while creating", () => {
		expect(validateExternalProviderForm(values({ connectionId: "" }), true).connectionId).toBeDefined();
		expect(validateExternalProviderForm(values({ connectionId: "Unsloth Box" }), true).connectionId).toBeDefined();
		expect(validateExternalProviderForm(values({ connectionId: "a".repeat(33) }), true).connectionId).toBeDefined();
	});

	it("does not re-validate the id of a stored connection, whose field is read-only", () => {
		expect(validateExternalProviderForm(values({ connectionId: "" }), false).connectionId).toBeUndefined();
	});

	it("requires a display name", () => {
		expect(validateExternalProviderForm(values({ displayName: "   " }), true).displayName).toBeDefined();
	});

	it("requires an absolute http(s) base URL and rejects userinfo, fragments and other schemes", () => {
		expect(validateExternalProviderForm(values({ baseUrl: "127.0.0.1:8080" }), true).baseUrl).toBeDefined();
		expect(validateExternalProviderForm(values({ baseUrl: "ftp://box/v1" }), true).baseUrl).toBeDefined();
		expect(validateExternalProviderForm(values({ baseUrl: "http://user:pass@box/v1" }), true).baseUrl).toBeDefined();
		expect(validateExternalProviderForm(values({ baseUrl: "http://box/v1#frag" }), true).baseUrl).toBeDefined();
		expect(validateExternalProviderForm(values({ baseUrl: "https://gateway.example.com" }), true).baseUrl).toBeUndefined();
	});

	// The bounds are the store's own (ExternalProviderStoreSchema.Min/MaxTimeoutSeconds): anything the form accepts has
	// to be storable, and anything the store accepts has to be typeable.
	it("accepts a blank timeout and the store's whole range, and rejects what the store would refuse", () => {
		const timeout = (timeoutSeconds: string) => validateExternalProviderForm(values({ timeoutSeconds }), true).timeoutSeconds;

		expect(timeout("")).toBeUndefined();
		expect(timeout(String(MIN_TIMEOUT_SECONDS))).toBeUndefined();
		expect(timeout("120")).toBeUndefined();
		expect(timeout(String(MAX_TIMEOUT_SECONDS))).toBeUndefined();
		expect(timeout("0")).toBeDefined();
		expect(timeout(String(MIN_TIMEOUT_SECONDS - 1))).toBeDefined();
		expect(timeout(String(MAX_TIMEOUT_SECONDS + 1))).toBeDefined();
		expect(timeout("abc")).toBeDefined();
	});

	it("accepts an empty model list — a connection may be registered before its models are", () => {
		expect(validateExternalProviderForm(values({ models: [emptyModelDraft] }), true).models).toBeUndefined();
	});

	it("rejects two rows naming the exact same backing model", () => {
		const duplicated = values({
			models: [
				{ ...emptyModelDraft, wireId: "qwen3-27b" },
				{ ...emptyModelDraft, wireId: " qwen3-27b " },
			],
		});

		expect(validateExternalProviderForm(duplicated, true).models).toBeDefined();
	});

	it("allows two rows differing only in case — the store's identity is Ordinal, so both are registrable ids", () => {
		const caseVariants = values({
			models: [
				{ ...emptyModelDraft, wireId: "qwen3-27b" },
				{ ...emptyModelDraft, wireId: "QWEN3-27B" },
			],
		});

		expect(validateExternalProviderForm(caseVariants, true).models).toBeUndefined();
	});

	it("rejects a non-numeric or negative context length but allows a blank one", () => {
		const withContext = (contextLength: string) =>
			validateExternalProviderForm(values({ models: [{ ...emptyModelDraft, wireId: "m", contextLength }] }), true).models;

		expect(withContext("")).toBeUndefined();
		expect(withContext("32768")).toBeUndefined();
		expect(withContext("-1")).toBeDefined();
		expect(withContext("lots")).toBeDefined();
	});
});
