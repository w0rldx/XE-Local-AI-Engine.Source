import { environment } from "@/Environment";

const apiVersionSegment = environment.VITE_API_VERSION;

function trimLeadingSlash(path: string): string {
	return path.startsWith("/") ? path.slice(1) : path;
}

export const versionedApiBaseUrl = `/api/${apiVersionSegment}`;

export function buildVersionedApiUrl(path: string): string {
	return `${versionedApiBaseUrl}/${trimLeadingSlash(path)}`;
}
