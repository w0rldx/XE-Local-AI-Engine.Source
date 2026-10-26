import { Alert, Button, Group, Loader, Text } from "@mantine/core";
import { IconAlertTriangle, IconCloud, IconX } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { useCallback, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import {
	codexStatusOptions,
	getCloudSettingsOptions,
	listExternalProviderConnectionsOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { AzureFoundryPanel } from "@/features/cloud-providers/azure/components/AzureFoundryPanel";
import { errorMessage as cloudSettingsErrorMessage } from "@/features/cloud-providers/azure/models/CloudSettingsFormState";
import { hasStoredAzureConnection } from "@/features/cloud-providers/azure/models/CloudSettingsModel";
import { CodexSignInCard } from "@/features/cloud-providers/codex/components/CodexSignInCard";
import { AddProviderMenu } from "@/features/cloud-providers/components/AddProviderMenu";
import { CloudProviderList } from "@/features/cloud-providers/components/CloudProviderList";
import { ExternalProviderPanel } from "@/features/cloud-providers/external/components/ExternalProviderPanel";
import {
	connectionToFormValues,
	errorMessage as externalErrorMessage,
} from "@/features/cloud-providers/external/models/ExternalProviderFormState";
import type { EditorTarget, ExternalProviderFormValues } from "@/features/cloud-providers/external/models/ExternalProviderModel";
import {
	type ExternalProviderTemplateId,
	externalTemplateValues,
	isExternalProviderTemplate,
	type ProviderTemplateId,
	providerTemplateOptions,
} from "@/features/cloud-providers/models/ProviderTemplates";

type ActiveEditor =
	| {
			readonly kind: "external";
			// Remounts the panel per opening, so each one starts from its own form.
			readonly opening: number;
			readonly target: EditorTarget;
			readonly values: ExternalProviderFormValues;
			readonly template?: ExternalProviderTemplateId;
	  }
	| { readonly kind: "azureFoundry" }
	| { readonly kind: "codex" };

// One page for every cloud or remote provider. Each kind keeps its own store and editor; this page lists them
// together and starts new ones from templates.
export function CloudProviders() {
	const { t } = useTranslation();
	const connectionsQuery = useQuery({
		...listExternalProviderConnectionsOptions(),
		enabled: nodeCapabilities.externalProviders,
	});
	const cloudSettingsQuery = useQuery({ ...getCloudSettingsOptions(), enabled: nodeCapabilities.cloudSettings });
	const codexQuery = useQuery({ ...codexStatusOptions(), enabled: nodeCapabilities.cloudSettings });

	const [active, setActive] = useState<ActiveEditor | null>(null);
	const [conflictNotice, setConflictNotice] = useState<string | null>(null);
	const openings = useRef(0);

	const connections = useMemo(() => connectionsQuery.data?.connections ?? [], [connectionsQuery.data]);
	const azure = cloudSettingsQuery.data?.azureFoundry;
	const azureConfigured = hasStoredAzureConnection(azure);
	const codexSignedIn = codexQuery.data?.signedIn === true;
	const templateOptions = providerTemplateOptions(nodeCapabilities, { azureFoundryConfigured: azureConfigured, codexSignedIn });

	const openExternal = (target: EditorTarget, values: ExternalProviderFormValues, template?: ExternalProviderTemplateId) => {
		openings.current += 1;
		setConflictNotice(null);
		setActive({ kind: "external", opening: openings.current, target, values, template });
	};

	const openTemplate = (id: ProviderTemplateId) => {
		if (isExternalProviderTemplate(id)) {
			openExternal({ connectionId: "", isNew: true }, externalTemplateValues(id), id);
			return;
		}
		setConflictNotice(null);
		setActive({ kind: id });
	};

	const close = useCallback(() => setActive(null), []);

	// A save or delete finishes after its panel may already have been replaced by another opening; it closes only the
	// opening it was started from, so it can never discard a connection the operator opened meanwhile.
	const closeOpening = useCallback(
		(opening: number) => setActive((current) => (current?.kind === "external" && current.opening === opening ? null : current)),
		[],
	);

	const isLoading = connectionsQuery.isLoading || cloudSettingsQuery.isLoading || codexQuery.isLoading;

	return (
		<PageShell>
			<PageHeader
				title={t("pages.cloudProviders.title", "Cloud providers")}
				icon={<IconCloud size={24} />}
				subtitle={t("pages.cloudProviders.subtitle")}
			/>

			{isLoading ? (
				<Group gap="sm">
					<Loader size="sm" />
					<Text c="dimmed">{t("pages.cloudProviders.loading", "Loading providers…")}</Text>
				</Group>
			) : null}

			{connectionsQuery.error ? <InlineErrorAlert message={externalErrorMessage(connectionsQuery.error)} /> : null}
			{cloudSettingsQuery.error ? <InlineErrorAlert message={cloudSettingsErrorMessage(cloudSettingsQuery.error)} /> : null}

			{/* Shown whenever a Codex session is active, wherever the operator is on this page. */}
			{codexSignedIn ? (
				<Alert color="orange" icon={<IconAlertTriangle size={16} />}>
					<Text size="sm" fw={500}>
						{t("pages.cloudSettings.provider.activeEgressBanner")}
					</Text>
				</Alert>
			) : null}

			{conflictNotice !== null ? (
				<Alert color="orange" icon={<IconAlertTriangle size={16} />} data-testid="external-provider-conflict">
					<Text size="sm">{conflictNotice}</Text>
				</Alert>
			) : null}

			<CloudProviderList
				connections={connections}
				azureFoundry={azureConfigured ? { endpoint: azure?.endpoint ?? null, modelCount: azure?.models?.length ?? 0 } : null}
				codex={codexSignedIn ? { accountId: codexQuery.data?.accountId ?? null } : null}
				addAction={<AddProviderMenu options={templateOptions} onPick={openTemplate} />}
				onEditExternal={(connectionId) => {
					const connection = connections.find((entry) => entry.id === connectionId);
					if (connection) {
						openExternal({ connectionId, isNew: false }, connectionToFormValues(connection));
					}
				}}
				onEditAzureFoundry={() => openTemplate("azureFoundry")}
				onEditCodex={() => openTemplate("codex")}
			/>

			{active?.kind === "external" ? (
				<ExternalProviderPanel
					key={active.opening}
					target={active.target}
					initialValues={active.values}
					template={active.template}
					onConflict={setConflictNotice}
					onClose={() => closeOpening(active.opening)}
				/>
			) : null}

			{active?.kind === "azureFoundry" || active?.kind === "codex" ? (
				<>
					{active.kind === "azureFoundry" ? <AzureFoundryPanel /> : <CodexSignInCard />}
					<Group>
						<Button variant="subtle" leftSection={<IconX size={16} />} data-testid="cloud-provider-close" onClick={close}>
							{t("pages.cloudProviders.close", "Close")}
						</Button>
					</Group>
				</>
			) : null}
		</PageShell>
	);
}
