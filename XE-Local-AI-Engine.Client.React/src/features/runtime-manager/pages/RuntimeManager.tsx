import { Alert, Badge, Button, Card, Container, Group, Loader, ScrollArea, Select, SimpleGrid, Stack, Table, Tabs, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconCpu, IconFileDescription, IconRefresh, IconServer, IconSettings } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import { executeRuntimeContainerAction, getRuntimeManagerStatus, type RuntimeContainerActionName, type RuntimeContainerActionResponseDto, type RuntimeLogLineDto, streamRuntimeLogs } from "@/features/runtime-manager/api/RuntimeManagerApi";
import { formatRuntimeBoolean, formatRuntimeBytes, formatRuntimeLogLine, formatRuntimeText, formatRuntimeTimestamp, getComponentHealthColor, getRuntimeStatusColor, manifestSummary, runtimeContainerActionLabel, sortRuntimeComponents } from "@/features/runtime-manager/models/RuntimeManagerModel";
import { runtimeManagerQueryKeys } from "@/features/runtime-manager/queries/RuntimeManagerQueryKeys";

const runtimeLogTailLines = 200;
const runtimeLogLineLimit = 500;

async function followRuntimeLogs(
  containerName: string,
  abortController: AbortController,
  onLine: (line: RuntimeLogLineDto) => void,
  onError: (error: unknown) => void,
  onComplete: () => void,
): Promise<void> {
  try {
    for await (const line of streamRuntimeLogs({ containerName, tailLines: runtimeLogTailLines, follow: true }, abortController.signal)) {
      onLine(line);
    }
  } catch (error) {
    if (!abortController.signal.aborted) {
      onError(error);
    }
  } finally {
    onComplete();
  }
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : "Runtime manager data could not be loaded.";
}

function Diagnostics({ diagnostics }: { readonly diagnostics: string[] }) {
  if (diagnostics.length === 0) {
    return null;
  }

  return (
    <Group gap="xs" mt="sm">
      {diagnostics.map((diagnostic) => (
        <Badge key={diagnostic} color="yellow" variant="light">
          {diagnostic}
        </Badge>
      ))}
    </Group>
  );
}

export function RuntimeManager() {
  const queryClient = useQueryClient();
  const [actionMessage, setActionMessage] = useState<string | undefined>();
  const [selectedLogContainer, setSelectedLogContainer] = useState<string | null>(null);
  const [runtimeLogLines, setRuntimeLogLines] = useState<RuntimeLogLineDto[]>([]);
  const [runtimeLogError, setRuntimeLogError] = useState<string | undefined>();
  const [isFollowingLogs, setIsFollowingLogs] = useState(false);
  const logAbortControllerRef = useRef<AbortController | undefined>(undefined);
  const statusQuery = useQuery({
    queryKey: runtimeManagerQueryKeys.status(),
    queryFn: ({ signal }) => getRuntimeManagerStatus({ signal }),
  });
  const snapshot = statusQuery.data;
  const sortedComponents = useMemo(() => sortRuntimeComponents(snapshot?.components ?? []), [snapshot]);
  const logContainerOptions = useMemo(() => {
    const names = new Set<string>();
    for (const component of sortedComponents) {
      names.add(component.name);
    }
    for (const container of snapshot?.manifest.containers ?? []) {
      names.add(container.name);
    }

    return [...names].sort((left, right) => left.localeCompare(right)).map((name) => ({ value: name, label: name }));
  }, [snapshot, sortedComponents]);
  const refreshStatus = useCallback(async (_response: RuntimeContainerActionResponseDto) => {
    await queryClient.invalidateQueries({ queryKey: runtimeManagerQueryKeys.status() });
  }, [queryClient]);
  const actionMutation = useMutation({
    mutationFn: (request: { containerName: string; action: RuntimeContainerActionName }) => executeRuntimeContainerAction({ ...request, drainTimeoutSeconds: snapshot?.manifest.stopDrainTimeoutSeconds ?? undefined }),
    onSuccess: async (response) => {
      setActionMessage(`${response.action} requested for ${response.containerName}.`);
      await refreshStatus(response);
    },
  });
  const runContainerAction = useCallback(
    (containerName: string, action: RuntimeContainerActionName) => {
      setActionMessage(undefined);
      actionMutation.mutate({ containerName, action });
    },
    [actionMutation],
  );
  const stopLogFollow = useCallback(() => {
    logAbortControllerRef.current?.abort();
    logAbortControllerRef.current = undefined;
    setIsFollowingLogs(false);
  }, []);
  const startLogFollow = useCallback(() => {
    if (!selectedLogContainer) {
      return;
    }

    stopLogFollow();
    const abortController = new AbortController();
    logAbortControllerRef.current = abortController;
    setRuntimeLogLines([]);
    setRuntimeLogError(undefined);
    setIsFollowingLogs(true);

    followRuntimeLogs(
      selectedLogContainer,
      abortController,
      (line) => setRuntimeLogLines((current) => [...current.slice(-(runtimeLogLineLimit - 1)), line]),
      (error) => setRuntimeLogError(errorMessage(error)),
      () => {
        if (logAbortControllerRef.current === abortController) {
          logAbortControllerRef.current = undefined;
          setIsFollowingLogs(false);
        }
      },
    );
  }, [selectedLogContainer, stopLogFollow]);

  useEffect(() => {
    const firstContainer = logContainerOptions[0];
    if (!selectedLogContainer && firstContainer) {
      setSelectedLogContainer(firstContainer.value);
    }
  }, [logContainerOptions, selectedLogContainer]);

  useEffect(() => stopLogFollow, [stopLogFollow]);

  return (
    <Container size="xl" py="lg">
      <Stack gap="lg">
        <Group justify="space-between" align="flex-start">
          <Stack gap={4}>
            <Text size="sm" tt="uppercase" fw={700} c="dimmed">
              Host runtime
            </Text>
            <Title order={2}>Runtime manager</Title>
            <Text c="dimmed">Inspect HostAgent status, capabilities, container health, and sanitized runtime manifest data.</Text>
          </Stack>
          <Group gap="sm">
            <Badge color={getRuntimeStatusColor(snapshot?.status.state)}>{snapshot?.status.state ?? "Loading"}</Badge>
            <Button variant="subtle" leftSection={<IconRefresh size={16} />} onClick={() => statusQuery.refetch()} disabled={statusQuery.isFetching}>
              Refresh
            </Button>
          </Group>
        </Group>

        {statusQuery.isLoading ? (
          <Group gap="sm">
            <Loader size="sm" />
            <Text c="dimmed">Loading runtime manager data...</Text>
          </Group>
        ) : null}

        {statusQuery.error ? (
          <Alert color="red" icon={<IconAlertTriangle size={16} />}>
            {errorMessage(statusQuery.error)}
          </Alert>
        ) : null}

        {actionMutation.error ? (
          <Alert color="red" icon={<IconAlertTriangle size={16} />}>
            {errorMessage(actionMutation.error)}
          </Alert>
        ) : null}

        {actionMessage ? <Alert color="green">{actionMessage}</Alert> : null}

        {snapshot ? (
          <Tabs defaultValue="status" keepMounted={false}>
            <Tabs.List>
              <Tabs.Tab value="status" leftSection={<IconServer size={16} />}>Status</Tabs.Tab>
              <Tabs.Tab value="components" leftSection={<IconCpu size={16} />}>Components</Tabs.Tab>
              <Tabs.Tab value="manifest" leftSection={<IconFileDescription size={16} />}>Manifest</Tabs.Tab>
              <Tabs.Tab value="logs" leftSection={<IconFileDescription size={16} />}>Logs</Tabs.Tab>
            </Tabs.List>

            <Tabs.Panel value="status" pt="lg">
              <SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
                <Card withBorder={true} radius="md" p="lg">
                  <Stack gap="md">
                    <Group justify="space-between">
                      <Title order={3}>Substrate status</Title>
                      <IconServer size={22} />
                    </Group>
                    <Table verticalSpacing="sm">
                      <Table.Tbody>
                        <Table.Tr><Table.Td>State</Table.Td><Table.Td>{snapshot.status.state}</Table.Td></Table.Tr>
                        <Table.Tr><Table.Td>Desired state</Table.Td><Table.Td>{snapshot.status.desiredState}</Table.Td></Table.Tr>
                        <Table.Tr><Table.Td>Runtime lifecycle</Table.Td><Table.Td>{snapshot.status.runtimeLifecycle}</Table.Td></Table.Tr>
                        <Table.Tr><Table.Td>Bootstrap model ready</Table.Td><Table.Td>{formatRuntimeBoolean(snapshot.status.bootstrapModelReady)}</Table.Td></Table.Tr>
                        <Table.Tr><Table.Td>Web UI</Table.Td><Table.Td>{formatRuntimeText(snapshot.status.webUiUrl)}</Table.Td></Table.Tr>
                        <Table.Tr><Table.Td>Observed</Table.Td><Table.Td>{formatRuntimeTimestamp(snapshot.status.observedAt)}</Table.Td></Table.Tr>
                      </Table.Tbody>
                    </Table>
                    <Diagnostics diagnostics={snapshot.status.diagnostics} />
                  </Stack>
                </Card>

                <Card withBorder={true} radius="md" p="lg">
                  <Stack gap="md">
                    <Group justify="space-between">
                      <Title order={3}>Capabilities</Title>
                      <IconSettings size={22} />
                    </Group>
                    <Table verticalSpacing="sm">
                      <Table.Tbody>
                        <Table.Tr><Table.Td>CPU available</Table.Td><Table.Td>{formatRuntimeBoolean(snapshot.capabilities.cpuAvailable)}</Table.Td></Table.Tr>
                        <Table.Tr><Table.Td>NVIDIA GPU inference</Table.Td><Table.Td>{formatRuntimeBoolean(snapshot.capabilities.nvidiaGpuInference)}</Table.Td></Table.Tr>
                        <Table.Tr><Table.Td>GPU runtime configured</Table.Td><Table.Td>{formatRuntimeBoolean(snapshot.capabilities.gpuRuntimeConfigured)}</Table.Td></Table.Tr>
                        <Table.Tr><Table.Td>AMD GPU status</Table.Td><Table.Td>{formatRuntimeText(snapshot.capabilities.amdGpuStatus)}</Table.Td></Table.Tr>
                        <Table.Tr><Table.Td>Runtime disk</Table.Td><Table.Td>{formatRuntimeBytes(snapshot.capabilities.runtimeDiskBytes)}</Table.Td></Table.Tr>
                        <Table.Tr><Table.Td>Observed</Table.Td><Table.Td>{formatRuntimeTimestamp(snapshot.capabilities.observedAt)}</Table.Td></Table.Tr>
                      </Table.Tbody>
                    </Table>
                    <Diagnostics diagnostics={snapshot.capabilities.diagnostics} />
                  </Stack>
                </Card>
              </SimpleGrid>
            </Tabs.Panel>

            <Tabs.Panel value="components" pt="lg">
              <Card withBorder={true} radius="md" p="lg">
                <Stack gap="md">
                  <Title order={3}>Runtime components</Title>
                  <Table.ScrollContainer minWidth={760}>
                    <Table striped={true} highlightOnHover={true} verticalSpacing="sm">
                      <Table.Thead>
                        <Table.Tr>
                          <Table.Th>Name</Table.Th>
                          <Table.Th>Desired</Table.Th>
                          <Table.Th>Health</Table.Th>
                          <Table.Th>Image</Table.Th>
                          <Table.Th>Digest</Table.Th>
                          <Table.Th>Observed</Table.Th>
                          <Table.Th>Actions</Table.Th>
                        </Table.Tr>
                      </Table.Thead>
                      <Table.Tbody>
                        {sortedComponents.map((component) => (
                          <Table.Tr key={component.name}>
                            <Table.Td>{component.name}</Table.Td>
                            <Table.Td>{component.desiredState}</Table.Td>
                            <Table.Td><Badge color={getComponentHealthColor(component.health)}>{component.health}</Badge></Table.Td>
                            <Table.Td><Text size="sm" style={{ wordBreak: "break-all" }}>{component.imageReference}</Text></Table.Td>
                            <Table.Td>{formatRuntimeBoolean(component.digestVerified)}</Table.Td>
                            <Table.Td>{formatRuntimeTimestamp(component.observedAt)}</Table.Td>
                            <Table.Td>
                              <Group gap="xs">
                                {(["start", "stop", "restart"] as const).map((action) => (
                                  <Button
                                    key={action}
                                    size="xs"
                                    variant={action === "restart" ? "filled" : "light"}
                                    onClick={() => runContainerAction(component.name, action)}
                                    loading={actionMutation.isPending && actionMutation.variables?.containerName === component.name && actionMutation.variables?.action === action}
                                    disabled={actionMutation.isPending}
                                  >
                                    {runtimeContainerActionLabel(action)}
                                  </Button>
                                ))}
                              </Group>
                            </Table.Td>
                          </Table.Tr>
                        ))}
                      </Table.Tbody>
                    </Table>
                  </Table.ScrollContainer>
                  {sortedComponents.length === 0 ? <Text c="dimmed">No runtime containers reported.</Text> : null}
                </Stack>
              </Card>
            </Tabs.Panel>

            <Tabs.Panel value="manifest" pt="lg">
              <Stack gap="lg">
                <Alert color={snapshot.manifest.available ? "blue" : "yellow"} icon={snapshot.manifest.available ? undefined : <IconAlertTriangle size={16} />}>
                  {manifestSummary(snapshot.manifest)}
                  {snapshot.manifest.diagnostics.length > 0 ? ` · ${snapshot.manifest.diagnostics.join(", ")}` : ""}
                </Alert>
                {snapshot.manifest.available ? (
                  <Card withBorder={true} radius="md" p="lg">
                    <Stack gap="md">
                      <Title order={3}>Runtime manifest</Title>
                      <Table verticalSpacing="sm">
                        <Table.Tbody>
                          <Table.Tr><Table.Td>Schema version</Table.Td><Table.Td>{snapshot.manifest.schemaVersion ?? "Unknown"}</Table.Td></Table.Tr>
                          <Table.Tr><Table.Td>Runtime mode</Table.Td><Table.Td>{snapshot.manifest.runtimeMode}</Table.Td></Table.Tr>
                          <Table.Tr><Table.Td>Bootstrap model</Table.Td><Table.Td>{snapshot.manifest.bootstrapModel}</Table.Td></Table.Tr>
                          <Table.Tr><Table.Td>Default chat model</Table.Td><Table.Td>{snapshot.manifest.defaultChatModel}</Table.Td></Table.Tr>
                          <Table.Tr><Table.Td>Runtime disk limit</Table.Td><Table.Td>{snapshot.manifest.maxRuntimeDiskGb ?? "Unknown"} GB</Table.Td></Table.Tr>
                          <Table.Tr><Table.Td>Stop drain timeout</Table.Td><Table.Td>{snapshot.manifest.stopDrainTimeoutSeconds ?? "Unknown"} seconds</Table.Td></Table.Tr>
                        </Table.Tbody>
                      </Table>
                    </Stack>
                  </Card>
                ) : null}
                {snapshot.manifest.containers.map((container) => (
                  <Card key={container.name} withBorder={true} radius="md" p="lg">
                    <Stack gap="md">
                      <Title order={4}>{container.name}</Title>
                      <Text size="sm" style={{ wordBreak: "break-all" }}>Image: {container.image}</Text>
                      <Text size="sm">Network: {container.network}</Text>
                      <SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
                        <Stack gap="xs">
                          <Text fw={700}>Environment</Text>
                          {container.environment.map((entry) => <Text key={entry.name} size="sm">{entry.name}: {entry.value}</Text>)}
                          {container.environment.length === 0 ? <Text size="sm" c="dimmed">No environment entries.</Text> : null}
                        </Stack>
                        <Stack gap="xs">
                          <Text fw={700}>Volumes</Text>
                          {container.volumes.map((volume) => <Text key={`${volume.source}:${volume.target}`} size="sm">{volume.source} → {volume.target} ({volume.readOnly ? "read-only" : "read-write"})</Text>)}
                          {container.volumes.length === 0 ? <Text size="sm" c="dimmed">No volumes.</Text> : null}
                        </Stack>
                      </SimpleGrid>
                    </Stack>
                  </Card>
                ))}
              </Stack>
            </Tabs.Panel>

            <Tabs.Panel value="logs" pt="lg">
              <Card withBorder={true} radius="md" p="lg">
                <Stack gap="md">
                  <Group justify="space-between" align="flex-end">
                    <Stack gap={4}>
                      <Title order={3}>Runtime logs</Title>
                      <Text c="dimmed">Tail and follow HostAgent container logs. The stream is cancelled when you stop following or leave the page.</Text>
                    </Stack>
                    <Group gap="sm" align="flex-end">
                      <Select
                        label="Container"
                        value={selectedLogContainer}
                        onChange={(value) => {
                          stopLogFollow();
                          setSelectedLogContainer(value);
                        }}
                        data={logContainerOptions}
                        disabled={isFollowingLogs || logContainerOptions.length === 0}
                      />
                      {isFollowingLogs ? (
                        <Button variant="light" color="red" onClick={stopLogFollow}>Stop</Button>
                      ) : (
                        <Button onClick={startLogFollow} disabled={!selectedLogContainer}>Follow logs</Button>
                      )}
                    </Group>
                  </Group>

                  {runtimeLogError ? <Alert color="red" icon={<IconAlertTriangle size={16} />}>{runtimeLogError}</Alert> : null}
                  {logContainerOptions.length === 0 ? <Text c="dimmed">No runtime containers are available for logs.</Text> : null}

                  <ScrollArea h={360} type="auto">
                    <Stack gap={4} p="xs" bg="dark.8" style={{ borderRadius: "var(--mantine-radius-sm)" }}>
                      {runtimeLogLines.map((line) => (
                        <Text key={`${line.observedAt}:${line.containerName}:${line.stream}:${line.line}`} component="pre" c="gray.1" size="sm" m={0} style={{ whiteSpace: "pre-wrap", wordBreak: "break-word" }}>
                          {formatRuntimeLogLine(line)}
                        </Text>
                      ))}
                      {runtimeLogLines.length === 0 ? <Text c="gray.5">No log lines loaded yet.</Text> : null}
                    </Stack>
                  </ScrollArea>
                </Stack>
              </Card>
            </Tabs.Panel>
          </Tabs>
        ) : null}
      </Stack>
    </Container>
  );
}
