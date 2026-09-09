// The two helpers every manual Zod-on-submit form in this app needs to route issues back to their inputs. They
// live in core because the forms are feature-local but the mapping is not: a Zod issue carries a path, Mantine's
// `error` prop takes a string, and each form was otherwise re-deriving the same two lines.

// Flatten a Zod issue path (e.g. ["env", 2, "key"]) to a stable string key so transport-conditional and per-row
// errors can be looked up by the inputs that own them.
export function issueKey(path: readonly PropertyKey[]): string {
	return path.map((segment) => String(segment)).join(".");
}

// Bracket-notation lookup for the flattened error map (the strict tsconfig forbids dotted access on an index
// signature). Returns undefined when the field has no error so it can flow straight into Mantine's `error`.
export function fieldError(errors: Record<string, string>, key: string): string | undefined {
	return errors[key];
}
