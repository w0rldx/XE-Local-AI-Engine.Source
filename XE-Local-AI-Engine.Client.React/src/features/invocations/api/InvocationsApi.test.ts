import { describe, expect, it, vi } from "vitest";

const { axiosInstanceMock, buildLocalApiUrlMock } = vi.hoisted(() => ({
  axiosInstanceMock: {
    get: vi.fn(),
  },
  buildLocalApiUrlMock: vi.fn((path: string) => `/local/${path}`),
}));

vi.mock("@/core/api/axios/AxiosInstance", () => ({
  axiosInstance: axiosInstanceMock,
}));

vi.mock("@/core/api/utils/LocalApiUrl", () => ({
  buildLocalApiUrl: buildLocalApiUrlMock,
}));

import { getInvocationMonitor } from "@/features/invocations/api/InvocationsApi";

describe("invocations API", () => {
  it("loads invocation monitor state and forwards request config", async () => {
    const response = { current: null, history: [], historyCapacity: 50 };
    const abortController = new AbortController();
    axiosInstanceMock.get.mockResolvedValue({ data: response });

    await expect(getInvocationMonitor({ signal: abortController.signal })).resolves.toBe(response);
    expect(axiosInstanceMock.get).toHaveBeenCalledWith("/local/invocations", { signal: abortController.signal });
  });
});
