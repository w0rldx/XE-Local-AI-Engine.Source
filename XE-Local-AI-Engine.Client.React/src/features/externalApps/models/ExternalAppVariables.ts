// The variable form's model: pure, no React, so it is testable in a plain `.test.ts` — the
// `DevWorkflowDefinitionValidation.ts` shape.
//
// Client validation is a COURTESY. The server validates again and its 400 names the offending variables, so nothing
// here is a gate: it only saves a round trip and puts the message next to the field.

import { EXTERNAL_APP_SECRET_SENTINEL, type ExternalAppVariableView } from "@/features/externalApps/models/ExternalAppModels";

/**
 * A variable definition is the WIRE view, not a second local interface. Both previews and
 * `instance.manifest.variables` deliver exactly this shape, so re-declaring it would buy a mapper that has to be kept
 * in sync with the generated types for no gain — every member is read defensively here instead.
 */
export type ExternalAppVariableDefinition = ExternalAppVariableView;

/** The five types the catalog validator accepts (`ExternalAppCatalogValidator.VariableTypes`). */
export const externalAppVariableTypes = ["string", "secret", "integer", "boolean", "enum"] as const;
export type ExternalAppVariableType = (typeof externalAppVariableTypes)[number];

/** name → raw string. Every control writes a string; the server owns typing. A stored secret holds the sentinel. */
export type ExternalAppVariableValues = Readonly<Record<string, string>>;

export interface ExternalAppVariableIssue {
	readonly name: string;
	/** A full i18n key, so the form renders `t(issue.messageKey, issue.params)` with no prefix of its own. */
	readonly messageKey: string;
	readonly params?: Readonly<Record<string, string | number>>;
}

const issueKeyPrefix = "pages.externalApps.variables.issues";
const integerPattern = /^-?\d+$/;

/** Unknown → `string`: a free-text box accepts anything the server will accept, so it is the safe fallback control. */
export function toExternalAppVariableType(value: string | null | undefined): ExternalAppVariableType {
	return externalAppVariableTypes.includes(value as ExternalAppVariableType) ? (value as ExternalAppVariableType) : "string";
}

/**
 * The form's starting values: a stored value wins over the manifest default, and a variable with neither starts empty.
 * A stored secret arrives as the sentinel and is kept as such — that is what "leave empty to keep it" round-trips.
 */
export function initialVariableValues(
	definitions: readonly ExternalAppVariableDefinition[],
	stored?: ExternalAppVariableValues,
): ExternalAppVariableValues {
	const values: Record<string, string> = {};
	for (const definition of definitions) {
		const name = definition.name ?? "";
		if (!name) {
			continue;
		}
		values[name] = stored?.[name] ?? definition.default ?? "";
	}
	return values;
}

/**
 * The names the node reports a stored secret for, read off the MASKED values it answered with. The form's own values
 * cannot answer this once a secret is cleared (the pending clear carries ""), so the boxes are told from here.
 */
export function storedSecretNames(stored: ExternalAppVariableValues | undefined): readonly string[] {
	return Object.entries(stored ?? {})
		.filter(([, value]) => value === EXTERNAL_APP_SECRET_SENTINEL)
		.map(([name]) => name);
}

/**
 * One issue per offending variable, in declaration order. Order per variable: required, then the type rule, then the
 * length/pattern rules — so an empty required field reports "required" rather than "must match a pattern".
 */
export function validateExternalAppVariables(
	definitions: readonly ExternalAppVariableDefinition[],
	values: ExternalAppVariableValues,
): readonly ExternalAppVariableIssue[] {
	const issues: ExternalAppVariableIssue[] = [];
	for (const definition of definitions) {
		const name = definition.name ?? "";
		if (!name) {
			continue;
		}
		const issue = validateOne(definition, name, values[name] ?? "");
		if (issue) {
			issues.push(issue);
		}
	}
	return issues;
}

function validateOne(
	definition: ExternalAppVariableDefinition,
	name: string,
	value: string,
): ExternalAppVariableIssue | undefined {
	const type = toExternalAppVariableType(definition.type);
	const label = definition.label ?? name;

	// A secret holding the sentinel is FILLED: the server has the value, the box is empty only because it is never
	// sent back in clear. Treating it as missing would make every stored secret block its own form.
	if (definition.required === true && value.length === 0) {
		return { name, messageKey: `${issueKeyPrefix}.required`, params: { label } };
	}
	if (value.length === 0 || value === EXTERNAL_APP_SECRET_SENTINEL) {
		return undefined;
	}

	if (type === "enum") {
		const allowed = definition.allowedValues ?? [];
		return allowed.includes(value)
			? undefined
			: { name, messageKey: `${issueKeyPrefix}.allowedValues`, params: { label, values: allowed.join(", ") } };
	}
	if (type === "integer") {
		return integerPattern.test(value) ? undefined : { name, messageKey: `${issueKeyPrefix}.integer`, params: { label } };
	}
	// `boolean` is "true"/"false" from a checkbox and never fails; only string and secret carry length/pattern rules.
	if (type !== "string" && type !== "secret") {
		return undefined;
	}
	return validateText(definition, name, value, label);
}

function validateText(
	definition: ExternalAppVariableDefinition,
	name: string,
	value: string,
	label: string,
): ExternalAppVariableIssue | undefined {
	const validation = definition.validation;
	if (!validation) {
		return undefined;
	}
	const { minLength, maxLength, pattern } = validation;
	if (typeof minLength === "number" && value.length < minLength) {
		return { name, messageKey: `${issueKeyPrefix}.minLength`, params: { label, min: minLength } };
	}
	if (typeof maxLength === "number" && value.length > maxLength) {
		return { name, messageKey: `${issueKeyPrefix}.maxLength`, params: { label, max: maxLength } };
	}
	// The pattern is catalog-authored. A malformed one must not throw out of a keystroke handler and take the dialog
	// with it, and the server validates the same rule anyway, so an uncompilable pattern is treated as no rule.
	if (pattern && !matchesPattern(pattern, value)) {
		return { name, messageKey: `${issueKeyPrefix}.pattern`, params: { label } };
	}
	return undefined;
}

function matchesPattern(pattern: string, value: string): boolean {
	try {
		return new RegExp(pattern).test(value);
	} catch {
		return true;
	}
}
