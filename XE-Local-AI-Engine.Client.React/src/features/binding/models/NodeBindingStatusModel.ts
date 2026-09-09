export function statusColor(status: string): "blue" | "green" | "orange" | "red" {
	const normalized = status.toLowerCase();
	if (normalized === "approved") {
		return "green";
	}
	if (["expired", "denied", "consumed", "cancelled"].includes(normalized)) {
		return "orange";
	}
	if (normalized === "failed") {
		return "red";
	}
	return "blue";
}
