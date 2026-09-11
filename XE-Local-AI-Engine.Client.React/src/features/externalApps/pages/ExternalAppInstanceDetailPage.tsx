import { Alert, Anchor, Button, Group, Progress, Skeleton, Stack, Table, Tabs, Text } from "@mantine/core";
import { IconApps } from "@tabler/icons-react";
import { useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { getErrorStatus } from "@/core/api/errors/RetryClassification";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { toast } from "@/core/ui/notifications/Toast";
import {
	externalAppConflictMessageKey,
	isExternalAppStaleRowConflict,
	readExternalAppConflict,
} from "@/features/externalApps/api/ExternalAppConflict";
import { ExternalAppStatusBadge } from "@/features/externalApps/components/ExternalAppStatusBadge";
import { InstanceActions } from "@/features/externalApps/components/InstanceActions";
import { InstanceEventsList } from "@/features/externalApps/components/InstanceEventsList";
import { InstanceLogsPanel } from "@/features/externalApps/components/InstanceLogsPanel";
import { PermissionsPanel } from "@/features/externalApps/components/PermissionsPanel";
import { UpdateDialog } from "@/features/externalApps/components/UpdateDialog";
import { VariablesForm } from "@/features/externalApps/components/VariablesForm";
import { useExternalAppHub } from "@/features/externalApps/hooks/useExternalAppHub";
import {
	isExternalAppConfigurable,
	toExternalAppFailureCategory,
	toExternalAppStatus,
} from "@/features/externalApps/models/ExternalAppModels";
import {
	type ExternalAppVariableValues,
	initialVariableValues,
	storedSecretNames,
	validateExternalAppVariables,
} from "@/features/externalApps/models/ExternalAppVariables";
import {
	invalidateExternalAppInstance,
	useExternalAppInstance,
	useExternalAppRuntime,
	useUpdateExternalAppVariables,
} from "@/features/externalApps/queries/useExternalApps";

interface ExternalAppInstanceDetailPageProps {
	/** The instance this page is open on. Passed as a prop so the page stays router-free and unit-testable. */
	readonly instanceId: string;
}

const keyPrefix = "pages.externalApps.detail";

/**
 * One installed application.
 *
 * Everything here is driven by the INSTANCE, never the catalog: `instance.manifest` is the sanitised snapshot taken
 * at install time, tested version included, so an application the catalog dropped still renders completely. No
 * catalog query is mounted on this page.
 *
 * Rows are shown exactly as the server sent them. A runtime that cannot be reached adds one banner and changes
 * nothing else — a transient status keeps its spinner rather than being repainted as failed, because reconciliation
 * settles those rows once a ready runtime returns and a guess in the meantime would be wrong half the time.
 */
export function ExternalAppInstanceDetailPage({ instanceId }: ExternalAppInstanceDetailPageProps) {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const [updateOpen, setUpdateOpen] = useState(false);

	const live = useExternalAppHub(instanceId);
	const instanceQuery = useExternalAppInstance(instanceId, { pollIntervalMs: live.pollIntervalMs });
	const runtimeQuery = useExternalAppRuntime();
	// A 404 is the only answer that means the row is GONE, and the query keeps its last data across a failed refetch:
	// after an uninstall settled with this page open, the hub-triggered re-read 404d and the deleted application stayed
	// on screen, frozen at `Uninstalling`. Every other failure leaves what was last read standing, which is right —
	// a node that briefly refused is not a reason to blank a page. 404 never retries (`shouldRetryQuery`).
	const missing = getErrorStatus(instanceQuery.error) === 404;
	const instance = missing ? undefined : instanceQuery.data;

	if (instanceQuery.isLoading) {
		return (
			<PageShell data-testid="external-app-instance-detail-page">
				<Skeleton height={160} radius="md" data-testid="external-app-detail-loading" />
			</PageShell>
		);
	}

	if (!instance) {
		return (
			<PageShell data-testid="external-app-instance-detail-page">
				<Stack gap="sm" data-testid="external-app-detail-not-found">
					<Text>{t(`${keyPrefix}.notFound`)}</Text>
					<Anchor component="button" type="button" onClick={() => navigate({ to: "/external-apps/installed" })}>
						{t(`${keyPrefix}.back`)}
					</Anchor>
				</Stack>
			</PageShell>
		);
	}

	const status = toExternalAppStatus(instance.status);
	const manifest = instance.manifest;
	const openTarget = (instance.publishedPorts ?? []).find((port) => Boolean(port.url));
	const pullProgress = Object.values(live.pullProgress);

	return (
		<PageShell data-testid="external-app-instance-detail-page">
			<PageHeader
				title={instance.displayName ?? instanceId}
				icon={<IconApps size={24} />}
				subtitle={<ExternalAppStatusBadge status={status} />}
				actions={<InstanceActions instance={instance} onUpdate={() => setUpdateOpen(true)} />}
			/>

			{live.pollIntervalMs ? (
				<Text size="sm" c="dimmed" data-testid="external-app-detail-live-degraded">
					{t(`${keyPrefix}.liveDegraded`)}
				</Text>
			) : null}

			{runtimeQuery.data?.available === false ? (
				<Alert color="yellow" data-testid="external-app-detail-runtime-unavailable">
					{t("pages.externalApps.runtime.unavailable")}
				</Alert>
			) : null}

			{/* The only place a download in flight is visible: no REST read reports a pull that is still running. */}
			{pullProgress.length > 0 ? (
				<Stack gap={4} data-testid="external-app-detail-pull-progress">
					{pullProgress.map((progress) => (
						<Stack key={progress.service} gap={2}>
							<Text size="sm">
								{t("pages.externalApps.install.progress.downloading", {
									service: progress.service,
									completed: progress.completedLayers,
									total: progress.layerCount,
								})}
							</Text>
							<Progress value={progress.layerCount > 0 ? (progress.completedLayers / progress.layerCount) * 100 : 0} />
						</Stack>
					))}
				</Stack>
			) : null}

			{instance.failureCategory ? (
				<Alert color="red" title={t(`${keyPrefix}.failureTitle`)} data-testid="external-app-detail-failure">
					{t(`pages.externalApps.failure.${toExternalAppFailureCategory(instance.failureCategory)}`)}
					{instance.failureSummary ? ` ${instance.failureSummary}` : ""}
				</Alert>
			) : null}

			{instance.updateAvailable === true ? (
				<Alert color="blue" data-testid="external-app-detail-update-available">
					<Group justify="space-between">
						<Text size="sm">{t(`${keyPrefix}.updateAvailable`, { version: instance.availableManifestVersion ?? "" })}</Text>
						<Button variant="default" onClick={() => setUpdateOpen(true)} data-testid="external-app-detail-update-open">
							{t("pages.externalApps.actions.update")}
						</Button>
					</Group>
				</Alert>
			) : null}

			<Tabs defaultValue="overview">
				<Tabs.List>
					<Tabs.Tab value="overview" data-testid="external-app-detail-tab-overview">
						{t(`${keyPrefix}.tabs.overview`)}
					</Tabs.Tab>
					<Tabs.Tab value="settings" data-testid="external-app-detail-tab-settings">
						{t(`${keyPrefix}.tabs.settings`)}
					</Tabs.Tab>
					<Tabs.Tab value="history" data-testid="external-app-detail-tab-history">
						{t(`${keyPrefix}.tabs.history`)}
					</Tabs.Tab>
					<Tabs.Tab value="logs" data-testid="external-app-detail-tab-logs">
						{t(`${keyPrefix}.tabs.logs`)}
					</Tabs.Tab>
				</Tabs.List>

				<Tabs.Panel value="overview" pt="md">
					<Stack gap="md" data-testid="external-app-detail-overview">
						<Table>
							<Table.Tbody>
								<DetailRow label={t(`${keyPrefix}.status`)} value={t(`pages.externalApps.status.${status}`)} />
								<DetailRow label={t(`${keyPrefix}.runtime`)} value={instance.runtimeProvider ?? ""} />
								<DetailRow
									label={t(`${keyPrefix}.version`)}
									value={t("pages.externalApps.catalog.testedVersion", { version: manifest?.testedVersion ?? "" })}
								/>
								<DetailRow label={t(`${keyPrefix}.access`)} value={t(`${keyPrefix}.accessValue`)} />
								<DetailRow label={t(`${keyPrefix}.storage`)} value={t(`${keyPrefix}.storageValue`)} />
								<DetailRow
									label={t(`${keyPrefix}.address`)}
									value={openTarget?.url ?? t(`${keyPrefix}.addressPending`)}
									testId="external-app-detail-address"
								/>
							</Table.Tbody>
						</Table>
						<PermissionsPanel permissions={manifest?.permissions} />
					</Stack>
				</Tabs.Panel>

				<Tabs.Panel value="settings" pt="md">
					<SettingsTab instance={instance} />
				</Tabs.Panel>

				<Tabs.Panel value="history" pt="md">
					<InstanceEventsList instanceId={instanceId} />
				</Tabs.Panel>

				<Tabs.Panel value="logs" pt="md">
					<InstanceLogsPanel instance={instance} />
				</Tabs.Panel>
			</Tabs>

			{updateOpen ? <UpdateDialog instance={instance} opened={true} onClose={() => setUpdateOpen(false)} /> : null}
		</PageShell>
	);
}

function DetailRow({ label, value, testId }: { label: string; value: string; testId?: string }) {
	return (
		<Table.Tr>
			<Table.Th w="30%">{label}</Table.Th>
			<Table.Td data-testid={testId}>{value}</Table.Td>
		</Table.Tr>
	);
}

/**
 * The settings of a settled instance. The variable definitions come from `instance.manifest`, never the catalog.
 *
 * The tab echoes the `version` it rendered as `expectedVersion` on every save: a stale token is a 409 that toasts and
 * invalidates, so the next save carries a fresh one. A saved change takes effect on the next start, which is why the
 * container must not be running to make one — `isExternalAppConfigurable` is the single place that rule lives, and it
 * mirrors what `ExternalAppService.ConfigureAsync` admits (`Failed` included, or a bad setting could not be repaired).
 */
function SettingsTab({ instance }: { instance: NonNullable<ReturnType<typeof useExternalAppInstance>["data"]> }) {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const save = useUpdateExternalAppVariables();
	const definitions = instance.manifest?.variables ?? [];
	const [values, setValues] = useState<ExternalAppVariableValues>({});
	// The installed manifest the form's values were last built against, not a boolean — see the reconcile effect. The
	// instance carries no manifest hash, so the snapshot's own `manifestVersion` is its identity; an update is the only
	// thing that swaps the snapshot and it always moves that number.
	const [seededFingerprint, setSeededFingerprint] = useState<string | null>(null);
	const fingerprint = String(instance.manifestVersion ?? 0);
	const issues = validateExternalAppVariables(definitions, values);
	const configurable = isExternalAppConfigurable(toExternalAppStatus(instance.status));
	// The whole form is locked while the save is in flight: the node answers with the masked instance and the success
	// handler replaces every value with it, so a keystroke landing mid-flight was silently thrown away.
	const locked = !configurable || save.isPending;

	// Seeded once per INSTALLED MANIFEST, so a poll or an invalidation never discards what was typed. An Update that
	// lands while this tab is open declares different variables, so the values are reconciled against them the same way
	// the dialogs do it: a name the new manifest does not declare is dropped rather than resent (the node refuses an
	// undeclared variable, which made every save a 400), one it newly declares falls back to the stored value and then
	// to its default, and what the operator typed for a surviving name wins over both.
	useEffect(() => {
		if (fingerprint === seededFingerprint) {
			return;
		}
		const stored = instance.variables ?? {};
		setValues((previous) =>
			seededFingerprint === null
				? initialVariableValues(definitions, stored)
				: initialVariableValues(definitions, { ...stored, ...previous }),
		);
		setSeededFingerprint(fingerprint);
	}, [definitions, instance.variables, fingerprint, seededFingerprint]);

	const submit = (): void => {
		save.mutate(
			{
				path: { instanceId: instance.id ?? "" },
				body: { variables: { ...values }, expectedVersion: instance.version ?? 0 },
			},
			{
				// Reseeded from the MASKED instance the node answers with, which puts every secret box back on the
				// sentinel. Leaving the typed replacement in `values` kept the plaintext on screen and resent it on the
				// next save, so a second save of an untouched form wrote the secret again in clear.
				onSuccess: (saved) => {
					setValues(initialVariableValues(saved.manifest?.variables ?? definitions, saved.variables ?? {}));
					toast.success(t("pages.externalApps.variables.saved"));
				},
				onError: (error) => {
					const conflict = readExternalAppConflict(error);
					const messageKey = conflict ? externalAppConflictMessageKey(conflict.conflictType) : undefined;
					toast.error(messageKey ? t(messageKey) : apiErrorMessage(error, t("pages.externalApps.variables.saveFailed")));
					// The rejected `expectedVersion` is the one this tab rendered. Without the re-read every retry
					// echoes the same stale token and is refused again for as long as the page stays open.
					if (isExternalAppStaleRowConflict(conflict?.conflictType)) {
						invalidateExternalAppInstance(queryClient, instance.id ?? "");
					}
				},
			},
		);
	};

	return (
		<Stack gap="md" data-testid="external-app-detail-settings">
			{configurable ? null : (
				<Text size="sm" c="dimmed" data-testid="external-app-detail-settings-stopped-only">
					{t("pages.externalApps.variables.stoppedOnly")}
				</Text>
			)}
			<VariablesForm
				definitions={definitions}
				values={values}
				issues={issues}
				storedSecrets={storedSecretNames(instance.variables ?? undefined)}
				disabled={locked}
				onChange={(name, value) => setValues((previous) => ({ ...previous, [name]: value }))}
			/>
			<Group justify="flex-end">
				<Button
					disabled={locked || issues.length > 0}
					loading={save.isPending}
					onClick={submit}
					data-testid="external-app-detail-settings-save"
				>
					{t("pages.externalApps.variables.save")}
				</Button>
			</Group>
		</Stack>
	);
}
