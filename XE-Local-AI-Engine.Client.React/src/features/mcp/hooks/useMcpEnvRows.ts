import { useCallback, useMemo, useState } from "react";

import { type McpEnvEntry, maskedEnvValue } from "@/features/mcp/models/McpServerModels";

// Form-local env row. Carries a stable client id so React can key rows across add/remove without using the
// array index (the index would change on remove and lose input focus / component state). The id never leaves
// the form — env is projected back to plain key/value McpEnvEntry on submit and for schema validation.
export interface McpEnvRow extends McpEnvEntry {
	/**
	 * This row arrived carrying the mask, i.e. it is an EXISTING variable whose stored value the API never
	 * returns. Its box renders empty with an "unchanged" placeholder rather than showing the sentinel, and an
	 * empty box submits the sentinel back — so clearing the field keeps the stored value instead of blanking it.
	 * Deleting a variable stays the explicit remove button.
	 */
	readonly masked: boolean;
	id: string;
}

let envRowSequence = 0;

function nextEnvRowId(): string {
	envRowSequence += 1;
	return `env-row-${envRowSequence}`;
}

function toEnvRows(entries: readonly McpEnvEntry[]): McpEnvRow[] {
	return entries.map((entry) => ({
		id: nextEnvRowId(),
		key: entry.key,
		value: entry.value,
		masked: entry.value === maskedEnvValue,
	}));
}

function toEnvEntries(rows: readonly McpEnvRow[]): McpEnvEntry[] {
	// An emptied masked row goes back as the sentinel: the operator cleared the box, which means "leave it alone",
	// not "store an empty string" and not "delete it".
	return rows.map((row) => ({ key: row.key, value: row.masked && row.value === "" ? maskedEnvValue : row.value }));
}

export interface McpEnvRowsController {
	rows: readonly McpEnvRow[];
	/** The rows projected back to the plain contract shape, for validation, dirty comparison and submit. */
	entries: McpEnvEntry[];
	onKeyChange: (id: string, key: string) => void;
	onValueChange: (id: string, value: string) => void;
	onAdd: () => void;
	onRemove: (id: string) => void;
}

/**
 * Env rows are held apart from the form values so each row can carry a stable id for React keys (the index would
 * shift on remove). `initialEnv` seeds the rows once, on mount: the host remounts the form when it opens on a
 * different server, so a later identity change of the same prop must not clobber rows being edited.
 */
export function useMcpEnvRows(initialEnv: readonly McpEnvEntry[]): McpEnvRowsController {
	const [rows, setRows] = useState<McpEnvRow[]>(() => toEnvRows(initialEnv));

	const onKeyChange = useCallback((id: string, key: string) => {
		setRows((current) => current.map((row) => (row.id === id ? { ...row, key } : row)));
	}, []);

	const onValueChange = useCallback((id: string, value: string) => {
		setRows((current) => current.map((row) => (row.id === id ? { ...row, value } : row)));
	}, []);

	const onAdd = useCallback(() => {
		setRows((current) => [...current, { id: nextEnvRowId(), key: "", value: "", masked: false }]);
	}, []);

	const onRemove = useCallback((id: string) => {
		setRows((current) => current.filter((row) => row.id !== id));
	}, []);

	const entries = useMemo(() => toEnvEntries(rows), [rows]);

	return { rows, entries, onKeyChange, onValueChange, onAdd, onRemove };
}
