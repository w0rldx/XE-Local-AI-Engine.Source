/** biome-ignore-all lint/suspicious/noConsole: <This is a Vite plugin, so we need to use console.log> */

import child_process from "node:child_process";
import fs from "node:fs";
import path from "node:path";

import type { Plugin } from "vite";

interface AspNetCoreDevelopmentCertificateOptions {
	certificateName?: string;
}

interface CertificateData {
	key: Buffer;
	cert: Buffer;
}

/**
 * Creates or finds the ASP.NET Core dev certificate.
 * @param {string} certificateName - The name for the certificate file.
 * @returns {CertificateData}
 */
function createCertificate(certificateName: string): CertificateData {
	const baseFolder =
		process.env.APPDATA !== undefined && process.env.APPDATA !== ""
			? `${process.env.APPDATA}/ASP.NET/https`
			: `${process.env.HOME}/.aspnet/https`;

	const certFilePath = path.join(baseFolder, `${certificateName}.pem`);
	const keyFilePath = path.join(baseFolder, `${certificateName}.key`);

	if (!fs.existsSync(baseFolder)) {
		fs.mkdirSync(baseFolder, { recursive: true });
	}

	// If the certificate and key don't exist, create them
	if (!fs.existsSync(certFilePath) || !fs.existsSync(keyFilePath)) {
		console.log(`Creating certificate for ${certificateName} at ${certFilePath}...`);
		const result = child_process.spawnSync(
			"dotnet",
			["dev-certs", "https", "--export-path", certFilePath, "--format", "Pem", "--no-password"],
			{ stdio: "inherit" },
		);

		if (result.status !== 0) {
			throw new Error("Could not create the ASP.NET Core certificate.");
		}
		console.log("Certificate created successfully.");
	} else {
		console.log(`Using existing certificate for ${certificateName} from ${certFilePath}.`);
	}

	// Read the certificate and key files into Buffers
	return {
		cert: fs.readFileSync(certFilePath),
		key: fs.readFileSync(keyFilePath),
	};
}

/**
 * A Vite plugin to use the ASP.NET Core developer certificate for the dev server.
 * @param {AspNetCoreDevCertOptions} [options] - The plugin options.
 * @returns {Plugin}
 */
export default function aspNetCoreDevelopmentCertificate(options?: AspNetCoreDevelopmentCertificateOptions): Plugin {
	return {
		name: "vite-plugin-aspnetcore-development-certificate",
		enforce: "pre",
		config: (config, { command }) => {
			// The plugin should only run in development mode ("serve" command)
			if (command !== "serve" || !config.server) {
				console.log("Skipping ASP.NET Core development certificate plugin in non-development mode.");

				return null;
			}

			const certName = options?.certificateName || process.env.npm_package_name || "vite-app";

			const { cert, key } = createCertificate(certName);

			return {
				server: {
					https: {
						key,
						cert,
					},
				},
				port: 5173,
			};
		},
	};
}
