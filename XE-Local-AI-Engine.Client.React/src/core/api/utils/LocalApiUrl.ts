import { environment } from "@/Environment";

function trimTrailingSlash(url: string): string {
	return url.endsWith("/") ? url.slice(0, -1) : url;
}

function trimSlashes(path: string): string {
	return path.replace(/^\/+|\/+$/g, "");
}

export const localApiBaseUrl = `${trimTrailingSlash(environment.VITE_API_URL)}/api/local/v1`;

export function buildLocalApiUrl(path: string): string {
	const normalizedPath = trimSlashes(path);
	return normalizedPath ? `${localApiBaseUrl}/${normalizedPath}` : localApiBaseUrl;
}
