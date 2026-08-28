export function cx(...names: (string | false | undefined)[]): string {
	return names.filter(Boolean).join(" ");
}
