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
//
// signIn() builds the Google authorization URL by hand rather than using Google Identity
// Services' initCodeClient() - GIS's CodeClientConfig has no access_type/prompt fields, and
// requesting access_type=offline + prompt=consent is required to get a refresh_token back
// (ROADMAP.md #117). That refresh_token is exchanged and stored server-side by
// functions/exchange-token/, which hands this page back only an opaque session_id. Everything
// below that touches sessionId/refreshAccessToken() exists so getAccessToken() can silently mint
// a new access token near/at expiry - via a plain background fetch, never a redirect - instead of
// forcing the ~hourly reauth this app used to require.
import { GOOGLE_CLIENT_ID, GOOGLE_SCOPES, TOKEN_EXCHANGE_URL, TOKEN_REFRESH_URL } from './config.js?v=36';
import { storage, sessionStore } from './storage.js?v=36';

const TOKEN_CACHE_KEY = 'tasky-auth-token';
const SESSION_ID_KEY = 'tasky-auth-session';
const STATE_KEY = 'tasky-auth-state';
// How long before actual expiry to proactively refresh - large enough that a background tab's
// throttled/delayed timer still fires well before the token is truly unusable.
const REFRESH_BUFFER_MS = 5 * 60 * 1000;
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
let sessionId = null;
let refreshTimer = null;
let refreshInFlight = null;

// All storage access goes through storage.js's guarded helpers: a failed cache write just means
// the next reload needs a real reauth, and unreadable storage reads as "nothing cached".
function loadCachedToken() {
  try {
    const raw = storage.get(TOKEN_CACHE_KEY);
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
    return false; // malformed JSON in the cache - treat as absent
  }
}

function persistToken() {
  storage.set(
    TOKEN_CACHE_KEY,
    JSON.stringify({ accessToken, expiresAt: tokenExpiresAt, accountEmail, accountName, accountPicture })
  );
}

function clearCachedToken() {
  storage.remove(TOKEN_CACHE_KEY);
}

// sessionId lives under its own key, separate from the access-token cache above, because it must
// survive an expired/missing access token - it's exactly what lets a stale/cleared token cache
// still be silently recovered via refreshAccessToken() instead of falling back to a real signIn().
function loadSessionId() {
  return storage.get(SESSION_ID_KEY);
}

function persistSessionId(id) {
  if (id) storage.set(SESSION_ID_KEY, id);
  else storage.remove(SESSION_ID_KEY);
}

/**
 * Forgets the current access token (in memory and in the localStorage cache) without touching
 * the refresh session. drive.js calls this when Google rejects a token that still looked valid by
 * its local expiry (clock skew, an early server-side invalidation) - the very next
 * getAccessToken() then goes through a silent refresh instead of handing back the same rejected
 * token, and only if THAT fails does the user see a sign-in prompt.
 */
export function invalidateAccessToken() {
  accessToken = null;
  tokenExpiresAt = 0;
  clearCachedToken();
}

/**
 * Arms (or re-arms) a background timer to silently refresh the access token shortly before it
 * expires. No-op if there's no session to refresh with. Safe to call repeatedly - always clears
 * any previously-scheduled timer first.
 */
function scheduleRefresh() {
  if (refreshTimer) {
    clearTimeout(refreshTimer);
    refreshTimer = null;
  }
  if (!sessionId) return;
  const delay = Math.max(0, tokenExpiresAt - Date.now() - REFRESH_BUFFER_MS);
  refreshTimer = setTimeout(() => {
    refreshAccessToken();
  }, delay);
}

/**
 * Silently exchanges the server-held refresh token for a new access token - a plain background
 * fetch, never a redirect. Returns true and updates all in-memory/cached token state on success.
 *
 * Only a 401 from the server means the session is gone (session unknown, past its max age, or
 * Google itself invalidated the refresh token - see functions/refresh-token/): then sessionId is
 * cleared so nothing keeps retrying with it, and the caller falls back to the pre-#117 behavior
 * of requiring a real signIn(). Everything else is treated as transient and the session is left
 * intact, since the refresh token itself is presumably still good: a network-level failure
 * (offline, a brief DNS blip), a 429 from the Cloud Function's own rate limiter, a 5xx or a
 * Cloud Run cold start. Those used to be lumped in with 401 and cleared the session too, which
 * meant one rate-limited request forced a full re-consent round trip through Google.
 *
 * A transient failure retries once shortly (isRetry) - honoring Retry-After on a 429 - before
 * falling back to the slower unattended 60s retry loop. That immediate retry matters because
 * getAccessToken() (below) calls this synchronously and reports back to its caller the moment
 * this returns false - without it, a single blip here surfaced as an error (e.g. an inline photo
 * failing to load) even though the very next attempt would likely have succeeded on its own.
 *
 * Concurrent callers share one in-flight request: the pre-expiry timer, the visibilitychange
 * catch-up below and any number of getAccessToken() callers (a save racing an attachment
 * download) can all want a refresh at the same instant, and firing one request per caller is
 * exactly what trips the rate limiter in the first place.
 */
function refreshAccessToken() {
  if (!sessionId) return Promise.resolve(false);
  if (!refreshInFlight) {
    refreshInFlight = doRefreshAccessToken().finally(() => {
      refreshInFlight = null;
    });
  }
  return refreshInFlight;
}

const REFRESH_RETRY_MS = 1500;
const REFRESH_RATE_LIMITED_RETRY_MS = 5000;
const REFRESH_RETRY_AFTER_CAP_MS = 10 * 1000;
const REFRESH_UNATTENDED_RETRY_MS = 60 * 1000;

async function doRefreshAccessToken(isRetry = false) {
  const startedWith = sessionId;
  let res;
  let data;
  try {
    res = await fetch(TOKEN_REFRESH_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ session_id: sessionId }),
    });
    if (res.ok) data = await res.json();
  } catch (err) {
    if (sessionId !== startedWith) return false;
    return retryRefreshLater(isRetry, `request failed: ${err.message}`, REFRESH_RETRY_MS);
  }
  // signOut() (or a fresh sign-in) happened while this was in flight - applying the response would
  // put a token back in memory and in the localStorage cache for a user who just signed out.
  if (sessionId !== startedWith) return false;
  if (res.status === 401) {
    console.warn('Tasky: silent token refresh rejected, session cleared');
    sessionId = null;
    persistSessionId(null);
    return false;
  }
  if (!res.ok) {
    const delay = res.status === 429
      ? retryAfterMs(res) ?? REFRESH_RATE_LIMITED_RETRY_MS
      : REFRESH_RETRY_MS;
    return retryRefreshLater(isRetry, `HTTP ${res.status}`, delay);
  }
  accessToken = data.access_token;
  tokenExpiresAt = Date.now() + (Number(data.expires_in) || 3600) * 1000;
  // This path runs whenever the cached access token itself had already expired (loadCachedToken()
  // bails out before ever reading its accountEmail/Name/Picture fields in that case) - without
  // this, accountEmail/Name/Picture stay at their fresh-page-load `null`, onSignedIn() has
  // nothing to show, and the header avatar is stuck on its bare "?" fallback for the rest of the
  // session even though the refresh above just proved the user is genuinely still signed in
  // (reported live: the "?" read as "signed out" when the account was fine and tasks loaded
  // normally). persistToken() below would otherwise also bake those nulls back into the cache.
  if (!accountEmail) await fetchAccountInfo();
  if (sessionId !== startedWith) {
    // Signed out during the userinfo request above - undo the token this call just set.
    accessToken = null;
    tokenExpiresAt = 0;
    return false;
  }
  persistToken();
  scheduleRefresh();
  return true;
}

async function retryRefreshLater(isRetry, reason, delayMs) {
  if (!isRetry) {
    console.warn(`Tasky: silent token refresh failed once (${reason}), retrying in ${delayMs}ms`);
    await new Promise((resolve) => setTimeout(resolve, delayMs));
    return doRefreshAccessToken(true);
  }
  console.warn(`Tasky: silent token refresh failed (${reason}), will retry in ${REFRESH_UNATTENDED_RETRY_MS / 1000}s`);
  clearTimeout(refreshTimer);
  refreshTimer = setTimeout(() => refreshAccessToken(), REFRESH_UNATTENDED_RETRY_MS);
  return false;
}

// Retry-After can be delay-seconds or an HTTP date; capped so a getAccessToken() caller that's
// waiting on this (a save in progress) never hangs for longer than a few seconds.
function retryAfterMs(res) {
  const header = res.headers.get('Retry-After');
  if (!header) return null;
  const seconds = Number(header);
  const ms = Number.isFinite(seconds) ? seconds * 1000 : Date.parse(header) - Date.now();
  if (!Number.isFinite(ms) || ms <= 0) return null;
  return Math.min(ms, REFRESH_RETRY_AFTER_CAP_MS);
}

// A tab that's been backgrounded (laptop asleep, phone locked) can have its timers throttled or
// paused outright, so the scheduled refresh above can't be trusted alone - catch up immediately
// whenever the tab becomes visible again if the token is already at/past its refresh point.
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState !== 'visible') return;
  if (sessionId && Date.now() >= tokenExpiresAt - REFRESH_BUFFER_MS) {
    refreshAccessToken();
  }
});

/**
 * Restores a session from the localStorage cache, falling back to a silent server-side refresh
 * (still just a background fetch - never a redirect) if the cached access token is missing or
 * expired but a refresh session is available. Returns true if the caller ends up signed in either
 * way. Safe to call from page load.
 */
export async function restoreFromCache() {
  sessionId = loadSessionId();
  if (loadCachedToken()) {
    scheduleRefresh();
    return true;
  }
  if (sessionId && (await refreshAccessToken())) {
    return true;
  }
  return false;
}

/**
 * Checks whether this page load is Google redirecting back with an authorization code (or an
 * error, e.g. the user cancelled). If so, exchanges the code for a token via the Cloud Function
 * and cleans the params out of the URL either way, so a later refresh doesn't resubmit an
 * already-used code. Call this once at boot, before restoreFromCache().
 *
 * Returns { status: 'none' } when this load isn't a redirect return at all (the common case -
 * plain boot or a cache-restored session), { status: 'success' } once the token exchange
 * completes, or { status: 'error', message } for anything that went wrong along the way - callers
 * need to tell these apart, since only 'error' is worth surfacing to the user; 'none' happening on
 * every normal boot would otherwise look identical to a swallowed failure.
 */
export async function handleRedirectReturn() {
  const params = new URLSearchParams(window.location.search);
  const code = params.get('code');
  const error = params.get('error');
  const returnedState = params.get('state');

  if (!code && !error) return { status: 'none' };

  window.history.replaceState(null, '', window.location.pathname);
  const expectedState = sessionStore.get(STATE_KEY) || storage.get(STATE_KEY);
  sessionStore.remove(STATE_KEY);
  storage.remove(STATE_KEY);

  if (error) {
    return {
      status: 'error',
      message: error === 'access_denied' ? 'Sign-in was cancelled.' : `Sign-in failed: ${error}`,
    };
  }
  if (!expectedState || !returnedState || returnedState.trim() !== expectedState.trim()) {
    console.error('OAuth state mismatch - discarding response.');
    return { status: 'error', message: 'Sign-in failed: security check did not match. Please try again.' };
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
    sessionId = data.session_id ?? null;
    persistSessionId(sessionId);
    await fetchAccountInfo();
    persistToken();
    scheduleRefresh();
    return { status: 'success' };
  } catch (err) {
    console.error('Token exchange failed', err);
    return { status: 'error', message: 'Sign-in failed: could not complete sign-in. Please try again.' };
  }
}

/**
 * Triggers Google sign-in. Must be called from a user gesture (click handler) - this navigates
 * the entire tab to Google's consent screen; there is nothing to await here. The result arrives
 * via handleRedirectReturn() on the page load Google sends the browser back to.
 *
 * Built by hand rather than via Google Identity Services' initCodeClient() - see the file header
 * for why (GIS's redirect mode has no access_type/prompt support, and both are required to get a
 * refresh_token). prompt=consent forces the consent screen even for a returning user, which is
 * the one-time UX cost of guaranteeing a refresh_token on every sign-in rather than only the
 * first - acceptable since silent refresh (below) should make explicit sign-ins rare.
 */
export function signIn() {
  const state = (typeof crypto !== 'undefined' && crypto.randomUUID)
    ? crypto.randomUUID()
    : (Math.random().toString(36).slice(2) + Date.now().toString(36));
  sessionStore.set(STATE_KEY, state);
  storage.set(STATE_KEY, state);
  const params = new URLSearchParams({
    client_id: GOOGLE_CLIENT_ID,
    redirect_uri: REDIRECT_URI,
    response_type: 'code',
    scope: GOOGLE_SCOPES,
    state,
    access_type: 'offline',
    prompt: 'consent',
    include_granted_scopes: 'true',
  });
  window.location.href = `https://accounts.google.com/o/oauth2/v2/auth?${params.toString()}`;
}

export function signOut() {
  if (sessionId) {
    // Revokes the refresh token server-side, which also invalidates any access token derived
    // from it - covers what a client-side GIS revoke() call used to handle. There's deliberately
    // no GIS script on this page anymore (see auth.js's header comment) since merely loading it
    // was enough to break the Drive scope (ROADMAP.md #117), so this is the only revoke path left.
    // Best-effort - local state below is cleared regardless of whether this reaches the server.
    fetch(TOKEN_REFRESH_URL, {
      method: 'DELETE',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ session_id: sessionId }),
    }).catch(() => {});
  }
  if (refreshTimer) {
    clearTimeout(refreshTimer);
    refreshTimer = null;
  }
  accessToken = null;
  tokenExpiresAt = 0;
  accountEmail = null;
  accountName = null;
  accountPicture = null;
  sessionId = null;
  clearCachedToken();
  persistSessionId(null);
}

export function isSignedIn() {
  return !!accessToken && Date.now() < tokenExpiresAt;
}

/**
 * True while a refresh session exists, even if the access token itself has lapsed - i.e. the
 * next getAccessToken() can be expected to succeed silently. Lets UI that only wants to know
 * "would a Drive call need a redirect sign-in first?" avoid sending an expired-but-refreshable
 * session through the consent screen.
 */
export function hasRefreshSession() {
  return !!sessionId;
}

/**
 * True if restoreFromCache() has anything to work with at all - readable before it runs, so boot
 * can tell "restoring a session" apart from "nobody has signed in on this device".
 */
export function hasStoredSession() {
  return !!loadSessionId() || !!storage.get(TOKEN_CACHE_KEY);
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
 * Returns a valid access token. If the cached one has expired but a refresh session exists, this
 * tries one silent refresh first - still just a background fetch, never a redirect - since that's
 * now the common case (ROADMAP.md #117) rather than the exception. Deliberately does NOT attempt
 * a real signIn() when there's no usable session, though: this is called from background contexts
 * too (the autosave debounce), and navigating the tab away unprompted there would be jarring.
 * Callers get a clear NOT_SIGNED_IN error and are expected to prompt for a click-driven signIn().
 */
export async function getAccessToken() {
  if (isSignedIn()) return accessToken;
  if (sessionId) {
    if (await refreshAccessToken()) return accessToken;
    // The refresh failed but the session survived it, so it was a transient failure (offline,
    // rate-limited, the Cloud Function briefly down) rather than a dead session - the user is
    // still signed in and nothing they do at Google's consent screen would help. Callers treat
    // this like any other network error (retry later) instead of showing the sign-in prompt.
    if (sessionId) throw new Error('AUTH_UNAVAILABLE');
  }
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
