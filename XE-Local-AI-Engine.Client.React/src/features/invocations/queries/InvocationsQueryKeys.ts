export const invocationsQueryKeys = {
  all: () => ["invocations"] as const,
  monitor: () => [...invocationsQueryKeys.all(), "monitor"] as const,
};
