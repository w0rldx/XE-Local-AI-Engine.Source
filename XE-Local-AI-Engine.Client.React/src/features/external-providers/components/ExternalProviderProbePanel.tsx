import { Alert, Badge, Button, Group, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconPlugConnected, IconPlus } from "@tabler/icons-react";
import { useMutation } from "@tanstack/react-query";
import type { Dispatch } from "react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientEndpointsExternalProvidersV1ExternalProviderProbeResponse } from "@/core/api/generated";
import { probeExternalProviderMutation } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import {
	errorMessage,
	type ExternalProviderFormAction,
	nextExternalRowId,
} from "@/features/external-providers/models/ExternalProviderFormState";
import type { ExternalProviderFormValues } from "@/features/external-providers/models/ExternalProviderModel";

type ProbeResponse = XeLocalAiEngineClientEndpointsExternalProvidersV1ExternalProviderProbeResponse;

interface ExternalProviderProbePanelProps {
	readonly values: ExternalProviderFormValues;
	// False while creating: there is nothing stored to probe by id yet, so the draft address must carry the request.
	readonly isStored: boolean;
	readonly hasStoredApiKey: boolean;
	readonly dispatch: Dispatch<ExternalProviderFormAction>;
}

export function ExternalProviderProbePanel({ values, isStored, hasStoredApiKey, dispatch }: ExternalProviderProbePanelProps) {
	const { t } = useTranslation();
	const [result, setResult] = useState<ProbeResponse | null>(null);
	const [failure, setFailure] = useState<string | null>(null);

	const probeMutation = useMutation({
		...withResponseValidation(probeExternalProviderMutation()),
		onSuccess: (response: ProbeResponse) => {
			setFailure(null);
			setResult(response);
		},
		onError: (error) => {
			setResult(null);
			setFailure(errorMessage(error));
		},
	});

	const typedKey = values.apiKey.trim();
	const baseUrl = values.baseUrl.trim();
	// The probe has to reach the endpoint the operator is LOOKING at, so the draft address and a freshly typed key
	// always win. Naming the stored connection is the only way to probe with a key the editor cannot see — so it is
	// the fallback, used exactly when the connection is stored and the key field was left untouched.
	const probeByStoredConnection = isStored && typedKey.length === 0 && hasStoredApiKey && !values.clearApiKey;

	const runProbe = (): void => {
		probeMutation.mutate({
			body: probeByStoredConnection
				? { connectionId: values.connectionId }
				: { baseUrl, ...(typedKey.length > 0 ? { apiKey: values.apiKey } : {}) },
		});
	};

	const probedModels = result?.models ?? [];
	const canProbe = probeByStoredConnection || baseUrl.length > 0;

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
									const alreadyAdded = values.models.some(
										(row) => row.wireId.trim().toLowerCase() === model.id.trim().toLowerCase(),
									);
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
