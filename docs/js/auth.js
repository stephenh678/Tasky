// Google sign-in via the Authorization Code + redirect flow - the only way to get a genuine
// full-page redirect (no popup window at all) while still ending with a usable access token.
//
// The token model (initTokenClient) was tried first since it needs no backend at all, but it's
// confirmed (directly against Google's own API reference) to have no redirect option whatsoever -
// it is popup-only, full stop. The code model's redirect mode is real, but Google's token endpoint
// requires the client_secret to complete that exchange for a Web-application-type client, even
// with PKCE - so something has to hold that secret server-side. That's what
// functions/exchange-token/ is: a small Cloud Function that takes the authorization code this
// page receives and does the actual exchange, returning only the resulting access token. The
// secret itself never reaches the browser.
//
// Flow: signIn() navigates the whole tab to Google's consent screen. Google redirects back to
// this exact page with ?code=...&state=... in the URL. handleRedirectReturn(), called once at
// boot, notices that, verifies state (CSRF protection - state is generated and stashed in
// sessionStorage right before navigating away), POSTs the code to the Cloud Function, and stores
// the token it gets back. The access token is also cached in localStorage (scoped narrowly to
// drive.file + email, expires in under an hour - no more sensitive than a session cookie) so a
// same-hour reload restores the session with zero network calls and zero redirects.
import { GOOGLE_CLIENT_ID, GOOGLE_SCOPES, TOKEN_EXCHANGE_URL } from './config.js?v=15';

const TOKEN_CACHE_KEY = 'tasky-auth-token';
const STATE_KEY = 'tasky-auth-state';
// Must exactly match an Authorized redirect URI on the Web OAuth client (Google does a strict
// string comparison, no normalization) - stripping a trailing slash keeps this in sync with how
// the URI is naturally registered in Cloud Console (without one) regardless of whether the page
// was visited with or without a trailing slash. Also strips a trailing index.html, since an
// installed PWA launches at manifest.json's start_url (which resolves to the literal .../index.html
// path) while a normal browser visit resolves to the bare directory URL - without this, those two
// access paths compute different redirect_uri values and only one of them matches what's
// registered in Cloud Console, so signing in from the other fails with redirect_uri_mismatch.
const REDIRECT_URI = (window.location.origin + window.location.pathname)
  .replace(/\/index\.html$/i, '')
  .replace(/\/$/, '');

let accessToken = null;
let tokenExpiresAt = 0;
let accountEmail = null;
let accountName = null;
let accountPicture = null;

function loadCachedToken() {
  try {
    const raw = localStorage.getItem(TOKEN_CACHE_KEY);
    if (!raw) return false;
    const cached = JSON.parse(raw);
    if (!cached.accessToken || !cached.expiresAt || Date.now() >= cached.expiresAt) return false;
    accessToken = cached.accessToken;
    tokenExpiresAt = cached.expiresAt;
    accountEmail = cached.accountEmail ?? null;
    accountName = cached.accountName ?? null;
    accountPicture = cached.accountPicture ?? null;
    return true;
  } catch {
    return false;
  }
}

function persistToken() {
  try {
    localStorage.setItem(
      TOKEN_CACHE_KEY,
      JSON.stringify({ accessToken, expiresAt: tokenExpiresAt, accountEmail, accountName, accountPicture })
    );
  } catch {
    // Best-effort - a failed cache write just means the next reload needs a real reauth.
  }
}

function clearCachedToken() {
  try {
    localStorage.removeItem(TOKEN_CACHE_KEY);
  } catch {
    // Nothing to clean up if this fails.
  }
}

/**
 * Restores a session from the localStorage cache only - no network call, no redirect. Safe to
 * call from page load. Returns true if a still-valid cached token was found.
 */
export function restoreFromCache() {
  return loadCachedToken();
}

/**
 * Checks whether this page load is Google redirecting back with an authorization code (or an
 * error, e.g. the user cancelled). If so, exchanges the code for a token via the Cloud Function
 * and cleans the params out of the URL either way, so a later refresh doesn't resubmit an
 * already-used code. Call this once at boot, before restoreFromCache().
 */
export async function handleRedirectReturn() {
  const params = new URLSearchParams(window.location.search);
  const code = params.get('code');
  const error = params.get('error');
  const returnedState = params.get('state');

  if (!code && !error) return false;

  window.history.replaceState(null, '', window.location.pathname);
  const expectedState = sessionStorage.getItem(STATE_KEY);
  sessionStorage.removeItem(STATE_KEY);

  if (error) return false;
  if (!expectedState || returnedState !== expectedState) {
    console.error('OAuth state mismatch - discarding response.');
    return false;
  }

  try {
    const res = await fetch(TOKEN_EXCHANGE_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ code, redirect_uri: REDIRECT_URI }),
    });
    if (!res.ok) throw new Error(`Token exchange failed (${res.status})`);
    const data = await res.json();
    accessToken = data.access_token;
    tokenExpiresAt = Date.now() + (Number(data.expires_in) || 3600) * 1000;
    await fetchAccountInfo();
    persistToken();
    return true;
  } catch (err) {
    console.error('Token exchange failed', err);
    return false;
  }
}

/**
 * Triggers Google sign-in. Must be called from a user gesture (click handler) - this navigates
 * the entire tab to Google's consent screen; there is nothing to await here. The result arrives
 * via handleRedirectReturn() on the page load Google sends the browser back to.
 */
export function signIn() {
  if (!window.google?.accounts?.oauth2) {
    throw new Error('Google Identity Services has not loaded yet.');
  }
  const state = crypto.randomUUID();
  sessionStorage.setItem(STATE_KEY, state);
  const client = window.google.accounts.oauth2.initCodeClient({
    client_id: GOOGLE_CLIENT_ID,
    scope: GOOGLE_SCOPES,
    ux_mode: 'redirect',
    redirect_uri: REDIRECT_URI,
    state,
  });
  client.requestCode();
}

export function signOut() {
  if (accessToken) {
    window.google?.accounts?.oauth2?.revoke(accessToken, () => {});
  }
  accessToken = null;
  tokenExpiresAt = 0;
  accountEmail = null;
  accountName = null;
  accountPicture = null;
  clearCachedToken();
}

export function isSignedIn() {
  return !!accessToken && Date.now() < tokenExpiresAt;
}

export function getAccountEmail() {
  return accountEmail;
}

export function getAccountName() {
  return accountName;
}

export function getAccountPicture() {
  return accountPicture;
}

/**
 * Returns a valid access token. Deliberately does NOT attempt a reauth when the token has
 * expired - this is called from background contexts too (the autosave debounce), and a reauth
 * attempt there would mean navigating the tab away unprompted. Callers get a clear NOT_SIGNED_IN
 * error and are expected to prompt for a real click-driven signIn() instead.
 */
export async function getAccessToken() {
  if (isSignedIn()) return accessToken;
  throw new Error('NOT_SIGNED_IN');
}

async function fetchAccountInfo() {
  try {
    const res = await fetch('https://www.googleapis.com/oauth2/v3/userinfo', {
      headers: { Authorization: `Bearer ${accessToken}` },
    });
    if (!res.ok) return;
    const data = await res.json();
    accountEmail = data.email ?? null;
    accountName = data.name ?? null;
    accountPicture = data.picture ?? null;
  } catch {
    // Cosmetic only ("Connected as ...") - never worth surfacing an error for.
  }
}
