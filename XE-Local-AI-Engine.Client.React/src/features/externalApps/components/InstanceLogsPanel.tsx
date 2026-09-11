import { Alert, Button, Group, ScrollArea, SegmentedControl, Select, Skeleton, Stack, Text } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import type { ExternalAppInstanceView } from "@/features/externalApps/models/ExternalAppModels";
import { useExternalAppInstanceLogs } from "@/features/externalApps/queries/useExternalApps";

interface InstanceLogsPanelProps {
	readonly instance: ExternalAppInstanceView;
	readonly "data-testid"?: string;
}

const keyPrefix = "pages.externalApps.logs";
/** The server's own cap is the largest choice: a tail above it is a 400, never a clamp. */
const tailChoices = ["100", "500", "2000"] as const;

/**
 * Bounded log text, read on demand. Not a stream: the hub carries no log lines, so there is nothing to subscribe to
 * and nothing to poll for.
 *
 * `truncated` is the server's, and it is rendered: a tail cut at the bound otherwise reads as the complete log. The
 * text is unmasked container output by design and is not presented as scrubbed.
 */
export function InstanceLogsPanel({ instance, "data-testid": testId }: InstanceLogsPanelProps) {
	const { t } = useTranslation();
	const services = (instance.manifest?.services ?? []).map((service) => service.name ?? "").filter(Boolean);
	const [service, setService] = useState<string | undefined>(services[0]);
	const [tail, setTail] = useState<string>("500");

	const logsQuery = useExternalAppInstanceLogs(instance.id ?? undefined, service, Number(tail));
	const logs = logsQuery.data;
	const text = logs?.text ?? "";
	// The node answers 503 while the runtime is down. Without this the empty state stood in for the failure and told
	// the operator the container had printed nothing, and a failed REFRESH left the previous tail on screen in silence.
	const failure = logsQuery.isError ? apiErrorMessage(logsQuery.error, t(`${keyPrefix}.failed`)) : undefined;

	return (
		<Stack gap="sm" data-testid={testId ?? "external-app-logs"}>
			<Group gap="sm" align="flex-end">
				{services.length > 1 ? (
					<Select
						label={t(`${keyPrefix}.service`)}
						data={services}
						value={service ?? null}
						onChange={(value) => setService(value ?? undefined)}
						allowDeselect={false}
						data-testid="external-app-logs-service"
					/>
				) : null}
				<SegmentedControl
					data={[...tailChoices]}
					value={tail}
					onChange={setTail}
					aria-label={t(`${keyPrefix}.tail`)}
					data-testid="external-app-logs-tail"
				/>
				<Button
					variant="default"
					loading={logsQuery.isFetching}
					onClick={() => logsQuery.refetch().catch(() => undefined)}
					data-testid="external-app-logs-refresh"
				>
					{t(`${keyPrefix}.refresh`)}
				</Button>
			</Group>

			{logs?.truncated === true ? (
				<Text size="sm" c="dimmed" data-testid="external-app-logs-truncated">
					{t(`${keyPrefix}.truncated`, { lines: logs.lineCount ?? 0 })}
				</Text>
			) : null}

			{failure ? (
				<Alert color="red" data-testid="external-app-logs-error">
					{failure}
				</Alert>
			) : null}

			{/* "No output" is a claim about the container, so it is reserved for a read that SUCCEEDED and came back
			    empty — and it waits for the first read rather than asserting it. */}
			{logsQuery.isLoading ? (
				<Skeleton height={360} radius="md" data-testid="external-app-logs-loading" />
			) : text.length > 0 ? (
				<ScrollArea h={360} type="auto">
					<pre style={{ margin: 0, overflowX: "auto" }} data-testid="external-app-logs-text">
						{text}
					</pre>
				</ScrollArea>
			) : failure ? null : (
				<EmptyState message={t(`${keyPrefix}.empty`)} data-testid="external-app-logs-empty" />
			)}
		</Stack>
	);
}
