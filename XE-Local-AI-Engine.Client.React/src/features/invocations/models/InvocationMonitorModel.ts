import type { InvocationHistoryDto, InvocationStatusDto } from "@/features/invocations/api/InvocationsApi";

export const invocationEmptyValue = "—";

export function formatInvocationText(value: string | null | undefined): string {
  return value?.trim() || invocationEmptyValue;
}

export function formatInvocationTimestamp(value: string | null | undefined): string {
  if (!value) {
    return "Not reported";
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime()) || date.getTime() === 0) {
    return "Not reported";
  }

  return date.toLocaleString();
}

export function formatInvocationDuration(durationMs: number | null | undefined): string {
  if (durationMs === null || durationMs === undefined || !Number.isFinite(durationMs) || durationMs < 0) {
    return invocationEmptyValue;
  }

  if (durationMs >= 60_000) {
    return `${(durationMs / 60_000).toFixed(1)} min`;
  }

  if (durationMs >= 1000) {
    return `${(durationMs / 1000).toFixed(1)} s`;
  }

  return `${Math.round(durationMs)} ms`;
}

export function getInvocationStatusColor(status: InvocationStatusDto | undefined): "blue" | "green" | "gray" | "red" | "yellow" {
  switch (status) {
    case "Assigned":
    case "Running":
      return "blue";
    case "Completed":
      return "green";
    case "Failed":
      return "red";
    case "Cancelled":
      return "yellow";
    default:
      return "gray";
  }
}

export function isInvocationActive(status: InvocationStatusDto | undefined): boolean {
  return status === "Assigned" || status === "Running";
}

export function sortInvocationHistory(history: InvocationHistoryDto[]): InvocationHistoryDto[] {
  return [...history].sort((left, right) => new Date(right.completedAt).getTime() - new Date(left.completedAt).getTime());
}
