import { Alert, Button, Group, Stack, Text } from "@mantine/core";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { toast } from "@/core/ui/notifications/Toast";
import {
	externalAppConflictMessageKey,
	externalAppConflictTypes,
	isExternalAppStaleRowConflict,
	readExternalAppConflict,
} from "@/features/externalApps/api/ExternalAppConflict";
import { PermissionsPanel } from "@/features/externalApps/components/PermissionsPanel";
import { VariablesForm } from "@/features/externalApps/components/VariablesForm";
import type { ExternalAppInstanceView, ExternalAppUpdatePreview } from "@/features/externalApps/models/ExternalAppModels";
import {
	type ExternalAppVariableValues,
	initialVariableValues,
	storedSecretNames,
	validateExternalAppVariables,
} from "@/features/externalApps/models/ExternalAppVariables";
import {
	invalidateExternalAppInstance,
	useExternalAppUpdatePreview,
	useUpdateExternalApp,
} from "@/features/externalApps/queries/useExternalApps";

interface UpdateDialogProps {
	readonly instance: ExternalAppInstanceView;
	readonly opened: boolean;
	readonly onClose: () => void;
}

const updateSteps = ["variables", "permissions", "resources", "updating"] as const;
type UpdateStep = (typeof updateSteps)[number];

const keyPrefix = "pages.externalApps.update";

/**
 * An update is an install against a different manifest, so it gets the same disclosure rather than a bare confirm:
 * the target's settings, then what it asks for beyond what the installed version already had, then whether this
 * computer can run it.
 *
 * The diff is shown BEFORE the call. A 409 `…PermissionChangeRequiresAcknowledgement` therefore means this dialog was
 * bypassed, and it reopens at the permission step with the names the problem details carried.
 *
 * The preview has no `manifestVersion` member: the confirm body's `manifestVersion` is `targetManifestVersion` and
 * its `manifestSha256` is this preview's. Both are COPIED from the preview the operator was shown — an acceptance
 * bound to a fingerprint that moved is no acceptance at all.
 */
export function UpdateDialog({ instance, opened, onClose }: UpdateDialogProps) {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const instanceId = instance.id ?? "";
	const name = instance.displayName ?? instanceId;

	const [step, setStep] = useState<UpdateStep>("variables");
	const [values, setValues] = useState<ExternalAppVariableValues>({});
	// The fingerprint the form's values were last built against, not a boolean — see the reconcile effect below.
	const [seededFingerprint, setSeededFingerprint] = useState<string | null>(null);
	const [acceptedFingerprint, setAcceptedFingerprint] = useState<string | null>(null);
	// Set only by the bypassed-dialog 409: the server's list, never one this client computed.
	const [refusedPermissions, setRefusedPermissions] = useState<readonly string[] | null>(null);

	const previewQuery = useExternalAppUpdatePreview(instanceId, opened);
	const update = useUpdateExternalApp();
	const preview = previewQuery.data;
	const { refetch } = previewQuery;
	const fingerprint = preview ? fingerprintOf(preview) : null;
	const definitions = preview?.variables ?? [];
	const issues = validateExternalAppVariables(definitions, values);
	const addedPermissions = refusedPermissions ?? preview?.addedPermissions ?? [];
	// A required target variable the installed manifest never declared has nothing to seed it, so it renders empty and
	// blocks Continue for a reason the operator cannot otherwise see. Named here rather than derived in the form: only
	// the update knows what the installed manifest had.
	const installedVariableNames = new Set((instance.manifest?.variables ?? []).map((variable) => variable.name ?? ""));
	const newlyRequired = definitions
		.filter((definition) => definition.required === true && !installedVariableNames.has(definition.name ?? ""))
		.map((definition) => definition.name ?? "");

	// Seeded once per FINGERPRINT from the masked current values, so a refetch never discards what was typed. A
	// variable the target newly requires has no stored value and renders empty, asterisked and blocking.
	//
	// A preview that MOVED declares different variables, so the values are reconciled against them: a name the new
	// target does not declare is dropped rather than resent (the node refuses an undeclared variable, which made every
	// retry a 400), one it newly declares falls back to the stored value and then to its default, and what the
	// operator typed for a surviving name wins over both.
	useEffect(() => {
		if (!preview || !fingerprint || fingerprint === seededFingerprint) {
			return;
		}
		const declared = preview.variables ?? [];
		const stored = preview.currentValues ?? {};
		setValues((previous) =>
			seededFingerprint === null
				? initialVariableValues(declared, stored)
				: initialVariableValues(declared, { ...stored, ...previous }),
		);
		setSeededFingerprint(fingerprint);
	}, [preview, fingerprint, seededFingerprint]);

	// A preview whose fingerprint differs from the accepted one describes a different update, so the acceptance no
	// longer covers it. This fires on a SUCCESSFUL fetch too: the catalog can move without anyone being refused.
	useEffect(() => {
		if (acceptedFingerprint && fingerprint && fingerprint !== acceptedFingerprint) {
			setAcceptedFingerprint(null);
			setStep("variables");
			toast.error(t(externalAppConflictMessageKey(externalAppConflictTypes.manifestChanged) ?? ""));
		}
	}, [fingerprint, acceptedFingerprint, t]);

	const close = (): void => {
		setStep("variables");
		setValues({});
		setSeededFingerprint(null);
		setAcceptedFingerprint(null);
		setRefusedPermissions(null);
		onClose();
	};

	// LEAVING THE SETTINGS STEP is what binds the acceptance, on both branches. Binding it only on the rendered
	// permission step left `acceptedFingerprint` null for an update that asks for nothing more, which made the
	// drift effect below inert: a preview that moved under the open dialog was then confirmed with
	// `acceptPermissions: true` against a manifest nobody had been shown.
	const leaveSettings = (): void => {
		setAcceptedFingerprint(fingerprint);
		setStep(addedPermissions.length > 0 ? "permissions" : "resources");
	};

	const accept = (): void => {
		setAcceptedFingerprint(fingerprint);
		setStep("resources");
	};

	const submit = (): void => {
		if (!preview) {
			return;
		}
		setStep("updating");
		update.mutate(
			{
				path: { instanceId },
				body: {
					// `targetManifestVersion`, not the instance's: the body names the manifest being moved TO.
					manifestVersion: preview.targetManifestVersion ?? 0,
					manifestSha256: preview.manifestSha256 ?? "",
					acceptPermissions: true,
					variables: { ...values },
					expectedVersion: instance.version ?? 0,
				},
			},
			{
				onSuccess: () => {
					toast.success(t(`${keyPrefix}.succeeded`, { name }));
					close();
				},
				onError: (error) => handleUpdateError(error),
			},
		);
	};

	const handleUpdateError = (error: unknown): void => {
		const conflict = readExternalAppConflict(error);
		if (conflict?.conflictType === externalAppConflictTypes.manifestChanged) {
			setAcceptedFingerprint(null);
			setStep("variables");
			refetch().catch(() => undefined);
			toast.error(t(externalAppConflictMessageKey(externalAppConflictTypes.manifestChanged) ?? ""));
			return;
		}
		if (conflict?.conflictType === externalAppConflictTypes.permissionChangeRequiresAcknowledgement) {
			// The dialog was bypassed. Reopen at the diff with the server's own names rather than toasting.
			setRefusedPermissions(conflict.addedPermissions ?? []);
			setAcceptedFingerprint(null);
			setStep("permissions");
			return;
		}
		const messageKey = conflict ? externalAppConflictMessageKey(conflict.conflictType) : undefined;
		toast.error(messageKey ? t(messageKey) : apiErrorMessage(error, t(`${keyPrefix}.failed`, { name })));
		// The rejected `expectedVersion` came from the instance this dialog was opened over. Refetching only the
		// PREVIEW left that row cached, so every retry echoed the same refused token; the instance and the list have
		// to be dropped as well, and the dialog re-renders against the re-read version.
		if (isExternalAppStaleRowConflict(conflict?.conflictType)) {
			invalidateExternalAppInstance(queryClient, instanceId);
		}
		// A 404 means the application left the catalog: the refetched preview says so with `CatalogMissing` under the
		// disabled confirm, which is more use than a toast that disappears.
		setStep("resources");
		refetch().catch(() => undefined);
	};

	const blockedReason = preview?.blockedReason ?? null;
	const canUpdate = preview?.canUpdate === true;

	return (
		<DialogShell
			opened={opened}
			onClose={close}
			title={t(`${keyPrefix}.title`, { name })}
			size="lg"
			data-testid="external-app-update-dialog"
			footer={
				<Group justify="flex-end" gap="sm">
					{step === "permissions" || step === "resources" ? (
						<Button
							variant="default"
							onClick={() =>
								setStep(step === "resources" ? (addedPermissions.length > 0 ? "permissions" : "variables") : "variables")
							}
							data-testid="external-app-update-back"
						>
							{t("pages.externalApps.install.back")}
						</Button>
					) : null}
					{step === "variables" ? (
						<Button disabled={issues.length > 0 || !preview} onClick={leaveSettings} data-testid="external-app-update-next">
							{t("pages.externalApps.install.next")}
						</Button>
					) : null}
					{step === "permissions" ? (
						<Button disabled={!fingerprint} onClick={accept} data-testid="external-app-update-accept">
							{t("pages.externalApps.actions.updateConfirm.confirm")}
						</Button>
					) : null}
					{step === "resources" ? (
						<Button disabled={!canUpdate} loading={update.isPending} onClick={submit} data-testid="external-app-update-confirm">
							{t("pages.externalApps.actions.updateConfirm.confirm")}
						</Button>
					) : null}
				</Group>
			}
		>
			<Stack gap="md" data-testid={`external-app-update-step-${step}`}>
				<Text size="sm" c="dimmed">
					{t(`${keyPrefix}.targetVersion`, { version: preview?.targetManifestVersion ?? "" })} ·{" "}
					{t("pages.externalApps.detail.version")} {preview?.currentManifestVersion ?? ""}
				</Text>

				{step === "variables" && preview ? (
					<VariablesForm
						definitions={definitions}
						values={values}
						issues={issues}
						storedSecrets={storedSecretNames(preview.currentValues ?? undefined)}
						newlyRequired={newlyRequired}
						onChange={(variable, value) => setValues((previous) => ({ ...previous, [variable]: value }))}
					/>
				) : null}

				{/* The TARGET's grants, never the installed manifest's: this is the thing being consented to. */}
				{step === "permissions" && preview ? (
					<PermissionsPanel effectivePermissions={preview.effectivePermissions} addedPermissions={addedPermissions} />
				) : null}

				{step === "resources" && preview ? (
					<Stack gap="sm" data-testid="external-app-update-resources">
						{addedPermissions.length === 0 ? <Text size="sm">{t(`${keyPrefix}.noPermissionChanges`)}</Text> : null}
						<Text size="sm">
							{t("pages.externalApps.install.resources.memory", {
								required: megabytes(preview.resourceVerdict?.requiredMemoryBytes),
								available: megabytes(preview.resourceVerdict?.availableMemoryBytes),
							})}
						</Text>
						<Text size="sm">
							{t("pages.externalApps.install.resources.disk", {
								required: megabytes(preview.resourceVerdict?.requiredDiskBytes),
								available: megabytes(preview.resourceVerdict?.availableDiskBytes),
							})}
						</Text>
						{blockedReason ? (
							<Alert color="yellow" data-testid="external-app-update-blocked">
								{t(`${keyPrefix}.blocked.${blockedReason}`)}
							</Alert>
						) : null}
					</Stack>
				) : null}

				{step === "updating" ? (
					<Text size="sm" data-testid="external-app-update-progress">
						{t("pages.externalApps.install.progress.keepsRunning")}
					</Text>
				) : null}
			</Stack>
		</DialogShell>
	);
}

function fingerprintOf(preview: ExternalAppUpdatePreview): string {
	return `${preview.targetManifestVersion ?? 0}:${preview.manifestSha256 ?? ""}`;
}

/** Bytes → whole MB, the unit the copy promises. */
function megabytes(bytes: number | null | undefined): number {
	return Math.round((bytes ?? 0) / 1_000_000);
}
