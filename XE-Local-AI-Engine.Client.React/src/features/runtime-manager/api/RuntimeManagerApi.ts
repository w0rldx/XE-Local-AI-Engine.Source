import type { AxiosRequestConfig } from "axios";
import { HubConnectionBuilder, HttpTransportType, LogLevel } from "@microsoft/signalr";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

export interface RuntimeComponentStatusDto {
  name: string;
  desiredState: string;
  health: string;
  imageReference: string;
  digestVerified: boolean;
  observedAt: string;
  diagnostics: string[];
}

export interface HostAgentStatusDto {
  state: string;
  desiredState: string;
  runtimeLifecycle: string;
  bootstrapModelReady: boolean;
  webUiUrl: string;
  observedAt: string;
  components: RuntimeComponentStatusDto[];
  diagnostics: string[];
}

export interface HostCapabilitiesDto {
  cpuAvailable: boolean;
  nvidiaGpuInference: boolean;
  gpuRuntimeConfigured: boolean;
  amdGpuStatus: string;
  runtimeDiskBytes: number;
  observedAt: string;
  diagnostics: string[];
}

export interface RuntimeModelProviderHealthDto {
  providerName: string;
  isHealthy: boolean;
  observedAt: string;
  diagnostics: string[];
}

export interface RuntimeLocalModelDto {
  modelName: string;
  providerName: string;
  isAvailable: boolean;
  sizeBytes: number | null;
  modifiedAt: string | null;
  maxContextTokens: number | null;
}

export interface RuntimeManifestEnvironmentDto {
  name: string;
  value: string;
}

export interface RuntimeManifestVolumeDto {
  source: string;
  target: string;
  readOnly: boolean;
}

export interface RuntimeManifestContainerDto {
  name: string;
  image: string;
  network: string;
  environment: RuntimeManifestEnvironmentDto[];
  volumes: RuntimeManifestVolumeDto[];
}

export interface RuntimeManifestDto {
  available: boolean;
  schemaVersion: number | null;
  runtimeMode: string;
  bootstrapModel: string;
  defaultChatModel: string;
  maxRuntimeDiskGb: number | null;
  stopDrainTimeoutSeconds: number | null;
  containers: RuntimeManifestContainerDto[];
  diagnostics: string[];
}

export interface RuntimeManagerStatusResponseDto {
  status: HostAgentStatusDto;
  capabilities: HostCapabilitiesDto;
  components: RuntimeComponentStatusDto[];
  modelProviderHealth: RuntimeModelProviderHealthDto;
  models: RuntimeLocalModelDto[];
  manifest: RuntimeManifestDto;
}

export type RuntimeContainerActionName = "start" | "stop" | "restart";

export interface RuntimeContainerActionRequestDto {
  containerName: string;
  action: RuntimeContainerActionName;
  drainTimeoutSeconds?: number;
}

export interface RuntimeContainerActionResponseDto {
  containerName: string;
  action: string;
  succeeded: boolean;
  startedAt: string;
  completedAt: string;
  components: RuntimeComponentStatusDto[];
  diagnostics: string[];
}

export interface RuntimeLogsRequestDto {
  containerName: string;
  tailLines?: number;
  follow?: boolean;
}

export interface RuntimeLogLineDto {
  containerName: string;
  stream: string;
  line: string;
  observedAt: string;
}

function signalRStream<T>(hubPath: string, methodName: string, request: unknown, signal: AbortSignal): AsyncIterable<T> {
  return {
    async *[Symbol.asyncIterator](): AsyncIterator<T> {
      const connection = new HubConnectionBuilder()
        .withUrl(buildLocalApiUrl(hubPath), {
          transport: HttpTransportType.LongPolling,
          accessTokenFactory: () => useNodeAuthStore.getState().accessToken ?? "",
        })
        .configureLogging(LogLevel.Warning)
        .build();
      const values: T[] = [];
      let completed = false;
      let failure: unknown;
      let wake: (() => void) | undefined;

      const notify = (): void => {
        wake?.();
        wake = undefined;
      };

      await connection.start();
      const subscription = connection.stream<T>(methodName, request).subscribe({
        next: (value) => {
          values.push(value);
          notify();
        },
        error: (error) => {
          failure = error;
          completed = true;
          notify();
        },
        complete: () => {
          completed = true;
          notify();
        },
      });

      const abort = (): void => {
        subscription.dispose();
        completed = true;
        notify();
      };

      signal.addEventListener("abort", abort, { once: true });

      try {
        while (!completed || values.length > 0) {
          const value = values.shift();
          if (value) {
            yield value;
            continue;
          }

          if (failure) {
            throw failure;
          }

          // biome-ignore lint/performance/noAwaitInLoops: AsyncIterable bridge waits for the next SignalR push before yielding again.
          await new Promise<void>((resolve) => {
            wake = resolve;
          });
        }

        if (failure) {
          throw failure;
        }
      } finally {
        signal.removeEventListener("abort", abort);
        subscription.dispose();
        await connection.stop();
      }
    },
  };
}

export async function getRuntimeManagerStatus(config?: AxiosRequestConfig): Promise<RuntimeManagerStatusResponseDto> {
  const { data } = await axiosInstance.get<RuntimeManagerStatusResponseDto>(buildLocalApiUrl("runtime/status"), config);
  return data;
}

export async function executeRuntimeContainerAction(request: RuntimeContainerActionRequestDto, config?: AxiosRequestConfig): Promise<RuntimeContainerActionResponseDto> {
  const { data } = await axiosInstance.post<RuntimeContainerActionResponseDto>(buildLocalApiUrl("runtime/containers/action"), request, config);
  return data;
}

export function streamRuntimeLogs(request: RuntimeLogsRequestDto, signal: AbortSignal): AsyncIterable<RuntimeLogLineDto> {
  return signalRStream<RuntimeLogLineDto>("runtime/hub", "StreamLogs", request, signal);
}
