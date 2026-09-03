import { Alert, Button, MultiSelect, Select, Stack, Switch, TextInput } from "@mantine/core";
import { IconDeviceFloppy, IconX } from "@tabler/icons-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { shortPrincipalId } from "@/features/integrations/components/IntegrationFormatters";
import {
	emptyIntegrationKeyFormValues,
	type IntegrationApiKey,
	type IntegrationKeyFormValues,
	integrationKeyFormSchema,
	type IntegrationTrigger,
} from "@/features/integrations/models/IntegrationModels";

interface IntegrationKeyGenerateDialogProps {
	opened: boolean;
	keys: readonly IntegrationApiKey[];
	triggers: readonly IntegrationTrigger[];
	isSubmitting: boolean;
	submitError?: string;
	onSubmit: (values: IntegrationKeyFormValues) => void;
	onClose: () => void;
}

// Generate dialog for an integration API key.
//
// Two controls decide an authorization scope, and both are written the safe way round. "Allow all triggers" is an
// explicit switch defaulting to OFF, and an empty multiselect is a validation error rather than the "all triggers"
// wildcard — mapping an untouched selection to null would turn "I picked nothing yet" into "this key may invoke
// every trigger on this node, including ones created later". The identity select defaults to "New identity", so the
// non-linking choice needs no thought and reusing an integrator identity is deliberate.
export function IntegrationKeyGenerateDialog({
	opened,
	keys,
	triggers,
	isSubmitting,
	submitError,
	onSubmit,
	onClose,
}: IntegrationKeyGenerateDialogProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState<IntegrationKeyFormValues>(emptyIntegrationKeyFormValues);
	const [errors, setErrors] = useState<Record<string, string>>({});

	// This component is mounted unconditionally by the page (it is the wrapper, not the Modal child), so Mantine's
	// unmount-on-close never reaches its state, and the success path closes the dialog without going through
	// handleClose. Reset on every OPEN instead: the wide "Allow all triggers" grant has to be a deliberate switch
	// each time, never a leftover from the previous key.
	useEffect(() => {
		if (opened) {
			setValues(emptyIntegrationKeyFormValues);
			setErrors({});
		}
	}, [opened]);

	// One option per DISTINCT principal in the key list, including revoked keys' principals: rotating a credential
	// after a revocation is exactly the case this control exists for.
	const principalData = useMemo(() => {
		const labelsByPrincipal = new Map<string, string[]>();
		for (const key of keys) {
			const labels = labelsByPrincipal.get(key.principalId);
			if (labels === undefined) {
				labelsByPrincipal.set(key.principalId, [key.label]);
			} else {
				labels.push(key.label);
			}
		}

		return [
			{ value: "", label: t("pages.integrations.keys.generate.principal.newIdentity", "New identity") },
			...[...labelsByPrincipal.entries()].map(([principalId, labels]) => ({
				value: principalId,
				label: `${shortPrincipalId(principalId)} — ${labels.join(", ")}`,
			})),
		];
	}, [keys, t]);

	const triggerData = useMemo(
		() => triggers.map((trigger) => ({ value: trigger.id, label: trigger.displayName })),
		[triggers],
	);

	const fieldError = useCallback(
		(key: string, fallback: string): string | undefined => {
			const message = errors[key];
			return message === undefined ? undefined : t(`pages.integrations.keys.generate.validation.${message}`, fallback);
		},
		[errors, t],
	);

	const handleClose = useCallback(() => {
		setValues(emptyIntegrationKeyFormValues);
		setErrors({});
		onClose();
	}, [onClose]);

	const handleSubmit = useCallback(() => {
		const result = integrationKeyFormSchema.safeParse(values);
		if (!result.success) {
			const nextErrors: Record<string, string> = {};
			for (const issue of result.error.issues) {
				nextErrors[issue.path.map((segment) => String(segment)).join(".")] = issue.message;
			}
			setErrors(nextErrors);
			return;
		}

		setErrors({});
		onSubmit(values);
	}, [onSubmit, values]);

	return (
		<DialogShell
			title={t("pages.integrations.keys.generate.title", "Generate API key")}
			opened={opened}
			onClose={handleClose}
			zIndex={300}
			data-testid="integration-key-generate-dialog"
			footer={
				<>
					<Button
						variant="subtle"
						leftSection={<IconX size={16} />}
						onClick={handleClose}
						disabled={isSubmitting}
						data-testid="integration-key-generate-cancel"
					>
						{t("common.cancel", "Cancel")}
					</Button>
					<Button
						leftSection={<IconDeviceFloppy size={16} />}
						onClick={handleSubmit}
						loading={isSubmitting}
						data-testid="integration-key-generate-submit"
					>
						{t("pages.integrations.keys.generate.submit", "Generate")}
					</Button>
				</>
			}
		>
			<Stack gap="md">
				<TextInput
					label={t("pages.integrations.keys.generate.label", "Label")}
					placeholder={t("pages.integrations.keys.generate.labelPlaceholder", "sensor-hub")}
					value={values.label}
					required={true}
					error={fieldError("label", "A label is required.")}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setValues((current) => ({ ...current, label: value }));
					}}
					data-testid="integration-key-generate-label"
				/>

				<Select
					label={t("pages.integrations.keys.generate.principal.label", "Integrator identity")}
					description={t(
						"pages.integrations.keys.generate.principal.description",
						"Keys sharing an identity own the same sessions and executions. Reuse an identity to rotate or add a credential for an existing integrator.",
					)}
					data={principalData}
					value={values.principalId}
					allowDeselect={false}
					onChange={(value) => {
						if (value === null) {
							return;
						}
						setValues((current) => ({ ...current, principalId: value }));
					}}
					data-testid="integration-key-generate-principal"
				/>

				<Switch
					label={t("pages.integrations.keys.generate.allTriggers.label", "Allow all triggers")}
					description={t(
						"pages.integrations.keys.generate.allTriggers.description",
						"This key may invoke every trigger on this node, including triggers created later.",
					)}
					checked={values.allowAllTriggers}
					onChange={(event) => {
						const checked = event.currentTarget.checked;
						setValues((current) => ({ ...current, allowAllTriggers: checked }));
					}}
					data-testid="integration-key-generate-all-triggers"
				/>

				{values.allowAllTriggers ? null : (
					<MultiSelect
						label={t("pages.integrations.keys.generate.triggersLabel", "Allowed triggers")}
						description={t(
							"pages.integrations.keys.generate.triggersDescription",
							"The triggers this key may invoke. Pick at least one.",
						)}
						data={triggerData}
						value={[...values.allowedTriggerIds]}
						error={fieldError("allowedTriggerIds", "Select at least one trigger, or turn on Allow all triggers.")}
						onChange={(value) => setValues((current) => ({ ...current, allowedTriggerIds: value }))}
						data-testid="integration-key-generate-triggers"
					/>
				)}

				{submitError ? (
					<Alert color="red" data-testid="integration-key-generate-error">
						{submitError}
					</Alert>
				) : null}
			</Stack>
		</DialogShell>
	);
}
