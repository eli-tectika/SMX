import { InteractionRequiredAuthError, PublicClientApplication } from '@azure/msal-browser';
import { setAccessTokenProvider } from '../api/client';

const clientId = import.meta.env.VITE_ENTRA_CLIENT_ID as string | undefined;
const tenantId = import.meta.env.VITE_ENTRA_TENANT_ID as string | undefined;
const apiScope = import.meta.env.VITE_API_SCOPE as string | undefined;

/**
 * When VITE_ENTRA_CLIENT_ID is unset (local dev), auth is a no-op and the app runs open behind the
 * Vite proxy + MSW mocks. When set (deployed image), the operator is redirected to Microsoft sign-in
 * and every /api call carries a freshly-acquired bearer token.
 *
 * Returns false if the page is mid-redirect (caller must NOT render — the browser is navigating away).
 */
export async function ensureAuthenticated(): Promise<boolean> {
  if (!clientId || !tenantId || !apiScope) return true; // open mode

  const msal = new PublicClientApplication({
    auth: { clientId, authority: `https://login.microsoftonline.com/${tenantId}`, redirectUri: window.location.origin },
    cache: { cacheLocation: 'sessionStorage' },
  });
  await msal.initialize();

  const redirect = await msal.handleRedirectPromise();
  const account = redirect?.account ?? msal.getActiveAccount() ?? msal.getAllAccounts()[0] ?? null;
  if (!account) {
    await msal.loginRedirect({ scopes: [apiScope] }); // navigates away; nothing renders
    return false;
  }
  msal.setActiveAccount(account);
  setAccessTokenProvider(async () => {
    try {
      const r = await msal.acquireTokenSilent({ scopes: [apiScope], account });
      return r.accessToken;
    } catch (e) {
      if (e instanceof InteractionRequiredAuthError) {
        await msal.acquireTokenRedirect({ scopes: [apiScope], account }); // navigates away to re-auth
        return null; // page is redirecting; the in-flight call is abandoned
      }
      throw e;
    }
  });
  return true;
}
