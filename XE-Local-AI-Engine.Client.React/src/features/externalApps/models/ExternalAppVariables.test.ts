// The variable form's pure model. Client validation is a courtesy — the server validates again — but the SECRET rule
// is not: a stored secret arrives as the sentinel, and treating that as "empty" would make every stored secret block
// its own form, while sending anything else back would overwrite the secret the node holds.

import { describe, expect, it } from "vitest";

import { EXTERNAL_APP_SECRET_SENTINEL } from "@/features/externalApps/models/ExternalAppModels";
import {
	type ExternalAppVariableDefinition,
	externalAppVariableTypes,
	initialVariableValues,
	toExternalAppVariableType,
	validateExternalAppVariables,
} from "@/features/externalApps/models/ExternalAppVariables";
import { externalAppVariable } from "@/features/externalApps/test/ExternalAppFixtures";

function definitions(...overrides: readonly Partial<ExternalAppVariableDefinition>[]): ExternalAppVariableDefinition[] {
	return overrides.map((override) => externalAppVariable(override));
}

function messageKeys(defs: readonly ExternalAppVariableDefinition[], values: Record<string, string>): readonly string[] {
	return validateExternalAppVariables(defs, values).map((issue) => issue.messageKey);
}

describe("initialVariableValues", () => {
	it("prefers a stored value over the default and the default over empty", () => {
		const defs = definitions({ name: "baseUrl", default: "http://127.0.0.1" }, { name: "token", type: "secret", default: null });

		expect(initialVariableValues(defs, { baseUrl: "http://ntfy.local" })).toEqual({
			baseUrl: "http://ntfy.local",
			token: "",
		});
	});

	it("seeds from the manifest defaults when nothing is stored", () => {
		expect(initialVariableValues(definitions({ name: "baseUrl", default: "http://127.0.0.1" }))).toEqual({
			baseUrl: "http://127.0.0.1",
		});
	});
});

describe("validateExternalAppVariables", () => {
	it("reports a required variable that is empty", () => {
		expect(messageKeys(definitions({ name: "baseUrl", required: true }), { baseUrl: "" })).toEqual([
			"pages.externalApps.variables.issues.required",
		]);
	});

	it("treats a stored secret as filled", () => {
		const defs = definitions({ name: "token", type: "secret", required: true });

		expect(validateExternalAppVariables(defs, { token: EXTERNAL_APP_SECRET_SENTINEL })).toEqual([]);
	});

	it("accepts an empty optional variable", () => {
		expect(validateExternalAppVariables(definitions({ name: "baseUrl", required: false }), { baseUrl: "" })).toEqual([]);
	});

	it("rejects an enum value outside allowedValues", () => {
		const defs = definitions({ name: "mode", type: "enum", allowedValues: ["quiet", "loud"] });

		expect(messageKeys(defs, { mode: "deafening" })).toEqual(["pages.externalApps.variables.issues.allowedValues"]);
		expect(validateExternalAppVariables(defs, { mode: "loud" })).toEqual([]);
	});

	it("accepts whole numbers and rejects everything else for an integer", () => {
		const defs = definitions({ name: "port", type: "integer" });

		expect(validateExternalAppVariables(defs, { port: "-3" })).toEqual([]);
		expect(messageKeys(defs, { port: "1.5" })).toEqual(["pages.externalApps.variables.issues.integer"]);
		expect(messageKeys(defs, { port: "x" })).toEqual(["pages.externalApps.variables.issues.integer"]);
	});

	it("applies minLength, maxLength and pattern to text", () => {
		const defs = definitions({ name: "baseUrl", validation: { minLength: 4, maxLength: 8, pattern: "^https?://" } });

		expect(messageKeys(defs, { baseUrl: "ab" })).toEqual(["pages.externalApps.variables.issues.minLength"]);
		expect(messageKeys(defs, { baseUrl: "http://far.too.long" })).toEqual(["pages.externalApps.variables.issues.maxLength"]);
		expect(messageKeys(defs, { baseUrl: "ftp://a" })).toEqual(["pages.externalApps.variables.issues.pattern"]);
		expect(validateExternalAppVariables(defs, { baseUrl: "http://a" })).toEqual([]);
	});

	it("carries the label and the bound the message interpolates", () => {
		const defs = definitions({
			name: "baseUrl",
			label: "Base URL",
			validation: { minLength: 4, maxLength: null, pattern: null },
		});

		expect(validateExternalAppVariables(defs, { baseUrl: "ab" })).toEqual([
			{
				name: "baseUrl",
				messageKey: "pages.externalApps.variables.issues.minLength",
				params: { label: "Base URL", min: 4 },
			},
		]);
	});

	it("never fails a boolean", () => {
		const defs = definitions({ name: "quiet", type: "boolean", required: true });

		expect(validateExternalAppVariables(defs, { quiet: "false" })).toEqual([]);
	});

	// A catalog-authored pattern that does not compile must not throw out of a keystroke handler and take the dialog
	// with it; the node validates the same rule, so it degrades to no rule.
	it("ignores a pattern that does not compile", () => {
		const defs = definitions({ name: "baseUrl", validation: { minLength: null, maxLength: null, pattern: "([" } });

		expect(validateExternalAppVariables(defs, { baseUrl: "anything" })).toEqual([]);
	});
});

describe("toExternalAppVariableType", () => {
	it("narrows the five catalog types to themselves and anything else to a text box", () => {
		// The five the node's catalog validator accepts, listed literally: a sixth added there without a control here
		// would silently render as a text box.
		expect([...externalAppVariableTypes]).toEqual(["string", "secret", "integer", "boolean", "enum"]);
		expect(["string", "secret", "integer", "boolean", "enum"].map(toExternalAppVariableType)).toEqual([
			"string",
			"secret",
			"integer",
			"boolean",
			"enum",
		]);
		expect(toExternalAppVariableType("duration")).toBe("string");
		expect(toExternalAppVariableType(undefined)).toBe("string");
	});
});
