// The one object guard for parsed JSON and other untyped payloads. Arrays pass: a caller that must reject them checks
// `Array.isArray` itself.
export function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null;
}
