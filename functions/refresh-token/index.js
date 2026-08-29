// Cloud Function: silently refreshes a Tasky Web access token using a refresh token that never
// leaves the server, and revokes/clears that session on sign-out. Companion to
// functions/exchange-token/ - see that file's header for how the session comes to exist in the
// first place (ROADMAP.md #117).
//
// POST { session_id } -> { access_token, expires_in }: exchanges the stored refresh_token with
//   Google for a fresh access token. docs/js/auth.js calls this proactively a few minutes before
//   the current access token expires, and also on-demand if a call finds the token already
//   expired - either way, this never redirects the user anywhere, unlike a full signIn().
//   If the refresh_token turns out to be dead (user revoked access from their Google Account, or
//   Google's own token invalidation) Google returns invalid_grant; this function deletes the
//   now-useless Firestore doc and returns 401, so the frontend falls back to a normal signIn()
//   the next time one is needed - never a silent redirect.
//
// DELETE { session_id } -> {}: called from signOut(). Revokes the refresh token with Google
//   (which also invalidates any access tokens issued from it) and deletes the Firestore doc.
//   Best-effort - signOut() clears its own local state regardless of whether this succeeds.
//
// Deploy: Google Cloud Functions (2nd gen), Node.js 20 runtime, HTTP trigger, unauthenticated
// invocations allowed (mirrors functions/exchange-token/ - the session_id itself is the only
// credential this needs, and it's meaningless without the matching Firestore doc). Needs the
// same GOOGLE_CLIENT_ID / GOOGLE_CLIENT_SECRET env vars as exchange-token, and its runtime
// service account needs Firestore read/write (roles/datastore.user) in this project.

const { Firestore } = require('@google-cloud/firestore');
const { checkRateLimit } = require('./rateLimit');

const firestore = new Firestore();
const sessions = firestore.collection('sessions');

const ALLOWED_ORIGINS = new Set([
  'https://stephenh678.github.io',
  'http://localhost:5500',
]);

// ROADMAP.md #137: a session_id lived in Firestore forever until an explicit sign-out DELETEd it -
// so a leaked/stale one (a stolen device, a browser profile nobody uses anymore) stayed a silently
// working credential indefinitely. Capped instead: once a session's created_at is older than this,
// POST (refresh) treats it the same as an invalid_grant from Google - delete it and 401, so the
// frontend falls back to a normal signIn(). Configurable since this is a UX/security tradeoff
// (shorter = more re-logins, longer = a leaked session stays useful longer) rather than a
// correctness fix with one right answer; 90 days is a conventional "remember me" default.
const SESSION_MAX_AGE_MS = (Number(process.env.SESSION_MAX_AGE_DAYS) || 90) * 24 * 60 * 60 * 1000;

exports.refreshToken = async (req, res) => {
  const origin = req.headers.origin;
  if (origin && ALLOWED_ORIGINS.has(origin)) {
    res.set('Access-Control-Allow-Origin', origin);
  }
  res.set('Access-Control-Allow-Methods', 'POST, DELETE, OPTIONS');
  res.set('Access-Control-Allow-Headers', 'Content-Type');

  if (req.method === 'OPTIONS') {
    res.status(204).send('');
    return;
  }

  if (!checkRateLimit(req)) {
    res.status(429).json({ error: 'Too many requests' });
    return;
  }

  const { session_id } = req.body ?? {};
  if (!session_id) {
    res.status(400).json({ error: 'Missing session_id' });
    return;
  }
  const docRef = sessions.doc(session_id);

  if (req.method === 'DELETE') {
    try {
      const snap = await docRef.get();
      if (snap.exists) {
        const { refresh_token } = snap.data();
        // Best-effort - a failed revoke just leaves a dead session doc behind, no security impact.
        await fetch(`https://oauth2.googleapis.com/revoke?token=${encodeURIComponent(refresh_token)}`, {
          method: 'POST',
        }).catch(() => {});
        await docRef.delete();
      }
    } catch (err) {
      console.error('Session revoke error', err);
    }
    res.status(200).json({});
    return;
  }

  if (req.method !== 'POST') {
    res.status(405).json({ error: 'Method not allowed' });
    return;
  }

  const clientId = process.env.GOOGLE_CLIENT_ID;
  const clientSecret = process.env.GOOGLE_CLIENT_SECRET;
  if (!clientId || !clientSecret) {
    console.error('Missing GOOGLE_CLIENT_ID / GOOGLE_CLIENT_SECRET environment variables.');
    res.status(500).json({ error: 'Server misconfigured' });
    return;
  }

  try {
    const snap = await docRef.get();
    if (!snap.exists) {
      res.status(401).json({ error: 'Unknown session' });
      return;
    }
    const { refresh_token, created_at } = snap.data();

    // See SESSION_MAX_AGE_MS's comment above. created_at is only ever set once, at session
    // creation in exchange-token/ - a refresh-token rotation (below) never touches it, so this is
    // genuinely "time since this session first signed in," not "time since last refresh."
    const ageMs = created_at ? Date.now() - created_at.toMillis() : Infinity;
    if (ageMs > SESSION_MAX_AGE_MS) {
      await docRef.delete().catch(() => {});
      res.status(401).json({ error: 'Session expired' });
      return;
    }

    const tokenRes = await fetch('https://oauth2.googleapis.com/token', {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({
        client_id: clientId,
        client_secret: clientSecret,
        refresh_token,
        grant_type: 'refresh_token',
      }),
    });

    const data = await tokenRes.json();
    if (!tokenRes.ok) {
      // invalid_grant (revoked/expired refresh token) is the expected way this eventually dies -
      // clean up the dead session so future attempts fail fast instead of retrying forever.
      if (data.error === 'invalid_grant') {
        await docRef.delete().catch(() => {});
      }
      res.status(401).json({ error: data.error_description || data.error || 'Refresh failed' });
      return;
    }

    // Google can optionally rotate the refresh token on refresh; if it hands back a new one, the
    // old one is invalidated, so the stored copy must be updated or the next refresh 401s.
    if (data.refresh_token && data.refresh_token !== refresh_token) {
      await docRef.update({ refresh_token: data.refresh_token });
    }

    res.status(200).json({ access_token: data.access_token, expires_in: data.expires_in });
  } catch (err) {
    console.error('Token refresh error', err);
    res.status(500).json({ error: 'Refresh failed' });
  }
};
