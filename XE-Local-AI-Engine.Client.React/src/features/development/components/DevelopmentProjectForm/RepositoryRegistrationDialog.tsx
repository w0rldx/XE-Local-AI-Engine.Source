import { Button, Stack, Text, TextInput } from "@mantine/core";
import { IconFolderPlus, IconX } from "@tabler/icons-react";
import type { TFunction } from "i18next";
import type { Dispatch, SetStateAction } from "react";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import type { RegisterDevelopmentRepositoryValues } from "@/features/development/models/DevelopmentProjectFormModels";

export interface RepositoryRegistrationSectionProps {
	readonly registrationOpened: boolean;
	readonly setRegistrationOpened: Dispatch<SetStateAction<boolean>>;
	readonly registration: RegisterDevelopmentRepositoryValues;
	readonly setRegistration: Dispatch<SetStateAction<RegisterDevelopmentRepositoryValues>>;
	readonly registrationAttemptError?: string;
	readonly register: () => Promise<void>;
	readonly isRegistering: boolean;
}

export function RepositoryRegistrationDialog({
	t,
	repositoryRegistration: props,
}: {
	readonly t: TFunction;
	readonly repositoryRegistration: RepositoryRegistrationSectionProps;
}) {
	const {
		registrationOpened,
		setRegistrationOpened,
		registration,
		setRegistration,
		registrationAttemptError,
		register,
		isRegistering,
	} = props;
	return (
		<DialogShell
			opened={registrationOpened}
			onClose={() => setRegistrationOpened(false)}
			title={t("pages.development.register.title", "Register local Git repository")}
			confirmCloseWhen={registration.alias.length > 0 || registration.hostPath.length > 0}
			footer={
				<>
					<Button variant="subtle" leftSection={<IconX size={16} />} onClick={() => setRegistrationOpened(false)}>
						{t("common.cancel", "Cancel")}
					</Button>
					<Button
						leftSection={<IconFolderPlus size={16} />}
						onClick={register}
						loading={isRegistering}
						disabled={!registration.alias.trim() || !registration.hostPath.trim()}
						data-testid="development-register-repository"
					>
						{t("pages.development.register.submit", "Register repository")}
					</Button>
				</>
			}
		>
			<Stack gap="md" px="md" pb="md">
				<Text size="sm" c="dimmed">
					{t(
						"pages.development.register.description",
						"Enter the absolute path once. The server stores it encrypted and returns only this alias and an opaque identifier.",
					)}
				</Text>
				<TextInput
					label={t("pages.development.register.alias", "Alias")}
					value={registration.alias}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setRegistration((current) => ({ ...current, alias: value }));
					}}
					required={true}
					data-testid="development-register-alias"
				/>
				<TextInput
					label={t("pages.development.register.path", "Absolute repository path")}
					value={registration.hostPath}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setRegistration((current) => ({ ...current, hostPath: value }));
					}}
					required={true}
					data-testid="development-register-path"
				/>
				{registrationAttemptError ? <InlineErrorAlert message={registrationAttemptError} /> : null}
			</Stack>
		</DialogShell>
	);
}
