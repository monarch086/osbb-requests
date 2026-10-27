export interface AppConfig {
  apiUrl: string;
  clientId: string;
  userPoolId: string;
}

export interface SignedInUser {
  profile: { email?: string };
}

export type SignInResult = 'SIGNED_IN' | 'CONFIRM_SIGN_UP' | 'RESET_PASSWORD';
