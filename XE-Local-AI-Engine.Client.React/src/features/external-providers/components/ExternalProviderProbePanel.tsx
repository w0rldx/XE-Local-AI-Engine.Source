import { Alert, Badge, Button, Group, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconPlugConnected, IconPlus } from "@tabler/icons-react";
import { useMutation } from "@tanstack/react-query";
import type { Dispatch } from "react";
import { useTranslation } from "react-i18next";

import { probeExternalProviderMutation } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import {
	errorMessage,
	type ExternalProviderFormAction,
	type ExternalProviderProbeState,
	nextExternalRowId,
	probeInputFingerprint,
} from "@/features/external-providers/models/ExternalProviderFormState";
import type { ExternalProviderFormValues } from "@/features/external-providers/models/ExternalProviderModel";

interface ExternalProviderProbePanelProps {
	readonly values: ExternalProviderFormValues;
	// False while creating: there is no stored connection whose unseen key the backend could fall back to.
	readonly isStored: boolean;
	readonly hasStoredApiKey: boolean;
	// The last probe outcome, already fingerprint-checked by the reducer — non-null only while it still describes the
	// configuration on screen.
	readonly probe: ExternalProviderProbeState | null;
	readonly dispatch: Dispatch<ExternalProviderFormAction>;
}

export function ExternalProviderProbePanel({
	values,
	isStored,
	hasStoredApiKey,
	probe,
	dispatch,
}: ExternalProviderProbePanelProps) {
	const { t } = useTranslation();
	const probeMutation = useMutation(withResponseValidation(probeExternalProviderMutation()));

	const typedKey = values.apiKey.trim();
	const baseUrl = values.baseUrl.trim();

	const runProbe = (): void => {
		// The fingerprint of what is being probed, taken BEFORE the request goes out, so a reply that lands after the
		// operator has edited the form is recognized as describing a different endpoint.
		const fingerprint = probeInputFingerprint(values);
		probeMutation.mutate(
			{
				body: {
					// The connection id rides ALONGSIDE the draft address, never instead of it: the backend falls back to
					// the stored key only when the draft address is on the stored connection's own origin, which is
					// exactly the case where the key the editor cannot see is the right credential. Sending only the id
					// would probe the OLD endpoint and hide an edited address. A pending key removal drops the id — that
					// draft is deliberately keyless.
					...(isStored && hasStoredApiKey && !values.clearApiKey ? { connectionId: values.connectionId.trim() } : {}),
					baseUrl,
					...(typedKey.length > 0 ? { apiKey: values.apiKey } : {}),
				},
			},
			{
				onSuccess: (result) => dispatch({ type: "probeSucceeded", fingerprint, result }),
				onError: (error) => dispatch({ type: "probeFailed", fingerprint, failure: errorMessage(error) }),
			},
		);
	};

	const result = probe?.result ?? null;
	const failure = probe?.failure ?? null;
	const probedModels = result?.models ?? [];
	const canProbe = baseUrl.length > 0;

	return (
		<Stack gap={6}>
			<Group>
				<Button
					variant="light"
					leftSection={<IconPlugConnected size={16} />}
					data-testid="external-provider-probe"
					disabled={!canProbe}
					loading={probeMutation.isPending}
					onClick={runProbe}
				>
					{t("pages.externalProviders.probe.test", "Test connection")}
				</Button>
			</Group>

			{failure !== null ? (
				<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="external-provider-probe-failure">
					<Text size="sm">{failure}</Text>
				</Alert>
			) : null}

			{result !== null ? (
				<Stack gap={6} data-testid="external-provider-probe-result">
					<Group gap="xs">
						<Badge color={result.reachable ? "green" : "red"} variant="light">
							{result.reachable
								? t("pages.externalProviders.probe.reachable", "Reachable")
								: t("pages.externalProviders.probe.unreachable", "Not reachable")}
						</Badge>
						{result.error ? (
							<Text size="sm" c="dimmed" data-testid="external-provider-probe-error">
								{result.error}
							</Text>
						) : null}
					</Group>

					{/* A reachable endpoint that lists nothing is a normal outcome, not a failure: plenty of gateways
					    answer chat completions without serving /v1/models at all. Say so, and leave the manual rows as
					    the way in. */}
					{result.reachable && probedModels.length === 0 ? (
						<Text size="sm" c="dimmed" data-testid="external-provider-probe-no-models">
							{t("pages.externalProviders.probe.noModels")}
						</Text>
					) : null}

					{probedModels.length > 0 ? (
						<Stack gap={4}>
							<Text size="xs" c="dimmed">
								{t("pages.externalProviders.probe.pickHint")}
							</Text>
							<Group gap="xs" wrap="wrap">
								{probedModels.map((model) => {
									// Exact, matching the store's Ordinal identity: "Foo" and "foo" are two registrable ids.
									const alreadyAdded = values.models.some((row) => row.wireId.trim() === model.id.trim());
									return (
										<Button
											key={model.id}
											size="xs"
											variant="default"
											disabled={alreadyAdded}
											leftSection={<IconPlus size={12} />}
											data-testid={`external-provider-probe-add-${model.id}`}
											onClick={() =>
												dispatch({
													type: "addProbedModel",
													wireId: model.id,
													contextLength: model.contextLength,
													rowId: nextExternalRowId("external-model"),
												})
											}
										>
											{alreadyAdded ? (
												<Group gap={4} wrap="nowrap">
													<IconCheck size={12} />
													<span>{model.id}</span>
												</Group>
											) : (
												model.id
											)}
										</Button>
									);
								})}
							</Group>
						</Stack>
					) : null}
				</Stack>
			) : null}
		</Stack>
	);
}
