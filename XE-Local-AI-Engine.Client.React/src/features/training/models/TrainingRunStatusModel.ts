// Mantine badge colour per training-run lifecycle status. The call site coalesces an unknown status to "gray", so a
// status this map has not seen renders neutral rather than blank.
export const statusColors: Record<string, string> = {
	Queued: "gray",
	Preparing: "blue",
	Training: "blue",
	Exporting: "blue",
	Smoke: "blue",
	Succeeded: "green",
	Failed: "red",
	Cancelled: "yellow",
};
