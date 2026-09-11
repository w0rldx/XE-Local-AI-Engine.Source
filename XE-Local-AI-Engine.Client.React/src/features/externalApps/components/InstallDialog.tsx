import { Alert, Button, Group, Stack, Text } from "@mantine/core";
import { type RefObject, useEffect, useRef, useState } from "react";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { toast } from "@/core/ui/notifications/Toast";
import {
	externalAppConflictMessageKey,
	externalAppConflictTypes,
	readExternalAppConflict,
} from "@/features/externalApps/api/ExternalAppConflict";
import { PermissionsPanel } from "@/features/externalApps/components/PermissionsPanel";
import { VariablesForm } from "@/features/externalApps/components/VariablesForm";
import type { ExternalAppInstallPreview, ExternalAppSummaryView } from "@/features/externalApps/models/ExternalAppModels";
import { externalAppCapabilityLabel } from "@/features/externalApps/models/ExternalAppModels";
import {
	type ExternalAppVariableValues,
	initialVariableValues,
	validateExternalAppVariables,
} from "@/features/externalApps/models/ExternalAppVariables";
import { useExternalAppInstallPreview, useInstallExternalApp } from "@/features/externalApps/queries/useExternalApps";

interface InstallDialogProps {
	readonly application: ExternalAppSummaryView;
	readonly opened: boolean;
	readonly onClose: () => void;
	readonly onInstalled: (instanceId: string) => void;
}

const installSteps = ["permissions", "variables", "resources", "installing"] as const;
type InstallStep = (typeof installSteps)[number];

const keyPrefix = "pages.externalApps.install";

/**
 * Install is a disclosure before it is an action: the operator is shown what the application gets, accepts it, fills
 * in what it needs, sees whether this computer can run it, and only then installs.
 *
 * Two rules hold the whole flow together. The acceptance is bound to the manifest FINGERPRINT the operator was shown
 * — every preview result is compared against it, and a different pair sends the dialog back to step 1 rather than
 * quietly swapping the thing that was consented to. And the body's fingerprint is COPIED from that preview, never
 * re-derived, so a catalog that moved underneath is refused by the node instead of installed.
 *
 * No `confirmCloseWhen`: closing discards nothing. The install runs on the node and carries on without this dialog,
 * which is what `install.progress.keepsRunning` says.
 */
export function InstallDialog({ application, opened, onClose, onInstalled }: InstallDialogProps) {
	const { t } = useTranslation();
	const applicationId = application.id ?? "";
	const [step, setStep] = useState<InstallStep>("permissions");
	const [values, setValues] = useState<ExternalAppVariableValues>({});
	// The fingerprint the form's values were last built against, not a boolean: a preview that moved brings different
	// variable DEFINITIONS with it, and the values have to be reconciled against them rather than left as they were.
	const [seededFingerprint, setSeededFingerprint] = useState<string | null>(null);
	// The `(manifestVersion, manifestSha256)` pair the operator accepted, as one comparable string.
	const [acceptedFingerprint, setAcceptedFingerprint] = useState<string | null>(null);
	const primaryRef = useRef<HTMLButtonElement>(null);

	const previewQuery = useExternalAppInstallPreview(applicationId, opened);
	const install = useInstallExternalApp();
	const preview = previewQuery.data;
	const fingerprint = preview ? fingerprintOf(preview) : null;
	const definitions = preview?.variables ?? [];
	const issues = validateExternalAppVariables(definitions, values);

	// Seeded once per FINGERPRINT, so a refetch that returns the same manifest never throws away what the operator
	// typed. When the manifest moves, the values are reconciled against the new definitions: a name it no longer
	// declares is dropped (the node rejects an undeclared variable, so the retry 400s every time) and one it newly
	// declares starts from its default, while everything typed for a surviving name is kept.
	useEffect(() => {
		if (!preview || !fingerprint || fingerprint === seededFingerprint) {
			return;
		}
		const declared = preview.variables ?? [];
		setValues((previous) =>
			seededFingerprint === null ? initialVariableValues(declared) : initialVariableValues(declared, previous),
		);
		setSeededFingerprint(fingerprint);
	}, [preview, fingerprint, seededFingerprint]);

	// The resource numbers must be current, not whatever was true when the dialog opened.
	const { refetch } = previewQuery;
	useEffect(() => {
		if (step === "resources") {
			refetch().catch(() => undefined);
		}
	}, [step, refetch]);

	// A preview whose fingerprint differs from the accepted one describes something else, so the acceptance no longer
	// covers it. This fires on a SUCCESSFUL fetch, not only on a 409: the catalog can move without anyone being refused.
	useEffect(() => {
		if (acceptedFingerprint && fingerprint && fingerprint !== acceptedFingerprint) {
			setAcceptedFingerprint(null);
			setStep("permissions");
			toast.error(manifestChangedMessage(t));
		}
	}, [fingerprint, acceptedFingerprint, t]);

	// Each step hands focus to its primary action, so the dialog is operable without reaching for the mouse. The
	// fingerprint is a dependency because step 1's button is disabled until the preview lands, and focus does not
	// stick to a disabled control.
	// biome-ignore lint/correctness/useExhaustiveDependencies: the deps identify which primary button is mounted, which is what this effect reacts to; the ref is stable and would never re-run it.
	useEffect(() => {
		primaryRef.current?.focus();
	}, [step, fingerprint]);

	const close = (): void => {
		setStep("permissions");
		setValues({});
		setSeededFingerprint(null);
		setAcceptedFingerprint(null);
		onClose();
	};

	const accept = (): void => {
		if (!fingerprint) {
			return;
		}
		setAcceptedFingerprint(fingerprint);
		setStep("variables");
	};

	const submit = (): void => {
		if (!preview) {
			return;
		}
		setStep("installing");
		install.mutate(
			{
				body: {
					applicationId,
					// Copied from the preview the operator was shown, never re-derived from the catalog row.
					manifestVersion: preview.manifestVersion ?? 0,
					manifestSha256: preview.manifestSha256 ?? "",
					variables: { ...values },
					// Legitimate only because step 1's acceptance is what let the dialog reach this state.
					acceptPermissions: true,
				},
			},
			{
				onSuccess: (data) => {
					toast.success(t(`${keyPrefix}.succeeded`, { name: application.displayName ?? applicationId }));
					onInstalled(data.id ?? "");
					close();
				},
				onError: (error) => handleInstallError(error),
			},
		);
	};

	const handleInstallError = (error: unknown): void => {
		const conflict = readExternalAppConflict(error);
		if (conflict?.conflictType === externalAppConflictTypes.manifestChanged) {
			// The catalog moved between disclosure and submit: the acceptance has to be re-taken against what is on
			// offer now, so the dialog goes back to step 1 with a fresh preview rather than retrying the same body.
			setAcceptedFingerprint(null);
			setStep("permissions");
			refetch().catch(() => undefined);
			toast.error(manifestChangedMessage(t));
			return;
		}
		const messageKey = conflict ? externalAppConflictMessageKey(conflict.conflictType) : undefined;
		setStep("resources");
		toast.error(
			messageKey
				? t(messageKey)
				: apiErrorMessage(error, t(`${keyPrefix}.failed`, { name: application.displayName ?? applicationId })),
		);
	};

	return (
		<DialogShell
			opened={opened}
			onClose={close}
			title={t(`${keyPrefix}.title`, { name: application.displayName ?? applicationId })}
			data-testid="external-app-install-dialog"
			footer={
				<InstallFooter
					step={step}
					primaryRef={primaryRef}
					canAccept={Boolean(fingerprint)}
					canContinue={issues.length === 0}
					canInstall={preview?.canInstall === true}
					isPending={install.isPending}
					existingInstanceId={preview?.existingInstanceId ?? null}
					onCancel={close}
					onBack={setStep}
					onAccept={accept}
					onInstall={submit}
					onOpenExisting={(instanceId) => {
						onInstalled(instanceId);
						close();
					}}
				/>
			}
		>
			<Stack gap="md" data-testid={`external-app-install-step-${step}`}>
				<Text size="sm" c="dimmed">
					{t(`${keyPrefix}.steps.${step}`)}
				</Text>

				{step === "permissions" && preview ? (
					<Stack gap="sm">
						<Text size="sm">{t("pages.externalApps.catalog.testedVersion", { version: application.testedVersion ?? "" })}</Text>
						{application.license ? (
							<Text size="sm">{t("pages.externalApps.catalog.license", { license: application.license })}</Text>
						) : null}
						<PermissionsPanel permissions={preview.permissions} effectivePermissions={preview.effectivePermissions} />
					</Stack>
				) : null}

				{step === "variables" ? (
					<VariablesForm
						definitions={definitions}
						values={values}
						issues={issues}
						onChange={(name, value) => setValues((previous) => ({ ...previous, [name]: value }))}
					/>
				) : null}

				{step === "resources" && preview ? <ResourceStep preview={preview} /> : null}

				{step === "installing" ? <InstallingStep /> : null}
			</Stack>
		</DialogShell>
	);
}

/** The one sentence for a catalog that moved between disclosure and submit, read off the conflict map's own key. */
function manifestChangedMessage(t: TFunction): string {
	return t(externalAppConflictMessageKey(externalAppConflictTypes.manifestChanged) ?? "");
}

function fingerprintOf(preview: ExternalAppInstallPreview): string {
	return `${preview.manifestVersion ?? 0}:${preview.manifestSha256 ?? ""}`;
}

/**
 * Memory and disk as the node measured them, plus the reason the install is refused when it is.
 *
 * `canInstall` is the gate, not the resource arithmetic: a GPU an application requires, a runtime that cannot offer
 * what it needs and an application already installed all read as "enough memory and disk" here.
 */
function ResourceStep({ preview }: { preview: ExternalAppInstallPreview }) {
	const { t } = useTranslation();
	const check = preview.resourceCheck;
	const blockedReason = preview.blockedReason ?? null;
	const missingCapabilities = preview.missingCapabilities ?? [];

	return (
		<Stack gap="sm" data-testid="external-app-install-resources">
			<Text size="sm">
				{t(`${keyPrefix}.resources.memory`, {
					required: megabytes(check?.requiredMemoryBytes),
					available: megabytes(check?.availableMemoryBytes),
				})}
			</Text>
			<Text size="sm">
				{t(`${keyPrefix}.resources.disk`, {
					required: megabytes(check?.requiredDiskBytes),
					available: megabytes(check?.availableDiskBytes),
				})}
			</Text>
			<Text size="sm">{t(`${keyPrefix}.resources.${check?.satisfied === true ? "ok" : "blocked"}`)}</Text>

			{missingCapabilities.length > 0 ? (
				<Text size="sm" data-testid="external-app-install-missing-capabilities">
					{t("pages.externalApps.runtime.missingCapabilities", {
						capabilities: missingCapabilities.map((name) => externalAppCapabilityLabel(name, t)).join(", "),
					})}
				</Text>
			) : null}

			{blockedReason ? (
				<Alert color="yellow" data-testid="external-app-install-blocked">
					{t(`${keyPrefix}.blocked.${blockedReason}`)}
				</Alert>
			) : null}
		</Stack>
	);
}

/** Bytes → whole MB, the unit the copy promises. */
function megabytes(bytes: number | null | undefined): number {
	return Math.round((bytes ?? 0) / 1_000_000);
}

/**
 * The install is running on the node. Per-service pull progress is NOT rendered here: the hub is subscribed per
 * INSTANCE and the id does not exist until the 202 lands, so the download lines belong on the instance detail page,
 * which is where `install.progress.downloading` is rendered from `useExternalAppHub`.
 */
function InstallingStep() {
	const { t } = useTranslation();

	return (
		<Stack gap="sm" data-testid="external-app-install-progress">
			<Text size="sm">{t(`${keyPrefix}.progress.starting`)}</Text>
			<Text size="sm" c="dimmed">
				{t(`${keyPrefix}.progress.keepsRunning`)}
			</Text>
		</Stack>
	);
}

interface InstallFooterProps {
	readonly step: InstallStep;
	readonly primaryRef: RefObject<HTMLButtonElement | null>;
	readonly canAccept: boolean;
	readonly canContinue: boolean;
	readonly canInstall: boolean;
	readonly isPending: boolean;
	readonly existingInstanceId: string | null;
	readonly onCancel: () => void;
	readonly onBack: (step: InstallStep) => void;
	readonly onAccept: () => void;
	readonly onInstall: () => void;
	readonly onOpenExisting: (instanceId: string) => void;
}

function InstallFooter({
	step,
	primaryRef,
	canAccept,
	canContinue,
	canInstall,
	isPending,
	existingInstanceId,
	onCancel,
	onBack,
	onAccept,
	onInstall,
	onOpenExisting,
}: InstallFooterProps) {
	const { t } = useTranslation();

	if (step === "installing") {
		return (
			<Button ref={primaryRef} variant="default" onClick={onCancel} data-testid="external-app-install-close">
				{t("common.close")}
			</Button>
		);
	}

	if (step === "permissions") {
		return (
			<Group justify="flex-end">
				<Button variant="subtle" onClick={onCancel} data-testid="external-app-install-cancel">
					{t("common.cancel")}
				</Button>
				{/* The act of acceptance, not a navigation: the label says so, and the server's validator rejects a
				    body claiming an acceptance this button never gave. */}
				<Button
					ref={primaryRef}
					disabled={!canAccept}
					data-autofocus={true}
					onClick={onAccept}
					data-testid="external-app-install-accept"
				>
					{t(`${keyPrefix}.acceptPermissions`)}
				</Button>
			</Group>
		);
	}

	if (step === "variables") {
		return (
			<Group justify="flex-end">
				<Button variant="subtle" onClick={() => onBack("permissions")} data-testid="external-app-install-back">
					{t(`${keyPrefix}.back`)}
				</Button>
				<Button
					ref={primaryRef}
					disabled={!canContinue}
					data-autofocus={true}
					onClick={() => onBack("resources")}
					data-testid="external-app-install-next"
				>
					{t(`${keyPrefix}.next`)}
				</Button>
			</Group>
		);
	}

	// An application that already has an instance is not installed a second time; the action becomes a way to it.
	if (existingInstanceId) {
		return (
			<Group justify="flex-end">
				<Button variant="subtle" onClick={() => onBack("variables")} data-testid="external-app-install-back">
					{t(`${keyPrefix}.back`)}
				</Button>
				<Button
					ref={primaryRef}
					data-autofocus={true}
					onClick={() => onOpenExisting(existingInstanceId)}
					data-testid="external-app-install-open-existing"
				>
					{t("pages.externalApps.catalog.details")}
				</Button>
			</Group>
		);
	}

	return (
		<Group justify="flex-end">
			<Button variant="subtle" onClick={() => onBack("variables")} data-testid="external-app-install-back">
				{t(`${keyPrefix}.back`)}
			</Button>
			<Button
				ref={primaryRef}
				disabled={!canInstall}
				loading={isPending}
				data-autofocus={true}
				onClick={onInstall}
				data-testid="external-app-install-confirm"
			>
				{t(`${keyPrefix}.confirm`)}
			</Button>
		</Group>
	);
}
