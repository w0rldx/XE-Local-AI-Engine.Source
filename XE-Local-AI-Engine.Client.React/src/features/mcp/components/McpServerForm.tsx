import { ActionIcon, Alert, Button, Group, Select, Stack, Text, Textarea, TextInput } from "@mantine/core";
import { IconDeviceFloppy, IconPlus, IconTrash, IconX } from "@tabler/icons-react";
import { type Ref, useCallback, useEffect, useImperativeHandle, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import {
	type McpEnvEntry,
	type McpServerFormValues,
	type McpTransportKind,
	mcpServerFormSchema,
	mcpTransportKinds,
} from "@/features/mcp/models/McpServerModels";

// Imperative handle so the host dialog can place Save in its sticky footer (outside the form body) yet still
// trigger the form's own validate-then-submit. The footer button calls submit(); validation stays in the form.
export interface McpServerFormHandle {
	submit: () => void;
}

interface McpServerFormProps {
	initialValues: McpServerFormValues;
	isSubmitting: boolean;
	submitError?: string;
	onSubmit: (values: McpServerFormValues) => void;
	onCancel: () => void;
	/** Imperative handle exposing submit() so a host footer can drive submission. */
	ref?: Ref<McpServerFormHandle>;
	/** Hides the form's own Cancel/Save buttons when the host (DialogShell footer) renders them instead. */
	hideActions?: boolean;
	/** Reports whether the current values differ from initialValues so the host can guard close/navigation. */
	onDirtyChange?: (isDirty: boolean) => void;
}

// Form-local env row. Carries a stable client id so React can key rows across add/remove without using the
// array index (the index would change on remove and lose input focus / component state). The id never leaves
// the form — env is projected back to plain key/value McpEnvEntry on submit and for schema validation.
interface McpEnvRow extends McpEnvEntry {
	id: string;
}

let envRowSequence = 0;

function nextEnvRowId(): string {
	envRowSequence += 1;
	return `env-row-${envRowSequence}`;
}

function toEnvRows(entries: readonly McpEnvEntry[]): McpEnvRow[] {
	return entries.map((entry) => ({ id: nextEnvRowId(), key: entry.key, value: entry.value }));
}

function toEnvEntries(rows: readonly McpEnvRow[]): McpEnvEntry[] {
	return rows.map((row) => ({ key: row.key, value: row.value }));
}

// Flatten the Zod issue path (e.g. ["env", 2, "key"]) to a stable string key so transport-conditional and
// per-row env errors can be looked up by the inputs that own them.
function issueKey(path: readonly PropertyKey[]): string {
	return path.map((segment) => String(segment)).join(".");
}

// Bracket-notation lookup for the flattened error map (the strict tsconfig forbids dotted access on an index
// signature). Returns undefined when the field has no error so it can flow straight into Mantine's `error`.
function fieldError(errors: Record<string, string>, key: string): string | undefined {
	return errors[key];
}

// Create/edit form for an MCP server registration. Controlled Mantine inputs validated with the shared Zod
// schema on submit. The transport select toggles between stdio fields (command/args/env/cwd) and the http
// field (loopback url). The enabled flag is NOT edited here — registering never auto-connects; enabling is a
// separate, deliberate row action on the list (the strict default gate).
export function McpServerForm({
	initialValues,
	isSubmitting,
	submitError,
	onSubmit,
	onCancel,
	ref,
	hideActions = false,
	onDirtyChange,
}: McpServerFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState<McpServerFormValues>(initialValues);
	// Env rows are held separately from `values` so each row can carry a stable id for React keys (the index
	// would shift on remove). They are projected back into McpEnvEntry[] for validation and submit.
	const [envRows, setEnvRows] = useState<McpEnvRow[]>(() => toEnvRows(initialValues.env));
	const [errors, setErrors] = useState<Record<string, string>>({});

	// Dirty = current values (with env projected back) differ from the initial snapshot. A JSON compare gives
	// shallow/structural dirty detection; the host uses this to guard close + navigation.
	const isDirty = useMemo(() => {
		const candidate: McpServerFormValues = { ...values, env: toEnvEntries(envRows) };
		return JSON.stringify(candidate) !== JSON.stringify(initialValues);
	}, [values, envRows, initialValues]);

	useEffect(() => {
		onDirtyChange?.(isDirty);
	}, [isDirty, onDirtyChange]);

	const transportData = useMemo(
		() =>
			mcpTransportKinds.map((kind) => ({
				value: kind,
				label: t(`pages.mcp.form.transport.options.${kind}`, kind),
			})),
		[t],
	);

	const isStdio = values.transportKind === "Stdio";

	const handleTransportChange = useCallback((value: string | null) => {
		if (value === null) {
			return;
		}
		setValues((current) => ({ ...current, transportKind: value as McpTransportKind }));
	}, []);

	const handleArgumentsChange = useCallback((raw: string) => {
		// Arguments are edited one-per-line; blank lines are dropped at submit (see toSaveMcpServerRequest).
		setValues((current) => ({ ...current, arguments: raw.split("\n") }));
	}, []);

	const handleEnvKeyChange = useCallback((id: string, key: string) => {
		setEnvRows((current) => current.map((row) => (row.id === id ? { ...row, key } : row)));
	}, []);

	const handleEnvValueChange = useCallback((id: string, value: string) => {
		setEnvRows((current) => current.map((row) => (row.id === id ? { ...row, value } : row)));
	}, []);

	const handleAddEnv = useCallback(() => {
		setEnvRows((current) => [...current, { id: nextEnvRowId(), key: "", value: "" }]);
	}, []);

	const handleRemoveEnv = useCallback((id: string) => {
		setEnvRows((current) => current.filter((row) => row.id !== id));
	}, []);

	const handleSubmit = useCallback(() => {
		const candidate: McpServerFormValues = { ...values, env: toEnvEntries(envRows) };
		const result = mcpServerFormSchema.safeParse(candidate);
		if (!result.success) {
			const nextErrors: Record<string, string> = {};
			for (const issue of result.error.issues) {
				nextErrors[issueKey(issue.path)] = issue.message;
			}
			setErrors(nextErrors);
			return;
		}

		setErrors({});
		onSubmit(candidate);
	}, [envRows, onSubmit, values]);

	useImperativeHandle(ref, () => ({ submit: handleSubmit }), [handleSubmit]);

	// Arguments are presented as a textarea (one per line) so the editor stays simple; the model carries them
	// as a string[] and the API layer drops blanks.
	const argumentsText = useMemo(() => values.arguments.join("\n"), [values.arguments]);

	return (
		<Stack gap="md" data-testid="mcp-server-form">
			<TextInput
				label={t("pages.mcp.form.name.label", "Name")}
				placeholder={t("pages.mcp.form.name.placeholder", "Filesystem tools")}
				value={values.name}
				required={true}
				error={fieldError(errors, "name") ? t("pages.mcp.form.name.required", "Name is required") : undefined}
				onChange={(event) => {
					const value = event.currentTarget.value;
					setValues((current) => ({ ...current, name: value }));
				}}
				data-testid="mcp-form-name"
			/>
			<Textarea
				label={t("pages.mcp.form.description.label", "Description")}
				placeholder={t("pages.mcp.form.description.placeholder", "Optional short summary")}
				value={values.description}
				autosize={true}
				minRows={2}
				onChange={(event) => {
					const value = event.currentTarget.value;
					setValues((current) => ({ ...current, description: value }));
				}}
				data-testid="mcp-form-description"
			/>
			<Select
				label={t("pages.mcp.form.transport.label", "Transport")}
				description={t(
					"pages.mcp.form.transport.description",
					"Stdio launches a local process; HTTP connects to a running loopback server.",
				)}
				data={transportData}
				value={values.transportKind}
				allowDeselect={false}
				onChange={handleTransportChange}
				data-testid="mcp-form-transport"
			/>

			{isStdio ? (
				<Stack gap="md" data-testid="mcp-form-stdio-fields">
					<TextInput
						label={t("pages.mcp.form.command.label", "Command")}
						placeholder={t("pages.mcp.form.command.placeholder", "/usr/bin/my-mcp-server")}
						value={values.command}
						required={true}
						error={fieldError(errors, "command")}
						onChange={(event) => {
							const value = event.currentTarget.value;
							setValues((current) => ({ ...current, command: value }));
						}}
						data-testid="mcp-form-command"
					/>
					<Textarea
						label={t("pages.mcp.form.arguments.label", "Arguments")}
						description={t("pages.mcp.form.arguments.description", "One argument per line.")}
						placeholder={t("pages.mcp.form.arguments.placeholder", "--stdio")}
						value={argumentsText}
						autosize={true}
						minRows={2}
						onChange={(event) => handleArgumentsChange(event.currentTarget.value)}
						data-testid="mcp-form-arguments"
					/>
					<TextInput
						label={t("pages.mcp.form.workingDirectory.label", "Working directory")}
						placeholder={t("pages.mcp.form.workingDirectory.placeholder", "/optional/cwd")}
						value={values.workingDirectory}
						onChange={(event) => {
							const value = event.currentTarget.value;
							setValues((current) => ({ ...current, workingDirectory: value }));
						}}
						data-testid="mcp-form-working-directory"
					/>
					<McpEnvEditor
						rows={envRows}
						errors={errors}
						onKeyChange={handleEnvKeyChange}
						onValueChange={handleEnvValueChange}
						onAdd={handleAddEnv}
						onRemove={handleRemoveEnv}
					/>
				</Stack>
			) : (
				<TextInput
					label={t("pages.mcp.form.url.label", "URL")}
					description={t("pages.mcp.form.url.description", "Loopback only (127.0.0.1 / localhost / ::1).")}
					placeholder={t("pages.mcp.form.url.placeholder", "http://127.0.0.1:3001/sse")}
					value={values.url}
					required={true}
					error={fieldError(errors, "url")}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setValues((current) => ({ ...current, url: value }));
					}}
					data-testid="mcp-form-url"
				/>
			)}

			{submitError ? (
				<Alert color="red" data-testid="mcp-form-submit-error">
					{submitError}
				</Alert>
			) : null}
			{hideActions ? null : (
				<Group justify="flex-end">
					<Button
						variant="subtle"
						leftSection={<IconX size={16} />}
						onClick={onCancel}
						disabled={isSubmitting}
						data-testid="mcp-form-cancel"
					>
						{t("common.cancel", "Cancel")}
					</Button>
					<Button
						leftSection={<IconDeviceFloppy size={16} />}
						onClick={handleSubmit}
						loading={isSubmitting}
						data-testid="mcp-form-submit"
					>
						{t("common.save", "Save")}
					</Button>
				</Group>
			)}
		</Stack>
	);
}

interface McpEnvEditorProps {
	rows: readonly McpEnvRow[];
	errors: Record<string, string>;
	onKeyChange: (id: string, key: string) => void;
	onValueChange: (id: string, value: string) => void;
	onAdd: () => void;
	onRemove: (id: string) => void;
}

// Key/value editor for stdio environment variables. env carries secrets, so the value inputs are rendered as
// plain text here (the user is the operator on their own node) but are encrypted at rest on save. Rows are
// keyed by their stable client id; the position index is used only to look up the validation error (whose Zod
// path is positional) and to build deterministic test ids.
function McpEnvEditor({ rows, errors, onKeyChange, onValueChange, onAdd, onRemove }: McpEnvEditorProps) {
	const { t } = useTranslation();

	return (
		<Stack gap="xs" data-testid="mcp-form-env">
			<Group justify="space-between" align="center">
				<Text size="sm" fw={500}>
					{t("pages.mcp.form.env.label", "Environment variables")}
				</Text>
				<Button size="xs" variant="subtle" leftSection={<IconPlus size={14} />} onClick={onAdd} data-testid="mcp-form-env-add">
					{t("pages.mcp.form.env.add", "Add variable")}
				</Button>
			</Group>
			{rows.length === 0 ? (
				<Text size="xs" c="dimmed">
					{t("pages.mcp.form.env.empty", "No environment variables.")}
				</Text>
			) : null}
			{rows.map((row, index) => (
				<Group key={row.id} gap="xs" align="flex-start" wrap="nowrap">
					<TextInput
						placeholder={t("pages.mcp.form.env.keyPlaceholder", "KEY")}
						value={row.key}
						error={errors[`env.${index}.key`]}
						onChange={(event) => onKeyChange(row.id, event.currentTarget.value)}
						style={{ flex: 1 }}
						data-testid={`mcp-form-env-key-${index}`}
					/>
					<TextInput
						placeholder={t("pages.mcp.form.env.valuePlaceholder", "value")}
						value={row.value}
						onChange={(event) => onValueChange(row.id, event.currentTarget.value)}
						style={{ flex: 1 }}
						data-testid={`mcp-form-env-value-${index}`}
					/>
					<ActionIcon
						variant="subtle"
						color="red"
						aria-label={t("pages.mcp.form.env.remove", "Remove variable")}
						onClick={() => onRemove(row.id)}
						data-testid={`mcp-form-env-remove-${index}`}
					>
						<IconTrash size={16} />
					</ActionIcon>
				</Group>
			))}
		</Stack>
	);
}
