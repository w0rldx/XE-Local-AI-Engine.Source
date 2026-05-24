import { describe, expect, it } from "vitest";

import { emptyModelValue, formatModelModifiedDate, formatModelSize, toLocalModelViewModel, toPullProgressModel } from "@/features/models/models/LocalModelModel";

describe("local model model helpers", () => {
  it("formats model sizes", () => {
    expect(formatModelSize(1_073_741_824)).toBe("1.0 GB");
    expect(formatModelSize(1_048_576)).toBe("1.0 MB");
    expect(formatModelSize(1024)).toBe("1.0 KB");
    expect(formatModelSize(null)).toBe(emptyModelValue);
  });

  it("formats UTC modified dates deterministically", () => {
    expect(formatModelModifiedDate(Date.UTC(2026, 4, 24))).toBe("2026-05-24");
    expect(formatModelModifiedDate(undefined)).toBe(emptyModelValue);
  });

  it("maps local model DTOs to display labels", () => {
    const model = toLocalModelViewModel({
      modelName: "llama3:8b",
      sizeBytes: 1_073_741_824,
      modifiedAtUtc: Date.UTC(2026, 4, 24),
      family: "llama",
      parameterSize: "8B",
      quantizationLevel: "Q4_0",
      isSelected: true,
    });

    expect(model).toEqual({
      modelName: "llama3:8b",
      sizeLabel: "1.0 GB",
      modifiedDateLabel: "2026-05-24",
      familyLabel: "llama",
      parameterSizeLabel: "8B",
      quantizationLabel: "Q4_0",
      isSelected: true,
    });
  });

  it("maps pull progress and clamps percentages", () => {
    expect(toPullProgressModel({ modelName: "llama", status: "pulling", totalBytes: 100, completedBytes: 150 })).toEqual({
      status: "pulling",
      progressPercent: 100,
    });
    expect(toPullProgressModel({ modelName: "llama", status: "", totalBytes: null, completedBytes: null })).toEqual({
      status: "Complete",
      progressPercent: undefined,
    });
  });
});
