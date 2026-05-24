export const localModelsQueryKeys = {
  all: () => ["local-models"] as const,
  list: () => [...localModelsQueryKeys.all(), "list"] as const,
  details: (modelName: string) => [...localModelsQueryKeys.all(), "details", modelName] as const,
};
