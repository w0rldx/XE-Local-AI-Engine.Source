import { Button, Code, Group, Stack, Text, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconInfoCircle, IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { useValidateExecutable } from "@/features/customTools/queries/useCustomTools";

// ProgramLaunch selector: the operator enters (or pastes) an absolute executable path and probes it. The probe is a
// desktop-only endpoint that resolves the path and rejects shells/interpreters/symlinks; its ok/reason is shown so the
// operator sees the verdict before committing. On a non-desktop node the probe errors and the reason surfaces.
export function CustomToolProgramLaunchSelector({
	value,
	error,
	onChange,
}: {
	value: string;
	error?: string;
	onChange: (next: string) => void;
}) {
	const { t } = useTranslation();
	const probe = useValidateExecutable();
	const result = probe.data;

	return (
		<Stack gap={4} data-testid="custom-tool-form-program-launch">
			<Group gap="xs" align="flex-end" data-testid="custom-tool-form-program-launch-row">
				<TextInput
					label={t("pages.customTools.form.command.executable", "Executable")}
					description={t(
						"pages.customTools.form.command.executableHint",
						"Absolute path to a regular program. Shells and interpreters (sh, bash, python, node…) are rejected.",
					)}
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
					<Text size="xs">
						{apiErrorMessage(probe.error, t("pages.customTools.form.command.validateFailed", "Could not validate the path."))}
					</Text>
				</Group>
			) : result ? (
				<Group gap={6} c={result.ok ? "teal" : "red"} data-testid="custom-tool-form-program-launch-result">
					{result.ok ? <IconCheck size={14} /> : <IconAlertTriangle size={14} />}
					<Text size="xs">
						{result.ok
							? t("pages.customTools.form.command.validateOk", "Looks good.")
							: (result.reason ?? t("pages.customTools.form.command.validateRejected", "Rejected."))}
					</Text>
					{result.path ? <Code>{result.path}</Code> : null}
				</Group>
			) : (
				<Group gap={6} c="dimmed">
					<IconInfoCircle size={14} />
					<Text size="xs">
						{t("pages.customTools.form.command.validateIdle", "Validate the path to confirm it resolves on this host.")}
					</Text>
				</Group>
			)}
		</Stack>
	);
}
