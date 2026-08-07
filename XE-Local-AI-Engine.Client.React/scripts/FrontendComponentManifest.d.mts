import type { Plugin } from "vite";

export interface FrontendComponent {
	name: string;
	version: string;
	license: string;
	purl: string;
}

export function resolvePackageComponent(moduleId: string): FrontendComponent | null;
export function createFrontendComponentManifestPlugin(): Plugin;
