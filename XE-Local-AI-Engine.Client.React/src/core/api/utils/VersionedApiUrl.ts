import { environment } from "@/Environment";

const apiVersionSegment = environment.VITE_API_VERSION;

function trimTrailingSlash(url: string): string {
	return url.endsWith("/") ? url.slice(0, -1) : url;
}

function trimLeadingSlash(path: string): string {
	return path.startsWith("/") ? path.slice(1) : path;
}

const apiRootUrl = trimTrailingSlash(environment.VITE_API_URL);

export const versionedApiBaseUrl = `${apiRootUrl}/api/${apiVersionSegment}`;

export function buildVersionedApiUrl(path: string): string {
	return `${versionedApiBaseUrl}/${trimLeadingSlash(path)}`;
}
