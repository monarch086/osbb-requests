import { Injectable } from '@angular/core';
import { Amplify } from 'aws-amplify';
import {
  confirmResetPassword,
  confirmSignUp,
  fetchAuthSession,
  getCurrentUser,
  resendSignUpCode,
  resetPassword,
  signIn,
  signOut,
  signUp,
} from 'aws-amplify/auth';
import { cognitoUserPoolsTokenProvider } from 'aws-amplify/auth/cognito';
import { sessionStorage } from 'aws-amplify/utils';

@Injectable({ providedIn: 'root' })
export class CognitoAuthClient {
  getCurrentUser = getCurrentUser;
  fetchAuthSession = fetchAuthSession;
  signIn = signIn;
  signUp = signUp;
  confirmSignUp = confirmSignUp;
  resendSignUpCode = resendSignUpCode;
  resetPassword = resetPassword;
  confirmResetPassword = confirmResetPassword;
  signOut = signOut;

  configure(userPoolId: string, userPoolClientId: string): void {
    Amplify.configure({ Auth: { Cognito: { userPoolId, userPoolClientId } } });
    cognitoUserPoolsTokenProvider.setKeyValueStorage(sessionStorage);
  }
}
