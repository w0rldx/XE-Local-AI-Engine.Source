export const runtimeManagerQueryKeys = {
  all: () => ["runtime-manager"] as const,
  status: () => [...runtimeManagerQueryKeys.all(), "status"] as const,
};
