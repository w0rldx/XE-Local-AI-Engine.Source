import {
	ActionIcon,
	Alert,
	Button,
	Checkbox,
	Code,
	Divider,
	Group,
	NumberInput,
	SegmentedControl,
	Select,
	Stack,
	Switch,
	Text,
	Textarea,
	TextInput,
} from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconInfoCircle, IconPlus, IconTrash, IconX } from "@tabler/icons-react";
import { type Ref, useCallback, useEffect, useImperativeHandle, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	CUSTOM_TOOL_HTTP_METHODS,
	CUSTOM_TOOL_NAME_PREFIX,
	CUSTOM_TOOL_PARAMETER_TYPES,
	CUSTOM_TOOL_SECRET_SENTINEL,
	CUSTOM_TOOL_TIMEOUT_MAX,
	type CustomToolEnvVar,
	type CustomToolFormValues,
	type CustomToolHeader,
	type CustomToolParameter,
	type CustomToolParameterType,
	customToolFormSchema,
} from "@/features/customTools/models/CustomToolModels";
import { useValidateExecutable } from "@/features/customTools/queries/useCustomTools";

// Imperative handle so the host dialog can place Save in its sticky footer yet still trigger validate-then-submit.
export interface CustomToolFormHandle {
	submit: () => void;
}

interface CustomToolFormProps {
	initialValues: CustomToolFormValues;
	isSubmitting: boolean;
	submitError?: string;
	// Shown only on edit (mirrors the Skills form): a new tool is created disabled-off is not offered here — enabling is
	// a deliberate act the operator takes in the editor once satisfied.
	showEnabledToggle: boolean;
	onSubmit: (values: CustomToolFormValues) => void;
	onCancel: () => void;
	ref?: Ref<CustomToolFormHandle>;
	onDirtyChange?: (isDirty: boolean) => void;
	/** Reports the danger acknowledgement so the host footer can gate Save on it (the server also enforces it). */
	onAcknowledgedChange?: (acknowledged: boolean) => void;
}

// Joined-path error map keyed off the Zod issue path (e.g. "http.urlTemplate", "parameters.0.name"). The form is
// nested, so a flat keyof map is not enough; joining the path with "." keeps lookups explicit and type-free.
type FieldErrors = Record<string, string>;

function errorAt(errors: FieldErrors, path: string): string | undefined {
	return errors[path];
}

// Create/edit form for a node custom tool. Controlled Mantine inputs validated with the shared Zod schema on submit.
// A prominent danger note and a mandatory acknowledgement gate the Save button — the same acknowledgement the backend
// enforces on every write. Both kind editors keep their own draft so switching kind never loses data.
export function CustomToolForm({
	initialValues,
	isSubmitting: _isSubmitting,
	submitError,
	showEnabledToggle,
	onSubmit,
	onCancel: _onCancel,
	ref,
	onDirtyChange,
	onAcknowledgedChange,
}: CustomToolFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState<CustomToolFormValues>(initialValues);
	const [errors, setErrors] = useState<FieldErrors>({});

	// Pure state update — parent notification happens from an effect below, never during render.
	const update = useCallback((updater: (current: CustomToolFormValues) => CustomToolFormValues) => {
		setValues(updater);
	}, []);

	useEffect(() => {
		onDirtyChange?.(JSON.stringify(values) !== JSON.stringify(initialValues));
	}, [values, initialValues, onDirtyChange]);

	useEffect(() => {
		onAcknowledgedChange?.(values.acknowledged);
	}, [values.acknowledged, onAcknowledgedChange]);

	const handleSubmit = useCallback(() => {
		const result = customToolFormSchema.safeParse(values);
		if (!result.success) {
			const next: FieldErrors = {};
			for (const issue of result.error.issues) {
				const key = issue.path.join(".");
				if (!next[key]) {
					next[key] = issue.message;
				}
			}
			setErrors(next);
			return;
		}
		setErrors({});
		onSubmit(result.data as CustomToolFormValues);
	}, [onSubmit, values]);

	useImperativeHandle(ref, () => ({ submit: handleSubmit }), [handleSubmit]);

	const nameError = useMemo(() => {
		if (!errorAt(errors, "name")) {
			return undefined;
		}
		return values.name.trim().length === 0
			? t("pages.customTools.form.name.required", "Name is required.")
			: t("pages.customTools.form.name.invalid", "Use lowercase letters, digits and underscores; start and end alphanumeric.");
	}, [errors, values.name, t]);

	return (
		<Stack gap="md" data-testid="custom-tool-form">
			<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="custom-tool-form-danger-note">
				{t(
					"pages.customTools.form.dangerNote",
					"Custom tools run on this machine: an HTTP tool reaches the network and a Command tool launches a program with your access. Only create and enable tools whose exact behaviour you trust.",
				)}
			</Alert>

			<TextInput
				label={t("pages.customTools.form.name.label", "Name")}
				description={t("pages.customTools.form.name.description", "The model sees this as {{prefixed}}.", {
					prefixed: `${CUSTOM_TOOL_NAME_PREFIX}${values.name || "name"}`,
				})}
				placeholder="weather"
				leftSection={
					<Text size="xs" c="dimmed" pl={4}>
						{CUSTOM_TOOL_NAME_PREFIX}
					</Text>
				}
				leftSectionWidth={72}
				value={values.name}
				required={true}
				error={nameError}
				onChange={(event) => {
					const value = event.currentTarget.value;
					update((current) => ({ ...current, name: value }));
				}}
				data-testid="custom-tool-form-name"
			/>
			<Textarea
				label={t("pages.customTools.form.description.label", "Description")}
				description={t("pages.customTools.form.description.description", "What the tool does — the model reads this to decide when to call it.")}
				value={values.description}
				required={true}
				autosize={true}
				minRows={2}
				error={errorAt(errors, "description") ? t("pages.customTools.form.description.required", "Description is required.") : undefined}
				onChange={(event) => {
					const value = event.currentTarget.value;
					update((current) => ({ ...current, description: value }));
				}}
				data-testid="custom-tool-form-description"
			/>

			<Stack gap={4}>
				<Text size="sm" fw={500}>
					{t("pages.customTools.form.kind.label", "Kind")}
				</Text>
				<SegmentedControl
					value={values.kind}
					onChange={(value) => update((current) => ({ ...current, kind: value as CustomToolFormValues["kind"] }))}
					data={[
						{ label: t("pages.customTools.form.kind.http", "HTTP fetch"), value: "HttpFetch" },
						{ label: t("pages.customTools.form.kind.command", "Command"), value: "Command" },
					]}
					data-testid="custom-tool-form-kind"
				/>
			</Stack>

			<Stack gap={4}>
				<Text size="sm" fw={500}>
					{t("pages.customTools.form.mode.label", "Mode")}
				</Text>
				<SegmentedControl
					value={values.mode}
					onChange={(value) => update((current) => ({ ...current, mode: value as CustomToolFormValues["mode"] }))}
					data={[
						{ label: t("pages.customTools.form.mode.fixed", "Fixed"), value: "Fixed" },
						{ label: t("pages.customTools.form.mode.parameterized", "Parameterized"), value: "Parameterized" },
					]}
					data-testid="custom-tool-form-mode"
				/>
				<Text size="xs" c="dimmed">
					{values.mode === "Parameterized"
						? t("pages.customTools.form.mode.parameterizedHint", "The model fills the declared inputs into the {param} placeholders each call.")
						: t("pages.customTools.form.mode.fixedHint", "The tool runs verbatim with no model-supplied input.")}
				</Text>
				{errorAt(errors, "mode") ? (
					<Text size="xs" c="red">
						{t("pages.customTools.form.mode.fixedNoParameters", "A Fixed tool cannot declare parameters. Remove them or switch to Parameterized.")}
					</Text>
				) : null}
			</Stack>

			{values.mode === "Parameterized" ? <ParameterBuilder values={values} errors={errors} update={update} /> : null}

			<Divider />

			{values.kind === "HttpFetch" ? (
				<HttpEditor values={values} errors={errors} update={update} />
			) : (
				<CommandEditor values={values} errors={errors} update={update} />
			)}

			<Divider />

			<Checkbox
				label={t(
					"pages.customTools.form.acknowledge.label",
					"I understand these tools can run code, call networks, and launch programs on my machine, and I take responsibility.",
				)}
				checked={values.acknowledged}
				onChange={(event) => {
					const checked = event.currentTarget.checked;
					update((current) => ({ ...current, acknowledged: checked }));
				}}
				error={errorAt(errors, "acknowledged") ? t("pages.customTools.form.acknowledge.required", "You must acknowledge this to save.") : undefined}
				data-testid="custom-tool-form-acknowledge"
			/>

			{showEnabledToggle ? (
				<Switch
					label={t("pages.customTools.form.enabled.label", "Enabled")}
					description={t("pages.customTools.form.enabled.description", "A disabled tool is never offered to any agent, even if assigned.")}
					checked={values.enabled}
					onChange={(event) => {
						const checked = event.currentTarget.checked;
						update((current) => ({ ...current, enabled: checked }));
					}}
					data-testid="custom-tool-form-enabled"
				/>
			) : null}

			{submitError ? (
				<Alert color="red" data-testid="custom-tool-form-submit-error">
					{submitError}
				</Alert>
			) : null}
		</Stack>
	);
}

interface EditorSectionProps {
	values: CustomToolFormValues;
	errors: FieldErrors;
	update: (updater: (current: CustomToolFormValues) => CustomToolFormValues) => void;
}

// Parameter builder: rows of name / type / description / required, editing the declared inputs a Parameterized tool
// exposes to the model. Rows are keyed by index — a controlled append/remove list on an operator form. Five controls
// never fit one phone-width line, so the row wraps: each input carries a flex basis and the checkbox and remove button
// keep their intrinsic width.
function ParameterBuilder({ values, errors, update }: EditorSectionProps) {
	const { t } = useTranslation();

	const addRow = () =>
		update((current) => ({
			...current,
			parameters: [...current.parameters, { name: "", type: "string", description: "", required: true }],
		}));

	const removeRow = (index: number) =>
		update((current) => ({ ...current, parameters: current.parameters.filter((_, i) => i !== index) }));

	const patchRow = (index: number, patch: Partial<CustomToolParameter>) =>
		update((current) => ({
			...current,
			parameters: current.parameters.map((parameter, i) => (i === index ? { ...parameter, ...patch } : parameter)),
		}));

	return (
		<Stack gap="xs" data-testid="custom-tool-form-parameters">
			<Group justify="space-between" align="center">
				<Text size="sm" fw={500}>
					{t("pages.customTools.form.parameters.label", "Parameters")}
				</Text>
				<Button size="xs" variant="subtle" leftSection={<IconPlus size={14} />} onClick={addRow} data-testid="custom-tool-form-parameter-add">
					{t("pages.customTools.form.parameters.add", "Add parameter")}
				</Button>
			</Group>
			{values.parameters.length === 0 ? (
				<Text size="xs" c="dimmed">
					{t("pages.customTools.form.parameters.empty", "No parameters declared yet.")}
				</Text>
			) : null}
			{values.parameters.map((parameter, index) => (
				// biome-ignore lint/suspicious/noArrayIndexKey: controlled operator list; rows have no stable id.
				<Group key={index} gap="xs" align="flex-start" data-testid={`custom-tool-form-parameter-row-${index}`}>
					<TextInput
						placeholder={t("pages.customTools.form.parameters.namePlaceholder", "city")}
						value={parameter.name}
						error={errorAt(errors, `parameters.${index}.name`) ? t("pages.customTools.form.parameters.nameInvalid", "Identifier only") : undefined}
						onChange={(event) => patchRow(index, { name: event.currentTarget.value })}
						style={{ flex: "2 1 140px" }}
						data-testid={`custom-tool-form-parameter-name-${index}`}
					/>
					<Select
						value={parameter.type}
						data={CUSTOM_TOOL_PARAMETER_TYPES.map((type) => ({ label: type, value: type }))}
						onChange={(value) => patchRow(index, { type: (value ?? "string") as CustomToolParameterType })}
						style={{ flex: "1 1 110px" }}
						allowDeselect={false}
						data-testid={`custom-tool-form-parameter-type-${index}`}
					/>
					<TextInput
						placeholder={t("pages.customTools.form.parameters.descriptionPlaceholder", "description")}
						value={parameter.description}
						onChange={(event) => patchRow(index, { description: event.currentTarget.value })}
						style={{ flex: "3 1 200px" }}
						data-testid={`custom-tool-form-parameter-description-${index}`}
					/>
					<Checkbox
						label={t("pages.customTools.form.parameters.required", "Required")}
						checked={parameter.required}
						onChange={(event) => patchRow(index, { required: event.currentTarget.checked })}
						mt={8}
						style={{ flexShrink: 0 }}
						data-testid={`custom-tool-form-parameter-required-${index}`}
					/>
					<ActionIcon
						variant="subtle"
						color="red"
						aria-label={t("pages.customTools.form.parameters.remove", "Remove parameter")}
						onClick={() => removeRow(index)}
						mt={4}
						style={{ flexShrink: 0 }}
						data-testid={`custom-tool-form-parameter-remove-${index}`}
					>
						<IconTrash size={16} />
					</ActionIcon>
				</Group>
			))}
		</Stack>
	);
}

// HttpFetch editor: method, URL template, headers (name/value/isSecret), body template, allowedHosts.
function HttpEditor({ values, errors, update }: EditorSectionProps) {
	const { t } = useTranslation();
	const http = values.http;

	const patchHttp = (patch: Partial<CustomToolFormValues["http"]>) => update((current) => ({ ...current, http: { ...current.http, ...patch } }));

	const addHeader = () => patchHttp({ headers: [...http.headers, { name: "", value: "", isSecret: false }] });
	const removeHeader = (index: number) => patchHttp({ headers: http.headers.filter((_, i) => i !== index) });
	const patchHeader = (index: number, patch: Partial<CustomToolHeader>) =>
		patchHttp({ headers: http.headers.map((header, i) => (i === index ? { ...header, ...patch } : header)) });

	return (
		<Stack gap="sm" data-testid="custom-tool-form-http">
			<Group grow={true} align="flex-start">
				<Select
					label={t("pages.customTools.form.http.method", "Method")}
					value={http.method}
					data={CUSTOM_TOOL_HTTP_METHODS.map((method) => ({ label: method, value: method }))}
					onChange={(value) => patchHttp({ method: value ?? "GET" })}
					allowDeselect={false}
					maw={140}
					data-testid="custom-tool-form-http-method"
				/>
			</Group>
			<TextInput
				label={t("pages.customTools.form.http.url", "URL template")}
				description={t("pages.customTools.form.http.urlHint", "Use {param} placeholders for query values or path segments only — never the scheme, host, or port.")}
				placeholder="https://api.example.com/weather?city={city}"
				value={http.urlTemplate}
				required={true}
				error={errorAt(errors, "http.urlTemplate") ? t("pages.customTools.form.http.urlRequired", "A URL template is required.") : undefined}
				onChange={(event) => patchHttp({ urlTemplate: event.currentTarget.value })}
				data-testid="custom-tool-form-http-url"
			/>

			<SecretRows
				title={t("pages.customTools.form.http.headers", "Headers")}
				addLabel={t("pages.customTools.form.http.addHeader", "Add header")}
				emptyLabel={t("pages.customTools.form.http.noHeaders", "No headers.")}
				testid="custom-tool-form-http-headers"
				rows={http.headers}
				onAdd={addHeader}
				onRemove={removeHeader}
				onPatch={patchHeader}
			/>

			<Textarea
				label={t("pages.customTools.form.http.body", "Body template")}
				description={t("pages.customTools.form.http.bodyHint", "Optional request body. {param} placeholders are filled the same way.")}
				value={http.bodyTemplate}
				autosize={true}
				minRows={2}
				onChange={(event) => patchHttp({ bodyTemplate: event.currentTarget.value })}
				data-testid="custom-tool-form-http-body"
			/>

			<HostList
				value={http.allowedHosts}
				onChange={(allowedHosts) => patchHttp({ allowedHosts })}
			/>
		</Stack>
	);
}

// Command editor: executable (with a ProgramLaunch probe), args template, working directory, timeout, env.
function CommandEditor({ values, errors, update }: EditorSectionProps) {
	const { t } = useTranslation();
	const command = values.command;

	const patchCommand = (patch: Partial<CustomToolFormValues["command"]>) =>
		update((current) => ({ ...current, command: { ...current.command, ...patch } }));

	const addArg = () => patchCommand({ argsTemplate: [...command.argsTemplate, ""] });
	const removeArg = (index: number) => patchCommand({ argsTemplate: command.argsTemplate.filter((_, i) => i !== index) });
	const patchArg = (index: number, value: string) =>
		patchCommand({ argsTemplate: command.argsTemplate.map((arg, i) => (i === index ? value : arg)) });

	const addEnv = () => patchCommand({ env: [...command.env, { name: "", value: "", isSecret: false }] });
	const removeEnv = (index: number) => patchCommand({ env: command.env.filter((_, i) => i !== index) });
	const patchEnv = (index: number, patch: Partial<CustomToolEnvVar>) =>
		patchCommand({ env: command.env.map((variable, i) => (i === index ? { ...variable, ...patch } : variable)) });

	return (
		<Stack gap="sm" data-testid="custom-tool-form-command">
			<ProgramLaunchSelector
				value={command.executable}
				error={errorAt(errors, "command.executable") ? t("pages.customTools.form.command.executableRequired", "An executable path is required.") : undefined}
				onChange={(executable) => patchCommand({ executable })}
			/>

			<Stack gap="xs">
				<Group justify="space-between" align="center">
					<Text size="sm" fw={500}>
						{t("pages.customTools.form.command.args", "Arguments")}
					</Text>
					<Button size="xs" variant="subtle" leftSection={<IconPlus size={14} />} onClick={addArg} data-testid="custom-tool-form-command-arg-add">
						{t("pages.customTools.form.command.addArg", "Add argument")}
					</Button>
				</Group>
				<Text size="xs" c="dimmed">
					{t("pages.customTools.form.command.argsHint", "One argument per row. A {param} placeholder fills a single argument — a value can never inject extra arguments.")}
				</Text>
				{command.argsTemplate.length === 0 ? (
					<Text size="xs" c="dimmed">
						{t("pages.customTools.form.command.noArgs", "No arguments.")}
					</Text>
				) : null}
				{command.argsTemplate.map((arg, index) => (
					// biome-ignore lint/suspicious/noArrayIndexKey: controlled operator list; args have no stable id.
					<Group key={index} gap="xs" align="center" wrap="nowrap">
						<TextInput
							placeholder="--city={city}"
							value={arg}
							onChange={(event) => patchArg(index, event.currentTarget.value)}
							style={{ flex: 1 }}
							data-testid={`custom-tool-form-command-arg-${index}`}
						/>
						<ActionIcon
							variant="subtle"
							color="red"
							aria-label={t("pages.customTools.form.command.removeArg", "Remove argument")}
							onClick={() => removeArg(index)}
							data-testid={`custom-tool-form-command-arg-remove-${index}`}
						>
							<IconTrash size={16} />
						</ActionIcon>
					</Group>
				))}
			</Stack>

			<Group grow={true} align="flex-start">
				<TextInput
					label={t("pages.customTools.form.command.workingDirectory", "Working directory")}
					placeholder="/opt/tool"
					value={command.workingDirectory}
					onChange={(event) => patchCommand({ workingDirectory: event.currentTarget.value })}
					data-testid="custom-tool-form-command-cwd"
				/>
				<NumberInput
					label={t("pages.customTools.form.command.timeout", "Timeout (seconds)")}
					description={t("pages.customTools.form.command.timeoutHint", "0 uses the default.")}
					value={command.timeoutSeconds}
					min={0}
					max={CUSTOM_TOOL_TIMEOUT_MAX}
					onChange={(value) => patchCommand({ timeoutSeconds: typeof value === "number" ? value : 0 })}
					data-testid="custom-tool-form-command-timeout"
				/>
			</Group>

			<SecretRows
				title={t("pages.customTools.form.command.env", "Environment variables")}
				addLabel={t("pages.customTools.form.command.addEnv", "Add variable")}
				emptyLabel={t("pages.customTools.form.command.noEnv", "No environment variables.")}
				testid="custom-tool-form-command-env"
				rows={command.env}
				onAdd={addEnv}
				onRemove={removeEnv}
				onPatch={patchEnv}
			/>
		</Stack>
	);
}

interface SecretRow {
	readonly name: string;
	readonly value: string;
	readonly isSecret: boolean;
}

interface SecretRowsProps {
	title: string;
	addLabel: string;
	emptyLabel: string;
	testid: string;
	rows: readonly SecretRow[];
	onAdd: () => void;
	onRemove: (index: number) => void;
	onPatch: (index: number, patch: Partial<SecretRow>) => void;
}

// Shared name/value/isSecret row editor for HTTP headers and command env. A stored secret comes back as the sentinel;
// the row shows a "stored" hint and leaves it in place so an unedited save keeps the secret. Editing the value replaces
// it. Marking a fresh row secret only affects how it is stored — the value input stays plain (operator on own node).
// Like the parameter rows, the row wraps at narrow widths instead of squeezing the inputs past their content.
function SecretRows({ title, addLabel, emptyLabel, testid, rows, onAdd, onRemove, onPatch }: SecretRowsProps) {
	const { t } = useTranslation();

	return (
		<Stack gap="xs" data-testid={testid}>
			<Group justify="space-between" align="center">
				<Text size="sm" fw={500}>
					{title}
				</Text>
				<Button size="xs" variant="subtle" leftSection={<IconPlus size={14} />} onClick={onAdd} data-testid={`${testid}-add`}>
					{addLabel}
				</Button>
			</Group>
			{rows.length === 0 ? (
				<Text size="xs" c="dimmed">
					{emptyLabel}
				</Text>
			) : null}
			{rows.map((row, index) => {
				const isStoredSecret = row.isSecret && row.value === CUSTOM_TOOL_SECRET_SENTINEL;
				return (
					// biome-ignore lint/suspicious/noArrayIndexKey: controlled operator list; rows have no stable id.
					<Group key={index} gap="xs" align="flex-start" data-testid={`${testid}-row-${index}`}>
						<TextInput
							placeholder={t("pages.customTools.form.secretRows.namePlaceholder", "Name")}
							value={row.name}
							onChange={(event) => onPatch(index, { name: event.currentTarget.value })}
							style={{ flex: "2 1 140px" }}
							data-testid={`${testid}-name-${index}`}
						/>
						<TextInput
							placeholder={
								isStoredSecret
									? t("pages.customTools.form.secretRows.storedPlaceholder", "•••• stored — leave to keep")
									: t("pages.customTools.form.secretRows.valuePlaceholder", "Value")
							}
							value={isStoredSecret ? "" : row.value}
							onChange={(event) => onPatch(index, { value: event.currentTarget.value })}
							style={{ flex: "3 1 200px" }}
							data-testid={`${testid}-value-${index}`}
						/>
						<Checkbox
							label={t("pages.customTools.form.secretRows.secret", "Secret")}
							checked={row.isSecret}
							onChange={(event) => {
								const checked = event.currentTarget.checked;
								// Clearing the sentinel when un-marking a stored secret avoids persisting the literal sentinel as a value.
								const nextValue = !checked && row.value === CUSTOM_TOOL_SECRET_SENTINEL ? "" : row.value;
								onPatch(index, { isSecret: checked, value: nextValue });
							}}
							mt={8}
							style={{ flexShrink: 0 }}
							data-testid={`${testid}-secret-${index}`}
						/>
						<ActionIcon
							variant="subtle"
							color="red"
							aria-label={t("pages.customTools.form.secretRows.remove", "Remove")}
							onClick={() => onRemove(index)}
							mt={4}
							style={{ flexShrink: 0 }}
							data-testid={`${testid}-remove-${index}`}
						>
							<IconTrash size={16} />
						</ActionIcon>
					</Group>
				);
			})}
		</Stack>
	);
}

// allowedHosts editor: one host per row. Required when the URL host itself is templated; the guard runs server-side.
function HostList({ value, onChange }: { value: readonly string[]; onChange: (next: string[]) => void }) {
	const { t } = useTranslation();

	return (
		<Stack gap="xs" data-testid="custom-tool-form-http-hosts">
			<Group justify="space-between" align="center">
				<Text size="sm" fw={500}>
					{t("pages.customTools.form.http.allowedHosts", "Allowed hosts")}
				</Text>
				<Button
					size="xs"
					variant="subtle"
					leftSection={<IconPlus size={14} />}
					onClick={() => onChange([...value, ""])}
					data-testid="custom-tool-form-http-host-add"
				>
					{t("pages.customTools.form.http.addHost", "Add host")}
				</Button>
			</Group>
			<Text size="xs" c="dimmed">
				{t("pages.customTools.form.http.allowedHostsHint", "Required when the URL host is itself templated: the request may only reach a host on this list.")}
			</Text>
			{value.map((host, index) => (
				// biome-ignore lint/suspicious/noArrayIndexKey: controlled operator list; hosts have no stable id.
				<Group key={index} gap="xs" align="center" wrap="nowrap">
					<TextInput
						placeholder="api.example.com"
						value={host}
						onChange={(event) => onChange(value.map((existing, i) => (i === index ? event.currentTarget.value : existing)))}
						style={{ flex: 1 }}
						data-testid={`custom-tool-form-http-host-${index}`}
					/>
					<ActionIcon
						variant="subtle"
						color="red"
						aria-label={t("pages.customTools.form.http.removeHost", "Remove host")}
						onClick={() => onChange(value.filter((_, i) => i !== index))}
						data-testid={`custom-tool-form-http-host-remove-${index}`}
					>
						<IconTrash size={16} />
					</ActionIcon>
				</Group>
			))}
		</Stack>
	);
}

// ProgramLaunch selector: the operator enters (or pastes) an absolute executable path and probes it. The probe is a
// desktop-only endpoint that resolves the path and rejects shells/interpreters/symlinks; its ok/reason is shown so the
// operator sees the verdict before committing. On a non-desktop node the probe errors and the reason surfaces. The
// path input and its Validate button wrap at narrow widths — an input cannot shrink below its intrinsic width, so
// forcing one line pushed the button off-screen.
function ProgramLaunchSelector({ value, error, onChange }: { value: string; error?: string; onChange: (next: string) => void }) {
	const { t } = useTranslation();
	const probe = useValidateExecutable();
	const result = probe.data;

	return (
		<Stack gap={4} data-testid="custom-tool-form-program-launch">
			<Group gap="xs" align="flex-end" data-testid="custom-tool-form-program-launch-row">
				<TextInput
					label={t("pages.customTools.form.command.executable", "Executable")}
					description={t("pages.customTools.form.command.executableHint", "Absolute path to a regular program. Shells and interpreters (sh, bash, python, node…) are rejected.")}
					placeholder="/usr/bin/curl"
					value={value}
					required={true}
					error={error}
					onChange={(event) => onChange(event.currentTarget.value)}
					style={{ flex: "1 1 220px" }}
					data-testid="custom-tool-form-command-executable"
				/>
				<Button
					variant="default"
					onClick={() => probe.mutate({ body: { path: value } })}
					loading={probe.isPending}
					disabled={value.trim().length === 0}
					style={{ flexShrink: 0 }}
					data-testid="custom-tool-form-program-launch-validate"
				>
					{t("pages.customTools.form.command.validate", "Validate")}
				</Button>
			</Group>
			{probe.error ? (
				<Group gap={6} c="red">
					<IconX size={14} />
					<Text size="xs">{apiErrorMessage(probe.error, t("pages.customTools.form.command.validateFailed", "Could not validate the path."))}</Text>
				</Group>
			) : result ? (
				<Group gap={6} c={result.ok ? "teal" : "red"} data-testid="custom-tool-form-program-launch-result">
					{result.ok ? <IconCheck size={14} /> : <IconAlertTriangle size={14} />}
					<Text size="xs">
						{result.ok ? t("pages.customTools.form.command.validateOk", "Looks good.") : (result.reason ?? t("pages.customTools.form.command.validateRejected", "Rejected."))}
					</Text>
					{result.path ? <Code>{result.path}</Code> : null}
				</Group>
			) : (
				<Group gap={6} c="dimmed">
					<IconInfoCircle size={14} />
					<Text size="xs">{t("pages.customTools.form.command.validateIdle", "Validate the path to confirm it resolves on this host.")}</Text>
				</Group>
			)}
		</Stack>
	);
}
