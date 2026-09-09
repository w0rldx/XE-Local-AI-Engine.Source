import { type FormEvent, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import {
	type DevelopmentProfileDetection,
	type DevelopmentRepository,
	developmentProfileIdForBuildTarget,
	isDevelopmentWhitespaceOnlyProfile,
} from "@/features/development/models/DevelopmentModels";
import type { DevelopmentProjectFormValues } from "@/features/development/models/DevelopmentProjectFormModels";

const initialValues: DevelopmentProjectFormValues = {
	selectedFolderId: "",
	objective: "",
	baseBranch: "main",
	taskTitle: "",
	requirements: "",
	acceptanceCriteriaJson: "[]",
	egressPolicy: "LocalOnly",
	coderModelId: "",
	reviewerModelId: "",
	trustedRepositoryAcknowledged: false,
};

interface UseProjectDraftOptions {
	readonly repositories: readonly DevelopmentRepository[];
	readonly repositoriesLoading: boolean;
	readonly detection?: DevelopmentProfileDetection | null;
	readonly onSubmit: (values: DevelopmentProjectFormValues) => void;
}

/**
 * The project draft itself: the editable values, the repository options, and the command-profile confirmation gate.
 * `selectRepository` is the seam the registration and template hooks use to auto-select what they just created.
 */
export function useProjectDraft({ repositories, repositoriesLoading, detection, onSubmit }: UseProjectDraftOptions) {
	const { t } = useTranslation();
	const [values, setValues] = useState(initialValues);
	const [profileConfirmed, setProfileConfirmed] = useState(false);
	const [confirmedDetectionIdentity, setConfirmedDetectionIdentity] = useState<string | null>(null);

	const repositoryOptions = useMemo(
		() =>
			repositories.map((repository) => ({
				value: repository.id,
				label:
					repository.availability === "Available"
						? repository.alias
						: `${repository.alias} (${t("pages.development.repositoryUnavailable", "unavailable")})`,
				disabled: repository.availability !== "Available",
			})),
		[repositories, t],
	);

	const selectedRepository = repositories.find((repository) => repository.id === values.selectedFolderId);

	const detectedProfileId = detection?.profileId ?? null;
	const candidates = useMemo(() => detection?.candidates ?? [], [detection]);
	// The chosen target defaults to what detection proposed and moves the profile with it, because the backend pairs
	// the two strictly.
	const chosenBuildTarget = values.buildTarget ?? detection?.buildTarget ?? null;
	const chosenProfileId = detection
		? chosenBuildTarget
			? developmentProfileIdForBuildTarget(chosenBuildTarget)
			: detectedProfileId
		: null;
	const whitespaceOnly = Boolean(detection) && isDevelopmentWhitespaceOnlyProfile(chosenProfileId);

	// A new detection is a new proposal, so neither a previous confirmation nor a previous override can carry over to
	// it. Adjusted during render rather than in an effect: an effect would let a stale confirmation be visible — and
	// therefore submittable — for one paint after the proposal changed.
	const detectionIdentity = detection ? `${detection.profileId ?? ""}:${detection.buildTarget ?? ""}` : null;
	if (detectionIdentity !== confirmedDetectionIdentity) {
		setConfirmedDetectionIdentity(detectionIdentity);
		setProfileConfirmed(false);
		setValues((current) => ({ ...current, commandProfileId: undefined, buildTarget: undefined }));
	}

	// Detection is advisory: when it has not loaded (or failed) the server runs its own detection at creation, so the
	// confirmation gate only applies once there is something concrete to confirm.
	const canCreate =
		values.trustedRepositoryAcknowledged &&
		selectedRepository?.availability === "Available" &&
		!repositoriesLoading &&
		(!detection || profileConfirmed);

	const submit = (event: FormEvent<HTMLFormElement>): void => {
		event.preventDefault();
		onSubmit(
			chosenProfileId ? { ...values, commandProfileId: chosenProfileId, buildTarget: chosenBuildTarget ?? undefined } : values,
		);
	};

	const selectRepository = (repositoryId: string): void => {
		setValues((current) => ({ ...current, selectedFolderId: repositoryId }));
	};

	return {
		values,
		setValues,
		selectRepository,
		repositoryOptions,
		candidates,
		chosenBuildTarget,
		chosenProfileId,
		whitespaceOnly,
		profileConfirmed,
		setProfileConfirmed,
		canCreate,
		submit,
	};
}
