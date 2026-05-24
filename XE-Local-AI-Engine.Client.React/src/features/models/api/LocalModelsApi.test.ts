import { describe, expect, it, vi } from "vitest";

const { axiosInstanceMock, buildLocalApiUrlMock } = vi.hoisted(() => ({
  axiosInstanceMock: {
    delete: vi.fn(),
    get: vi.fn(),
    post: vi.fn(),
  },
  buildLocalApiUrlMock: vi.fn((path: string) => `/local/${path}`),
}));

vi.mock("@/core/api/axios/AxiosInstance", () => ({
  axiosInstance: axiosInstanceMock,
}));

vi.mock("@/core/api/utils/LocalApiUrl", () => ({
  buildLocalApiUrl: buildLocalApiUrlMock,
}));

import { deleteLocalModel, getLocalModelDetails, listLocalModels, pullLocalModel, selectLocalModel } from "@/features/models/api/LocalModelsApi";

describe("local models API", () => {
  it("lists local models and forwards request config", async () => {
    const response = { isAvailable: true, selectedModelName: "llama3:8b", configuredDefaultModelName: "llama3:8b", error: null, items: [] };
    const abortController = new AbortController();
    axiosInstanceMock.get.mockResolvedValue({ data: response });

    await expect(listLocalModels({ signal: abortController.signal })).resolves.toBe(response);
    expect(axiosInstanceMock.get).toHaveBeenCalledWith("/local/models", { signal: abortController.signal });
  });

  it("loads model details with an encoded route segment", async () => {
    const details = { modelName: "llama3:8b", maxContextTokens: 8192, template: "{{ .Prompt }}", system: null, license: "fake" };
    axiosInstanceMock.get.mockResolvedValue({ data: details });

    await expect(getLocalModelDetails("llama3:8b")).resolves.toBe(details);
    expect(axiosInstanceMock.get).toHaveBeenCalledWith("/local/models/llama3%3A8b/details", undefined);
  });

  it("selects a model through POST", async () => {
    const request = { modelName: "llama3:8b" };
    const response = { selectedModelName: "llama3:8b" };
    axiosInstanceMock.post.mockResolvedValue({ data: response });

    await expect(selectLocalModel(request)).resolves.toBe(response);
    expect(axiosInstanceMock.post).toHaveBeenCalledWith("/local/models/select", request, undefined);
  });

  it("pulls a model through POST", async () => {
    const request = { modelName: "orca-mini:latest" };
    const response = { modelName: "orca-mini:latest", status: "success", totalBytes: 100, completedBytes: 100 };
    axiosInstanceMock.post.mockResolvedValue({ data: response });

    await expect(pullLocalModel(request)).resolves.toBe(response);
    expect(axiosInstanceMock.post).toHaveBeenCalledWith("/local/models/pull", request, undefined);
  });

  it("deletes a model with an encoded route segment", async () => {
    const response = { modelName: "orca-mini:latest", deleted: true };
    axiosInstanceMock.delete.mockResolvedValue({ data: response });

    await expect(deleteLocalModel("orca-mini:latest")).resolves.toBe(response);
    expect(axiosInstanceMock.delete).toHaveBeenCalledWith("/local/models/orca-mini%3Alatest", undefined);
  });
});
