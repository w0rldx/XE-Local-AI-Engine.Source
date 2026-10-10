import { Anchor, Badge, Button, Card, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { IconReload, IconTrash } from "@tabler/icons-react";
import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import type { XeLocalAiEngineClientEndpointsPythonV1ManagedPythonEnvironmentResponse as ManagedPythonEnvironment } from "@/core/api/generated";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { toast } from "@/core/ui/notifications/Toast";
import {
	useManagedPythonStatus,
	useRemoveComputePython,
	useRepairComputePython,
} from "@/features/node-settings/queries/useManagedPython";

const translationPrefix = "pages.nodeSettings.managedPython";

// Every state the status endpoint reports. The wire type is a plain string, so an unknown state (a newer node) falls
// back to its raw name rather than a missing translation key.
const stateColors: Readonly<Record<string, string>> = {
	NotProvisioned: "gray",
	Provisioning: "blue",
	Ready: "green",
	UpdateRequired: "yellow",
	RepairRequired: "orange",
	Failed: "red",
	Unsupported: "gray",
};

const knownProfiles = new Set(["training", "compute"]);
const knownMismatches = new Set([
	"profileId",
	"pythonMinor",
	"lockfile",
	"profileRevision",
	"rid",
	"probeContract",
	"toolchainStore",
]);

// The node's English platform reasons (ManagedPythonStatusService) that the card can translate; any other reason, or
// one a newer node words differently, renders raw.
const knownUnsupportedReasons: Readonly<Record<string, string>> = {
	"Training is available on Linux x64 only.": "trainingLinuxX64Only",
	"The Python compute tool is available on Linux x64 only.": "computeLinuxX64Only",
};

type ComputeAction = "repair" | "remove";

/**
 * The shared uv/CPython toolchain and the feature environments built on it. Read-only apart from Compute's repair and
 * remove: the Training environment has its own install flow on the Training page, so this card links there instead
 * of duplicating it.
 */
export function ManagedPythonCard() {
	const { t } = useTranslation();
	const status = useManagedPythonStatus();
	const repair = useRepairComputePython();
	const remove = useRemoveComputePython();
	// The action survives the dialog closing, so the title does not flip mid close-animation.
	const [confirmAction, setConfirmAction] = useState<ComputeAction>("repair");
	const [confirmOpen, setConfirmOpen] = useState(false);

	const openConfirm = (action: ComputeAction): void => {
		setConfirmAction(action);
		setConfirmOpen(true);
	};
	const confirm = (): void => {
		setConfirmOpen(false);
		(confirmAction === "repair" ? repair : remove).mutate(
			{},
			{ onError: (error) => toast.error(apiErrorMessage(error, t(`${translationPrefix}.${confirmAction}Error`))) },
		);
	};

	const reasonText = (environment: ManagedPythonEnvironment): string => {
		const reason = environment.reason ?? "";
		return environment.state === "Unsupported" && Object.hasOwn(knownUnsupportedReasons, reason)
			? t(`${translationPrefix}.unsupportedReasons.${knownUnsupportedReasons[reason]}`)
			: reason;
	};

	const stateLabel = (state: string): string =>
		Object.hasOwn(stateColors, state) ? t(`${translationPrefix}.states.${state}`) : state;

	// A host with no supported environment at all (an unsupported OS or architecture) will never download uv; saying
	// "not downloaded yet" there promises something that cannot happen, and Compute's repair/remove have nothing to act on.
	const environments = status.data?.environments ?? [];
	const unsupported = environments.length > 0 && environments.every((environment) => environment.state === "Unsupported");

	const renderEnvironment = (environment: ManagedPythonEnvironment) => {
		const isCompute = environment.profileId === "compute";
		const actionsDisabled =
			environment.state === "Unsupported" || environment.state === "Provisioning" || repair.isPending || remove.isPending;
		return (
			<Stack gap="xs" key={environment.profileId} data-testid={`managed-python-environment-${environment.profileId}`}>
				<Group gap="sm">
					<Title order={3} size="h5">
						{knownProfiles.has(environment.profileId)
							? t(`${translationPrefix}.profiles.${environment.profileId}`)
							: environment.profileId}
					</Title>
					<Badge color={Object.hasOwn(stateColors, environment.state) ? stateColors[environment.state] : "gray"}>
						{stateLabel(environment.state)}
					</Badge>
				</Group>
				{environment.reason ? (
					<Text size="sm" c="dimmed">
						{reasonText(environment)}
					</Text>
				) : null}
				{environment.installed ? (
					<Text size="sm">{t(`${translationPrefix}.pythonMinor`, { version: environment.installed.pythonMinor })}</Text>
				) : null}
				{environment.state === "UpdateRequired" && environment.mismatches.length > 0 ? (
					<Stack gap={2}>
						<Text size="sm">{t(`${translationPrefix}.mismatchesTitle`)}</Text>
						<ul>
							{environment.mismatches.map((mismatch) => (
								<li key={mismatch}>
									<Text size="sm">
										{knownMismatches.has(mismatch) ? t(`${translationPrefix}.mismatches.${mismatch}`) : mismatch}
									</Text>
								</li>
							))}
						</ul>
					</Stack>
				) : null}
				{environment.profileId === "training" && nodeCapabilities.training ? (
					<Anchor component={Link} to={nodeRoutePaths.training} size="sm" data-testid="managed-python-training-link">
						{t(`${translationPrefix}.trainingLink`)}
					</Anchor>
				) : null}
				{isCompute && !unsupported ? (
					<Group>
						<Button
							variant="light"
							leftSection={<IconReload size={16} />}
							disabled={actionsDisabled}
							loading={repair.isPending}
							onClick={() => openConfirm("repair")}
						>
							{t(`${translationPrefix}.repair`)}
						</Button>
						<Button
							color="red"
							variant="light"
							leftSection={<IconTrash size={16} />}
							disabled={actionsDisabled}
							loading={remove.isPending}
							onClick={() => openConfirm("remove")}
						>
							{t(`${translationPrefix}.remove`)}
						</Button>
					</Group>
				) : null}
			</Stack>
		);
	};

	const toolchain = status.data?.toolchain;
	const uvKey = toolchain?.uvPresent ? "uvPresent" : unsupported ? "uvUnsupported" : "uvMissing";

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="managed-python-card">
			<Stack gap="md">
				<Title order={2} size="h4">
					{t(`${translationPrefix}.title`)}
				</Title>
				<Text size="sm" c="dimmed">
					{t(`${translationPrefix}.description`)}
				</Text>

				{status.isLoading ? (
					<Group gap="sm" role="status" aria-live="polite">
						<Loader size="sm" />
						<Text c="dimmed">{t(`${translationPrefix}.loading`)}</Text>
					</Group>
				) : null}
				{status.error ? <InlineErrorAlert message={apiErrorMessage(status.error, t(`${translationPrefix}.loadError`))} /> : null}

				{toolchain ? (
					<Stack gap="xs" data-testid="managed-python-toolchain">
						<Group gap="sm">
							<Text size="sm">{t(`${translationPrefix}.uvVersion`, { version: toolchain.uvVersion })}</Text>
							<Badge color={toolchain.uvPresent ? "green" : "gray"} data-testid="managed-python-uv-badge">
								{t(`${translationPrefix}.${uvKey}`)}
							</Badge>
						</Group>
						<Text size="sm">{t(`${translationPrefix}.pythonInstallsTitle`)}</Text>
						{toolchain.pythonInstalls.length > 0 ? (
							<ul>
								{toolchain.pythonInstalls.map((install) => (
									<li key={install}>
										<Text size="sm" ff="monospace">
											{install}
										</Text>
									</li>
								))}
							</ul>
						) : (
							<Text size="sm" c="dimmed">
								{t(`${translationPrefix}.noPythonInstalls`)}
							</Text>
						)}
					</Stack>
				) : null}

				{status.data?.environments.map(renderEnvironment)}
			</Stack>

			<DialogShell
				opened={confirmOpen}
				onClose={() => setConfirmOpen(false)}
				title={t(`${translationPrefix}.${confirmAction}ConfirmTitle`)}
				size="sm"
				enableFullScreenToggle={false}
				data-testid="managed-python-confirm"
				footer={
					<>
						<Button variant="default" onClick={() => setConfirmOpen(false)}>
							{t("common.cancel")}
						</Button>
						<Button color={confirmAction === "remove" ? "red" : undefined} onClick={confirm}>
							{t(`${translationPrefix}.${confirmAction}Confirm`)}
						</Button>
					</>
				}
			>
				<Text size="sm">{t(`${translationPrefix}.${confirmAction}ConfirmBody`)}</Text>
			</DialogShell>
		</Card>
	);
}
