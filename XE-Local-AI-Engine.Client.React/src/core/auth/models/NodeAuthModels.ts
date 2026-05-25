export interface NodeAuthStatusResponse {
	setupRequired: boolean;
	authenticated: boolean;
}

export interface NodeSetupRequest {
	email: string;
	password: string;
}

export interface NodeLoginRequest {
	email?: string;
	password: string;
}

export interface NodeAccessTokenResponse {
	accessToken: string;
	expiresAtUtc: string;
}

export interface NodeMeResponse {
	userName: string;
	roles: string[];
}

export interface NodeAuthErrorResponse {
	message: string;
	errors?: string[];
}

export interface NodeAuthStoreState {
	accessToken?: string;
	expiresAtUtc?: string;
	actions: {
		setToken: (token: NodeAccessTokenResponse) => void;
		clear: () => void;
	};
}
