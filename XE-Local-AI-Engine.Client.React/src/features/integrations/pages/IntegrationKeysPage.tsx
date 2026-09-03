import { Alert, Button, Group, Loader, Text } from "@mantine/core";
import { IconAlertTriangle, IconKey, IconPlus } from "@tabler/icons-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { IntegrationKeyGenerateDialog } from "@/features/integrations/components/IntegrationKeyGenerateDialog";
import { IntegrationKeyList } from "@/features/integrations/components/IntegrationKeyList";
import { IntegrationKeyRevealPanel } from "@/features/integrations/components/IntegrationKeyRevealPanel";
import { toGenerateIntegrationApiKeyRequest } from "@/features/integrations/models/IntegrationMappers";
import type { IntegrationApiKey, IntegrationKeyFormValues } from "@/features/integrations/models/IntegrationModels";
import {
	useGenerateIntegrationApiKey,
	useIntegrationKeys,
	useRevokeIntegrationApiKey,
} from "@/features/integrations/queries/useIntegrationKeys";
import { useIntegrationTriggers } from "@/features/integrations/queries/useIntegrationTriggers";
import { useIntegrationsUiStore } from "@/features/integrations/stores/IntegrationsUiStore";

// Operator surface for the xeint_ credentials integrators present to the loopback integration API. Unlike the
// local-model proxy this node holds MANY keys, so generating never replaces an existing one and there is no
// regenerate verb — only Generate and Revoke.
export function IntegrationKeysPage() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const keyDialogOpen = useIntegrationsUiStore((state) => state.keyDialogOpen);
	const openKeyDialog = useIntegrationsUiStore((state) => state.actions.openKeyDialog);
	const closeKeyDialog = useIntegrationsUiStore((state) => state.actions.closeKeyDialog);

	// Reset the dialog on unmount so navigating away and back does not reopen it from stale Zustand state.
	useEffect(() => {
		return () => {
			closeKeyDialog();
		};
	}, [closeKeyDialog]);

	// The ONLY place the plaintext key ever lives. It is captured in the mutation's onSuccess callback and held in
	// component state — never a store, never the query cache — so unmounting or navigating away drops it, which is
	// the honest lifetime for a value the node can no longer supply. Deliberately NOT cleared on a query refetch:
	// the list invalidation a successful generate fires lands while the operator is still reading the key.
	const [revealedKey, setRevealedKey] = useState<string | null>(null);

	const keysQuery = useIntegrationKeys();
	const triggersQuery = useIntegrationTriggers();

	const generateMutation = useGenerateIntegrationApiKey();
	const revokeMutation = useRevokeIntegrationApiKey();

	const keys = useMemo(() => keysQuery.data ?? [], [keysQuery.data]);
	const triggers = useMemo(() => triggersQuery.data ?? [], [triggersQuery.data]);

	const handleGenerate = useCallback(
		(values: IntegrationKeyFormValues) => {
			generateMutation.mutate(
				{ body: toGenerateIntegrationApiKeyRequest(values) },
				{
					onSuccess: (response) => {
						setRevealedKey(response.key);
						closeKeyDialog();
					},
				},
			);
		},
		[closeKeyDialog, generateMutation],
	);

	const handleRevoke = useCallback(
		async (key: IntegrationApiKey) => {
			const confirmed = await confirm({
				title: t("pages.integrations.keys.revoke.title", "Revoke API key"),
				description: t(
					"pages.integrations.keys.revoke.description",
					"Revoke '{{label}}'? Any client still using it stops working immediately. This cannot be undone.",
					{ label: key.label },
				),
				confirmationText: t("pages.integrations.keys.revoke.action", "Revoke"),
				cancellationText: t("common.cancel", "Cancel"),
			});

			if (confirmed) {
				revokeMutation.mutate(
					{ path: { keyId: key.id } },
					{
						onError: (error) =>
							toast.error(apiErrorMessage(error, t("pages.integrations.keys.errors.revoke", "Could not revoke the key."))),
					},
				);
			}
		},
		[confirm, revokeMutation, t],
	);

	const loadError = keysQuery.error
		? apiErrorMessage(keysQuery.error, t("pages.integrations.keys.errors.load", "Could not load integration API keys."))
		: undefined;

	const generateError = generateMutation.error
		? apiErrorMessage(generateMutation.error, t("pages.integrations.keys.errors.generate", "Could not generate the key."))
		: undefined;

	return (
		<PageShell>
			<PageHeader
				title={t("pages.integrations.keys.title", "Integration API keys")}
				icon={<IconKey size={24} />}
				subtitle={t(
					"pages.integrations.keys.subtitle",
					"Credentials an integrator presents to the loopback integration API. Only a one-way hash is stored, so a key is shown once when you generate it.",
				)}
				actions={
					<Button leftSection={<IconPlus size={16} />} onClick={openKeyDialog} data-testid="integration-key-generate-button">
						{t("pages.integrations.keys.generateButton", "Generate key")}
					</Button>
				}
			/>

			<IntegrationKeyGenerateDialog
				opened={keyDialogOpen}
				keys={keys}
				triggers={triggers}
				isSubmitting={generateMutation.isPending}
				submitError={generateError}
				onSubmit={handleGenerate}
				onClose={closeKeyDialog}
			/>

			{revealedKey !== null ? (
				<IntegrationKeyRevealPanel apiKey={revealedKey} onDismiss={() => setRevealedKey(null)} />
			) : null}

			<SectionCard data-testid="integration-keys-card">
				{keysQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.integrations.keys.list.loading", "Loading integration API keys…")}</Text>
					</Group>
				) : null}
				{loadError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="integration-keys-error">
						{loadError}
					</Alert>
				) : null}
				{!(keysQuery.isLoading || loadError) ? (
					<IntegrationKeyList keys={keys} triggers={triggers} isMutating={revokeMutation.isPending} onRevoke={handleRevoke} />
				) : null}
			</SectionCard>
		</PageShell>
	);
}
