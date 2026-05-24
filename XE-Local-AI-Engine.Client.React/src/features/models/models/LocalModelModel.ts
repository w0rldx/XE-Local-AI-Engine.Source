import type { LocalModelDto, PullLocalModelResponseDto } from "@/features/models/api/LocalModelsApi";

export interface LocalModelViewModel {
  modelName: string;
  sizeLabel: string;
  modifiedDateLabel: string;
  familyLabel: string;
  parameterSizeLabel: string;
  quantizationLabel: string;
  isSelected: boolean;
}

export interface PullProgressModel {
  status: string;
  progressPercent: number | undefined;
}

export const emptyModelValue = "—";

export function formatModelSize(sizeBytes: number | null | undefined): string {
  if (sizeBytes === null || sizeBytes === undefined || !Number.isFinite(sizeBytes) || sizeBytes < 0) {
    return emptyModelValue;
  }

  if (sizeBytes >= 1_073_741_824) {
    return `${(sizeBytes / 1_073_741_824).toFixed(1)} GB`;
  }

  if (sizeBytes >= 1_048_576) {
    return `${(sizeBytes / 1_048_576).toFixed(1)} MB`;
  }

  return `${(sizeBytes / 1024).toFixed(1)} KB`;
}

export function formatModelModifiedDate(modifiedAtUtc: number | null | undefined): string {
  if (modifiedAtUtc === null || modifiedAtUtc === undefined || !Number.isFinite(modifiedAtUtc)) {
    return emptyModelValue;
  }

  return new Date(modifiedAtUtc).toISOString().slice(0, 10);
}

export function toLocalModelViewModel(model: LocalModelDto): LocalModelViewModel {
  return {
    modelName: model.modelName,
    sizeLabel: formatModelSize(model.sizeBytes),
    modifiedDateLabel: formatModelModifiedDate(model.modifiedAtUtc),
    familyLabel: model.family?.trim() || emptyModelValue,
    parameterSizeLabel: model.parameterSize?.trim() || emptyModelValue,
    quantizationLabel: model.quantizationLevel?.trim() || emptyModelValue,
    isSelected: model.isSelected,
  };
}

export function toPullProgressModel(response: PullLocalModelResponseDto): PullProgressModel {
	const totalBytes = response.totalBytes;
	const completedBytes = response.completedBytes;
	const progressPercent = totalBytes !== null && totalBytes > 0 && completedBytes !== null && completedBytes >= 0
		? Math.min(100, Math.max(0, (completedBytes / totalBytes) * 100))
		: undefined;

	return {
		status: response.status.trim() || "Complete",
		progressPercent,
	};
}
