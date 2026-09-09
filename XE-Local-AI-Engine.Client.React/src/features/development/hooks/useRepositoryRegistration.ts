import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import type { DevelopmentRepository } from "@/features/development/models/DevelopmentModels";
import type { RegisterDevelopmentRepositoryValues } from "@/features/development/models/DevelopmentProjectFormModels";

interface UseRepositoryRegistrationOptions {
	readonly onRegister: (values: RegisterDevelopmentRepositoryValues) => Promise<DevelopmentRepository>;
	readonly onRepositoryChange?: (selectedFolderId: string) => void;
	readonly selectRepository: (repositoryId: string) => void;
}

/** The "register local Git repository" dialog: its draft, its attempt error, and the register-then-select action. */
export function useRepositoryRegistration({
	onRegister,
	onRepositoryChange,
	selectRepository,
}: UseRepositoryRegistrationOptions) {
	const { t } = useTranslation();
	const [registrationOpened, setRegistrationOpened] = useState(false);
	const [registration, setRegistration] = useState<RegisterDevelopmentRepositoryValues>({ alias: "", hostPath: "" });
	const [registrationAttemptError, setRegistrationAttemptError] = useState<string>();

	const register = async (): Promise<void> => {
		setRegistrationAttemptError(undefined);
		try {
			const created = await onRegister(registration);
			selectRepository(created.id);

			// Registering auto-selects the new repository, so the owner has to be told as well — this is the same
			// notification the Select's onChange fires. Without it the page's profileFolderId stays null on the
			// register-then-create path, detection never runs, and the command-profile confirmation step never
			// appears at all. That is the FIRST-RUN path, so the operator would simply never be shown the profile.
			onRepositoryChange?.(created.id);
			setRegistration({ alias: "", hostPath: "" });
			setRegistrationOpened(false);
		} catch (registrationFailure) {
			setRegistrationAttemptError(
				apiErrorMessage(
					registrationFailure,
					t("pages.development.register.error", "Could not register the local Git repository."),
				),
			);
		}
	};

	return {
		registrationOpened,
		setRegistrationOpened,
		registration,
		setRegistration,
		registrationAttemptError,
		setRegistrationAttemptError,
		register,
	};
}
