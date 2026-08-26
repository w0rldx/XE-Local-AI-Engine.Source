export type FieldErrors = Record<string, string>;

export function errorAt(errors: FieldErrors, path: string): string | undefined {
	return errors[path];
}
