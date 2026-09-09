import type { ImageJobStatus } from "@/features/images/models/ImageModels";

// Mantine badge colour per coarse status. The only place an image-job status maps to a colour.
export const statusColor: Record<ImageJobStatus, string> = {
	Queued: "gray",
	Generating: "blue",
	Succeeded: "green",
	Failed: "red",
	Cancelled: "gray",
};
