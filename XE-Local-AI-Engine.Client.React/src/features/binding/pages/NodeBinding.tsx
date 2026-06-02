import { Alert, Anchor, Button, Card, Container, Group, List, Stack, Table, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconExternalLink, IconInfoCircle } from "@tabler/icons-react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import type { PollNodeBindingResponse, StartNodeBindingResponse } from "@/core/api/generated";
import {
	cancelNodeBindingMutation,
	pollNodeBindingMutation,
	startNodeBindingMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

function statusColor(status: string): "blue" | "green" | "orange" | "red" {
	const normalized = status.toLowerCase();
	if (normalized === "approved") {
		return "green";
	}
	if (["expired", "denied", "consumed", "cancelled"].includes(normalized)) {
		return "orange";
	}
	if (normalized === "failed") {
		return "red";
	}
	return "blue";
}

function errorMessage(error: unknown): string {
	return error instanceof Error ? error.message : "Unexpected binding error";
}

function formatDate(value: string): string {
	const date = new Date(value);
	return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

export function NodeBinding() {
	const queryClient = useQueryClient();
	const pollAbortController = useRef<AbortController | null>(null);
	const [session, setSession] = useState<StartNodeBindingResponse | undefined>();
	const [status, setStatus] = useState("not-started");
	const [message, setMessage] = useState<string | undefined>();
	const [error, setError] = useState<string | undefined>();

	useEffect(() => () => pollAbortController.current?.abort(), []);

	const pollGenerated = useMemo(() => withResponseValidation(pollNodeBindingMutation()), []);
	const startGenerated = useMemo(() => withResponseValidation(startNodeBindingMutation()), []);
	const cancelGenerated = useMemo(() => withResponseValidation(cancelNodeBindingMutation()), []);

	const pollMutation = useMutation({
		// The session is polled with a locally-owned AbortController so cancel/unmount can abort the in-flight
		// request; the controller's signal rides the generated mutation's axios passthrough. The poll body mirrors
		// the started session (the backend echoes the device-flow handshake fields back on each poll).
		mutationFn: async (activeSession: StartNodeBindingResponse): Promise<PollNodeBindingResponse> => {
			const abortController = new AbortController();
			pollAbortController.current = abortController;
			const result = await pollGenerated.mutationFn?.(
				{
					body: {
						deviceCode: activeSession.deviceCode,
						userCode: activeSession.userCode,
						verificationUri: activeSession.verificationUri,
						verificationUriComplete: activeSession.verificationUriComplete,
						expiresAt: activeSession.expiresAt,
						intervalSeconds: activeSession.intervalSeconds,
					},
					signal: abortController.signal,
				},
				undefined as never,
			);
			return result ?? {};
		},
		onSuccess: (result) => {
			const resultStatus = result.status ?? "";
			setStatus(resultStatus);
			setMessage(
				resultStatus.toLowerCase() === "approved"
					? "Binding approved. Worker credentials were stored securely."
					: `Binding ended with status '${resultStatus}'.`,
			);
		},
		onError: (pollError) => {
			if (!pollAbortController.current?.signal.aborted) {
				setStatus("failed");
				setError(errorMessage(pollError));
			}
		},
		onSettled: async () => {
			await queryClient.invalidateQueries();
			pollAbortController.current = null;
		},
	});

	const startMutation = useMutation({
		...startGenerated,
		onSuccess: async (startedSession: StartNodeBindingResponse) => {
			setSession(startedSession);
			setStatus("pending");
			setMessage("Binding started. Approve this worker in the Central Platform.");
			setError(undefined);
			pollMutation.mutate(startedSession);
			await queryClient.invalidateQueries();
		},
		onError: (startError) => setError(errorMessage(startError)),
	});

	const cancelMutation = useMutation({
		...cancelGenerated,
		onSettled: async () => {
			pollAbortController.current?.abort();
			setStatus("cancelled");
			setMessage("Binding polling was cancelled locally.");
			await queryClient.invalidateQueries();
		},
	});

	const handleStart = useCallback(() => {
		setMessage(undefined);
		setError(undefined);
		startMutation.mutate({});
	}, [startMutation]);

	const handleCancel = useCallback(() => {
		cancelMutation.mutate({});
	}, [cancelMutation]);

	const isWorking = startMutation.isPending || pollMutation.isPending;
	const canCancel = session !== undefined && pollMutation.isPending;

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Stack gap={4}>
					<Text size="sm" tt="uppercase" fw={700} c="dimmed">
						Worker Node
					</Text>
					<Title order={2}>Bind this node to your Central Platform account</Title>
					<Text c="dimmed">
						Start binding here, then approve the request in the Central Platform using the displayed user code.
					</Text>
				</Stack>

				{message ? (
					<Alert color={statusColor(status)} icon={<IconInfoCircle size={16} />}>
						{message}
					</Alert>
				) : null}
				{error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{error}
					</Alert>
				) : null}

				<Group align="stretch" grow={true}>
					<Card withBorder={true} radius="md" p="lg">
						<Stack gap="md">
							<Title order={3}>Device binding</Title>
							{session ? (
								<>
									<Table withTableBorder={true} withColumnBorders={true}>
										<Table.Tbody>
											<Table.Tr>
												<Table.Th>Status</Table.Th>
												<Table.Td>{status}</Table.Td>
											</Table.Tr>
											<Table.Tr>
												<Table.Th>User code</Table.Th>
												<Table.Td>
													<Text component="span" fw={800} size="lg">
														{session.userCode}
													</Text>
												</Table.Td>
											</Table.Tr>
											<Table.Tr>
												<Table.Th>Verification URL</Table.Th>
												<Table.Td>
													<Anchor href={session.verificationUriComplete} target="_blank" rel="noreferrer">
														{session.verificationUriComplete}
													</Anchor>
												</Table.Td>
											</Table.Tr>
											<Table.Tr>
												<Table.Th>Expires</Table.Th>
												<Table.Td>{formatDate(session.expiresAt ?? "")}</Table.Td>
											</Table.Tr>
										</Table.Tbody>
									</Table>
									<Group>
										<Button variant="outline" onClick={handleCancel} disabled={!canCancel || cancelMutation.isPending}>
											{cancelMutation.isPending ? "Cancelling..." : "Cancel polling"}
										</Button>
										<Button
											component="a"
											href={session.verificationUriComplete}
											target="_blank"
											rel="noreferrer"
											rightSection={<IconExternalLink size={14} />}
										>
											Open approval link
										</Button>
									</Group>
								</>
							) : (
								<>
									<Text c="dimmed">No binding request is active on this worker.</Text>
									<Button onClick={handleStart} loading={startMutation.isPending} disabled={isWorking}>
										Start binding
									</Button>
								</>
							)}
						</Stack>
					</Card>

					<Card withBorder={true} radius="md" p="lg">
						<Stack gap="md">
							<Title order={3}>How binding works</Title>
							<List icon={<IconCheck size={16} />} spacing="sm">
								<List.Item>Click Start binding to request a one-time user code.</List.Item>
								<List.Item>Open the approval link and sign in to the Central Platform.</List.Item>
								<List.Item>
									The worker polls at the server-provided interval and stores credentials only after approval.
								</List.Item>
							</List>
						</Stack>
					</Card>
				</Group>
			</Stack>
		</Container>
	);
}
