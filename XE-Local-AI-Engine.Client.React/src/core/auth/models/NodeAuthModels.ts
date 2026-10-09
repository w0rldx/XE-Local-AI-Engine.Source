// "pending": the node key is not wrapped yet (fresh install before setup, or a legacy data dir awaiting /vault-setup).
// "locked": a minimal pre-host serves only the SPA, this status call and the unlock routes until the password is given.
export type NodeVaultState = "pending" | "locked" | "unlocked";

export interface NodeAuthStatusResponse {
	setupRequired: boolean;
	authenticated: boolean;
	vault: NodeVaultState;
}

export interface NodeSetupRequest {
	email: string;
	password: string;
}

// `recoveryCode` is null when the node runs on an operator-supplied secret and therefore has no vault to recover.
export interface NodeSetupResponse {
	recoveryCode: string | null;
}

export interface NodeVaultPasswordRequest {
	password: string;
}

export interface NodeVaultConfirmResponse {
	recoveryCode: string;
}

// The recovery unlock rotates the code: the one just used stops working, and this one is shown exactly once.
export interface NodeVaultRecoveryUnlockResponse {
	recoveryCode: string;
}

export interface NodeVaultRecoveryUnlockRequest {
	recoveryCode: string;
	newPassword: string;
}

export interface NodeLoginRequest {
	email?: string;
	password: string;
}

export interface NodeChangePasswordRequest {
	currentPassword: string;
	newPassword: string;
}

export interface NodeAccessTokenResponse {
	accessToken: string;
	expiresAtUtc: string;
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
