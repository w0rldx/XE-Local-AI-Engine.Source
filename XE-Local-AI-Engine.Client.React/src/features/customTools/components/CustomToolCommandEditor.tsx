import { ActionIcon, Button, Group, NumberInput, Stack, Text, TextInput } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { CustomToolEditorSectionProps } from "@/features/customTools/components/CustomToolEditorShared";
import { useEditableRowKeys } from "@/features/customTools/components/CustomToolEditorShared";
import { CustomToolProgramLaunchSelector } from "@/features/customTools/components/CustomToolProgramLaunchSelector";
import { CustomToolSecretRows } from "@/features/customTools/components/CustomToolSecretRows";
import { errorAt } from "@/features/customTools/models/CustomToolFormErrors";
import {
	CUSTOM_TOOL_TIMEOUT_MAX,
	type CustomToolEnvVar,
	type CustomToolFormValues,
} from "@/features/customTools/models/CustomToolModels";

// Command editor: executable (with a ProgramLaunch probe), args template, working directory, timeout, env.
export function CommandEditor({ values, errors, update }: CustomToolEditorSectionProps) {
	const { t } = useTranslation();
	const command = values.command;
	const {
		rowKeys: argRowKeys,
		appendRowKey: appendArgRowKey,
		removeRowKey: removeArgRowKey,
	} = useEditableRowKeys(command.argsTemplate.length);

	const patchCommand = (patch: Partial<CustomToolFormValues["command"]>) =>
		update((current) => ({ ...current, command: { ...current.command, ...patch } }));

	const addArg = () => {
		appendArgRowKey();
		patchCommand({ argsTemplate: [...command.argsTemplate, ""] });
	};
	const removeArg = (index: number) => {
		removeArgRowKey(index);
		patchCommand({ argsTemplate: command.argsTemplate.filter((_, i) => i !== index) });
	};
	const patchArg = (index: number, value: string) =>
		patchCommand({ argsTemplate: command.argsTemplate.map((arg, i) => (i === index ? value : arg)) });

	const addEnv = () => patchCommand({ env: [...command.env, { name: "", value: "", isSecret: false }] });
	const removeEnv = (index: number) => patchCommand({ env: command.env.filter((_, i) => i !== index) });
	const patchEnv = (index: number, patch: Partial<CustomToolEnvVar>) =>
		patchCommand({ env: command.env.map((variable, i) => (i === index ? { ...variable, ...patch } : variable)) });

	return (
		<Stack gap="sm" data-testid="custom-tool-form-command">
			<CustomToolProgramLaunchSelector
				value={command.executable}
				error={
					errorAt(errors, "command.executable")
						? t("pages.customTools.form.command.executableRequired", "An executable path is required.")
						: undefined
				}
				onChange={(executable) => patchCommand({ executable })}
			/>

			<Stack gap="xs">
				<Group justify="space-between" align="center">
					<Text size="sm" fw={500}>
						{t("pages.customTools.form.command.args", "Arguments")}
					</Text>
					<Button
						size="xs"
						variant="subtle"
						leftSection={<IconPlus size={14} />}
						onClick={addArg}
						data-testid="custom-tool-form-command-arg-add"
					>
						{t("pages.customTools.form.command.addArg", "Add argument")}
					</Button>
				</Group>
				<Text size="xs" c="dimmed">
					{t(
						"pages.customTools.form.command.argsHint",
						"One argument per row. A {param} placeholder fills a single argument — a value can never inject extra arguments.",
					)}
				</Text>
				{command.argsTemplate.length === 0 ? (
					<Text size="xs" c="dimmed">
						{t("pages.customTools.form.command.noArgs", "No arguments.")}
					</Text>
				) : null}
				{command.argsTemplate.map((arg, index) => (
					<Group key={argRowKeys[index]} gap="xs" align="center" wrap="nowrap">
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

			<CustomToolSecretRows
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
