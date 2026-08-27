import { describe, expect, it } from "vitest";

import { emptyFormValues, emptyModelDraft } from "@/features/external-providers/models/ExternalProviderFormState";
import {
	type ExternalProviderFormValues,
	isTrustedLocalHost,
	parseLocality,
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
	])("treats %s as reachable only from this machine or network", (host) => {
		expect(isTrustedLocalHost(host)).toBe(true);
	});

	it.each(["api.openai.com", "8.8.8.8", "172.32.0.1", "172.15.0.1", "example.com", "203.0.113.7"])(
		"treats %s as off-network",
		(host) => {
			expect(isTrustedLocalHost(host)).toBe(false);
		},
	);
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

	it("accepts a blank timeout and rejects a non-positive or out-of-range one", () => {
		expect(validateExternalProviderForm(values({ timeoutSeconds: "" }), true).timeoutSeconds).toBeUndefined();
		expect(validateExternalProviderForm(values({ timeoutSeconds: "120" }), true).timeoutSeconds).toBeUndefined();
		expect(validateExternalProviderForm(values({ timeoutSeconds: "0" }), true).timeoutSeconds).toBeDefined();
		expect(validateExternalProviderForm(values({ timeoutSeconds: "abc" }), true).timeoutSeconds).toBeDefined();
		expect(validateExternalProviderForm(values({ timeoutSeconds: "99999" }), true).timeoutSeconds).toBeDefined();
	});

	it("accepts an empty model list — a connection may be registered before its models are", () => {
		expect(validateExternalProviderForm(values({ models: [emptyModelDraft] }), true).models).toBeUndefined();
	});

	it("rejects two rows naming the same backing model, case-insensitively", () => {
		const duplicated = values({
			models: [
				{ ...emptyModelDraft, wireId: "qwen3-27b" },
				{ ...emptyModelDraft, wireId: "QWEN3-27B" },
			],
		});

		expect(validateExternalProviderForm(duplicated, true).models).toBeDefined();
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
