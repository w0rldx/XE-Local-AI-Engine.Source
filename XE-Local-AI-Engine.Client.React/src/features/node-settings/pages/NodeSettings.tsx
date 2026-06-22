import { Alert, Button, Card, Container, Group, Loader, NumberInput, Stack, Switch, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconCode, IconDeviceFloppy, IconRefresh, IconSettings } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import type { SaveNodeSettingsResponse } from "@/core/api/generated";
import {
	getNodeSettingsOptions,
	getNodeSettingsQueryKey,
	saveNodeSettingsMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { toast } from "@/core/ui/notifications/Toast";
import { HfTokenPanel } from "@/features/node-settings/components/HfTokenPanel";
import { LlamaCppVersionPanel } from "@/features/node-settings/components/LlamaCppVersionPanel";
import type { LlamaCppVariant } from "@/features/node-settings/models/LocalRuntimeModels";
import {
	type NodeSettingsTimeoutInput,
	nodeSettingsDefaults,
	toValidNodeSettingsTimeoutSeconds,
} from "@/features/node-settings/models/NodeSettingsModel";
import {
	useEnsureLlamaCppBinary,
	useHfTokenStatus,
	useLlamaCppVersion,
	useSetHfToken,
} from "@/features/node-settings/queries/useLocalRuntime";
import { useHfTokenStore } from "@/features/node-settings/stores/HfTokenStore";

function errorMessage(error: unknown): string {
	return error instanceof Error ? error.message : "Unexpected node settings error";
}

function runtimeErrorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

export function NodeSettings() {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const settingsQuery = useQuery(withResponseValidation(getNodeSettingsOptions()));
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const { toggle: toggleDeveloperMode } = useDeveloperModeStore((state) => state.actions);
	const settings = settingsQuery.data;
	const [timeoutSeconds, setTimeoutSeconds] = useState<NodeSettingsTimeoutInput>(
		nodeSettingsDefaults.maxMessageRequestTimeoutSeconds,
	);

	useEffect(() => {
		if (settings?.maxMessageRequestTimeoutSeconds !== undefined) {
			setTimeoutSeconds(settings.maxMessageRequestTimeoutSeconds);
		}
	}, [settings]);

	const minTimeout = settings?.minMessageRequestTimeoutSeconds ?? nodeSettingsDefaults.minMessageRequestTimeoutSeconds;
	const maxTimeout =
		settings?.maxAllowedMessageRequestTimeoutSeconds ?? nodeSettingsDefaults.maxAllowedMessageRequestTimeoutSeconds;
	const timeoutToSave = useMemo(
		() => toValidNodeSettingsTimeoutSeconds(timeoutSeconds, minTimeout, maxTimeout),
		[maxTimeout, minTimeout, timeoutSeconds],
	);

	const saveMutation = useMutation({
		...withResponseValidation(saveNodeSettingsMutation()),
		onSuccess: async (updatedSettings: SaveNodeSettingsResponse) => {
			toast.success("Node settings saved. Capability reporting was requested for the worker connection.");
			setTimeoutSeconds(updatedSettings.maxMessageRequestTimeoutSeconds ?? nodeSettingsDefaults.maxMessageRequestTimeoutSeconds);
			queryClient.setQueryData(getNodeSettingsQueryKey(), updatedSettings);
			await queryClient.invalidateQueries({ queryKey: getNodeSettingsQueryKey() });
		},
		onError: (error) => toast.error(errorMessage(error)),
	});

	const canSave = timeoutToSave !== undefined && !saveMutation.isPending;

	// Local-runtime cards (relocated from the model-fit advisor). The llama.cpp version GET may trigger the first
	// prebuilt binary download backend-side, so it must NOT run on mount — `versionChecked` latches it on only when the
	// operator explicitly clicks "Check version". The HF token draft lives in a store so it survives a remount; the
	// token itself is write-only (never read back into the draft).
	const [versionChecked, setVersionChecked] = useState(false);
	const tokenDraft = useHfTokenStore((state) => state.tokenDraft);
	const setTokenDraft = useHfTokenStore((state) => state.actions.setTokenDraft);
	const clearTokenDraft = useHfTokenStore((state) => state.actions.clearTokenDraft);

	const versionQuery = useLlamaCppVersion(versionChecked);
	const hfTokenQuery = useHfTokenStatus();
	const ensureBinary = useEnsureLlamaCppBinary();
	const setHfToken = useSetHfToken();

	// Operator-initiated llama.cpp version probe. Latches the flag so the (possibly download-triggering) GET fires
	// once on demand; a subsequent click re-fetches the now-enabled query.
	const handleCheckVersion = (): void => {
		if (!versionChecked) {
			setVersionChecked(true);
			return;
		}
		versionQuery.refetch().catch(() => undefined);
	};

	const handleEnsureBinary = (variant: LlamaCppVariant): void => {
		ensureBinary.mutate(variant, {
			onSuccess: () => toast.success(t("pages.nodeSettings.llamaCpp.ensured", "llama.cpp binary ready.")),
			onError: (error) =>
				toast.error(
					runtimeErrorMessage(error, t("pages.nodeSettings.llamaCpp.ensureError", "Could not ensure the llama.cpp binary.")),
				),
		});
	};

	const handleSaveToken = (): void => {
		setHfToken.mutate(tokenDraft.trim(), {
			onSuccess: () => {
				clearTokenDraft();
				toast.success(t("pages.nodeSettings.hfToken.saved", "Token saved."));
			},
			onError: (error) =>
				toast.error(runtimeErrorMessage(error, t("pages.nodeSettings.hfToken.saveError", "Could not save the token."))),
		});
	};

	const handleClearToken = (): void => {
		setHfToken.mutate(undefined, {
			onSuccess: () => {
				clearTokenDraft();
				toast.success(t("pages.nodeSettings.hfToken.cleared", "Token cleared."));
			},
			onError: (error) =>
				toast.error(runtimeErrorMessage(error, t("pages.nodeSettings.hfToken.clearError", "Could not clear the token."))),
		});
	};

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Stack gap={4}>
					<Text size="sm" tt="uppercase" fw={700} c="dimmed">
						Worker Node
					</Text>
					<Title order={2}>Node settings</Title>
					<Text c="dimmed">Tune non-secret local runtime settings stored on this worker.</Text>
				</Stack>

				{settingsQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">Loading node settings…</Text>
					</Group>
				) : null}

				{settingsQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{errorMessage(settingsQuery.error)}
					</Alert>
				) : null}

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						<Group justify="space-between" align="center">
							<Title order={3}>Local chat runtime</Title>
							<IconSettings size={22} />
						</Group>
						<Text c="dimmed">
							The maximum message request timeout is included in capability reports so the platform can respect this worker's
							local runtime limit.
						</Text>
						<NumberInput
							label="Maximum message request timeout"
							description={`Allowed range: ${minTimeout}–${maxTimeout} seconds.`}
							suffix=" seconds"
							min={minTimeout}
							max={maxTimeout}
							step={5}
							allowDecimal={false}
							value={timeoutSeconds}
							onChange={setTimeoutSeconds}
							error={timeoutToSave === undefined ? `Enter a whole number from ${minTimeout} to ${maxTimeout}.` : undefined}
						/>
						<Group>
							<Button
								leftSection={<IconDeviceFloppy size={16} />}
								onClick={() =>
									saveMutation.mutate({
										body: {
											maxMessageRequestTimeoutSeconds: timeoutToSave ?? nodeSettingsDefaults.maxMessageRequestTimeoutSeconds,
										},
									})
								}
								loading={saveMutation.isPending}
								disabled={!canSave}
							>
								Save settings
							</Button>
							<Button
								variant="subtle"
								leftSection={<IconRefresh size={16} />}
								onClick={() => settingsQuery.refetch()}
								disabled={settingsQuery.isFetching}
							>
								Reload
							</Button>
						</Group>
					</Stack>
				</Card>

				<LlamaCppVersionPanel
					version={versionQuery.data}
					// `isLoading` is true while a DISABLED query idles, so gate the spinner on an actual in-flight fetch — the
					// panel shows its idle "not checked yet" state until the operator triggers the (download-capable) probe.
					isLoading={versionChecked && versionQuery.isFetching}
					error={versionChecked ? versionQuery.error : null}
					hasChecked={versionChecked}
					onCheck={handleCheckVersion}
					onEnsure={handleEnsureBinary}
					isEnsuring={ensureBinary.isPending}
				/>

				<HfTokenPanel
					hasToken={hfTokenQuery.data ?? false}
					isLoading={hfTokenQuery.isLoading}
					tokenDraft={tokenDraft}
					onTokenDraftChange={setTokenDraft}
					onSave={handleSaveToken}
					onClear={handleClearToken}
					isSaving={setHfToken.isPending}
				/>

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						<Group justify="space-between" align="center">
							<Title order={3}>{t("pages.nodeSettings.developerMode.title", "Developer settings")}</Title>
							<IconCode size={22} />
						</Group>
						<Switch
							label={t("pages.nodeSettings.developerMode.label", "Developer mode")}
							description={t(
								"pages.nodeSettings.developerMode.description",
								"Enables advanced, experimental controls in the app (e.g. chat sampling options). Stored in this browser only.",
							)}
							checked={developerMode}
							onChange={() => toggleDeveloperMode()}
							data-testid="developer-mode-switch"
						/>
					</Stack>
				</Card>
			</Stack>
		</Container>
	);
}
