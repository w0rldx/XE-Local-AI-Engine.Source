import type { ImageJobStatus } from "@/features/images/models/ImageModels";

// Mantine badge color per coarse status. The only place an image-job status maps to a color.
export const statusColor: Record<ImageJobStatus, string> = {
	Queued: "gray",
	Generating: "blue",
	Succeeded: "green",
	Failed: "red",
	Cancelled: "gray",
};
