import { environment } from "@/Environment";

function trimSlashes(path: string): string {
	return path.replace(/^\/+|\/+$/g, "");
}

const localApiBaseUrl = `/api/local/${environment.VITE_API_VERSION}`;

export function buildLocalApiUrl(path: string): string {
	const normalizedPath = trimSlashes(path);
	return normalizedPath ? `${localApiBaseUrl}/${normalizedPath}` : localApiBaseUrl;
}
