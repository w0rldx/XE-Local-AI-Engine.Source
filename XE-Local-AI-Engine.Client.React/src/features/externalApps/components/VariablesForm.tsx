import { Anchor, Checkbox, Collapse, NumberInput, Select, Stack, Text, TextInput } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { StoredSecretInput } from "@/core/ui/components/StoredSecretInput/StoredSecretInput";
import { EXTERNAL_APP_SECRET_SENTINEL } from "@/features/externalApps/models/ExternalAppModels";
import {
	type ExternalAppVariableDefinition,
	type ExternalAppVariableIssue,
	type ExternalAppVariableValues,
	toExternalAppVariableType,
} from "@/features/externalApps/models/ExternalAppVariables";

interface VariablesFormProps {
	readonly definitions: readonly ExternalAppVariableDefinition[];
	readonly values: ExternalAppVariableValues;
	readonly issues: readonly ExternalAppVariableIssue[];
	/**
	 * Names the node holds a stored secret for, from the masked values it answered with — NOT from `values`, which
	 * carries "" once a secret is cleared. Install has none. Without it a secret box forgets a pending clear whenever
	 * the Advanced section is collapsed and reopened, since that unmounts the box.
	 */
	readonly storedSecrets: readonly string[];
	/** Names the TARGET manifest requires and the installed one never declared. The update dialog is the only caller. */
	readonly newlyRequired?: readonly string[];
	readonly disabled?: boolean;
	readonly onChange: (name: string, value: string) => void;
	readonly "data-testid"?: string;
}

const keyPrefix = "pages.externalApps.variables";

/**
 * The type-switched variable renderer. Manual Mantine controls, no form library: the parent owns the values, so the
 * same component serves the install dialog, the update dialog and the instance's Settings tab.
 *
 * The secret mechanic lives in `StoredSecretInput`, shared with `CustomToolSecretRows`: a stored secret arrives as the
 * sentinel, its box renders EMPTY with a "stored — leave empty to keep" placeholder, an untouched save sends the
 * sentinel straight back, and clearing it is an explicit action rather than an emptied box. It is rendered `masked`
 * so the value the operator TYPES is hidden — an install dialog asking for an admin password must not print it on
 * screen, and the live round screenshots these dialogs. The reveal control that comes with it is harmless on a stored
 * secret: the box is empty, so revealing it shows the empty box, never a value the server did not return.
 */
export function VariablesForm({
	definitions,
	values,
	issues,
	storedSecrets,
	newlyRequired,
	disabled = false,
	onChange,
	"data-testid": testId,
}: VariablesFormProps) {
	const { t } = useTranslation();
	const [advancedOpen, setAdvancedOpen] = useState(false);

	const named = definitions.filter((definition) => (definition.name ?? "").length > 0);
	const basic = named.filter((definition) => definition.advanced !== true);
	const advanced = named.filter((definition) => definition.advanced === true);
	const issueFor = (name: string): ExternalAppVariableIssue | undefined => issues.find((issue) => issue.name === name);

	if (named.length === 0) {
		return (
			<Text size="sm" c="dimmed" data-testid={testId ?? "external-app-variables"}>
				{t(`${keyPrefix}.empty`)}
			</Text>
		);
	}

	const field = (definition: ExternalAppVariableDefinition) => {
		const name = definition.name ?? "";
		return (
			<VariableField
				key={name}
				definition={definition}
				value={values[name] ?? ""}
				stored={storedSecrets.includes(name)}
				issue={issueFor(name)}
				isNewlyRequired={(newlyRequired ?? []).includes(name)}
				disabled={disabled}
				onChange={onChange}
			/>
		);
	};

	return (
		<Stack gap="sm" data-testid={testId ?? "external-app-variables"}>
			{basic.map(field)}
			{advanced.length > 0 ? (
				<>
					<Anchor
						component="button"
						type="button"
						size="sm"
						onClick={() => setAdvancedOpen((open) => !open)}
						data-testid="external-app-variables-advanced-toggle"
					>
						{t(`${keyPrefix}.advanced`)}
					</Anchor>
					{/* `keepMounted={false}` so a closed section holds no focusable inputs and no half-rendered
					    controls — Mantine's default keeps them in the DOM behind an Activity boundary. */}
					<Collapse expanded={advancedOpen} keepMounted={false} data-testid="external-app-variables-advanced">
						<Stack gap="sm">{advanced.map(field)}</Stack>
					</Collapse>
				</>
			) : null}
		</Stack>
	);
}

interface VariableFieldProps {
	readonly definition: ExternalAppVariableDefinition;
	readonly value: string;
	readonly stored: boolean;
	readonly issue: ExternalAppVariableIssue | undefined;
	readonly isNewlyRequired: boolean;
	readonly disabled: boolean;
	readonly onChange: (name: string, value: string) => void;
}

function VariableField({ definition, value, stored, issue, isNewlyRequired, disabled, onChange }: VariableFieldProps) {
	const { t } = useTranslation();
	const name = definition.name ?? "";
	const type = toExternalAppVariableType(definition.type);
	const required = definition.required === true;
	// The issue's `messageKey` is already fully qualified, so it is translated with no prefix of this component's own.
	const error = issue ? t(issue.messageKey, issue.params) : undefined;
	const shared = {
		label: definition.label ?? name,
		description: isNewlyRequired ? (
			<>
				{definition.description ? `${definition.description} ` : null}
				<Text component="span" size="xs" fw={600} data-testid={`external-app-variable-new-${name}`}>
					{t("pages.externalApps.update.newlyRequired")}
				</Text>
			</>
		) : (
			(definition.description ?? undefined)
		),
		withAsterisk: required,
		error,
		disabled,
		"data-testid": `external-app-variable-${name}`,
	};

	if (type === "boolean") {
		return (
			<Checkbox
				{...shared}
				checked={value === "true"}
				onChange={(event) => onChange(name, event.currentTarget.checked ? "true" : "false")}
			/>
		);
	}

	if (type === "enum") {
		return (
			<Select
				{...shared}
				data={[...(definition.allowedValues ?? [])]}
				value={value.length > 0 ? value : null}
				clearable={!required}
				onChange={(next) => onChange(name, next ?? "")}
			/>
		);
	}

	if (type === "integer") {
		// Reported as a string like every other control: the server owns typing, and a number here would make the
		// values map two-shaped for no gain.
		return (
			<NumberInput
				{...shared}
				allowDecimal={false}
				value={value}
				onChange={(next) => onChange(name, next === "" || next === null ? "" : String(next))}
			/>
		);
	}

	if (type === "secret") {
		return (
			<StoredSecretInput
				{...shared}
				masked={true}
				stored={stored}
				sentinel={EXTERNAL_APP_SECRET_SENTINEL}
				value={value}
				storedPlaceholder={t(`${keyPrefix}.storedSecret`)}
				onChange={(next) => onChange(name, next)}
			/>
		);
	}

	return <TextInput {...shared} value={value} onChange={(event) => onChange(name, event.currentTarget.value)} />;
}
