import type { InferenceProfileStatus } from "@/features/model-fit/models/InferenceProfileModels";

export const statusColor: Record<InferenceProfileStatus, string> = {
	explored: "blue",
	frozen: "green",
	stale: "gray",
};
