// Cloud Function: exchanges a Google OAuth authorization code for an access token.
//
// This exists solely so the OAuth client_secret never has to live in the browser. The frontend
// (docs/js/auth.js) uses Google Identity Services' Code + redirect flow specifically so sign-in
// never opens a popup - it's a plain full-page redirect to Google and back. But Google's token
// endpoint requires client_secret for that exchange on a Web-application-type client (confirmed
// directly against Google's docs - PKCE alone isn't accepted for this client type), so something
// has to hold that secret server-side. This function is that "something": it takes the code the
// frontend just received, does the exchange itself, and returns only the resulting access token -
// the secret itself never leaves this function.
//
// docs/js/auth.js's signIn() now requests access_type=offline + prompt=consent on every sign-in
// (ROADMAP.md #117), so Google's response here also includes a refresh_token. That refresh_token
// is exactly as sensitive as the client_secret - it must never reach the browser either - so
// instead of returning it, this function stores it in Firestore (collection "sessions", doc ID a
// random session_id) and hands the frontend only that opaque session_id. functions/refresh-token/
// is the only thing that ever reads the refresh_token back out, to silently mint new access
// tokens without another full sign-in redirect.
//
// Deploy: Google Cloud Functions (2nd gen), Node.js 20 runtime, HTTP trigger, unauthenticated
// invocations allowed (the frontend calls this directly with no auth of its own - the code being
// exchanged is what proves the caller actually completed Google's consent screen).
// Required environment variables (set in the function's config, never committed to source):
//   GOOGLE_CLIENT_ID     - same Client ID already used in docs/js/config.js
//   GOOGLE_CLIENT_SECRET - the Web OAuth client's secret from Cloud Console
// The runtime service account also needs Firestore read/write (roles/datastore.user) in this
// project - grant that in Cloud Console -> IAM if it isn't there already.

const { Firestore } = require('@google-cloud/firestore');
const crypto = require('node:crypto');
const { checkRateLimit } = require('./rateLimit');

const firestore = new Firestore();
const sessions = firestore.collection('sessions');

// Only these origins may call this function - not a security boundary on its own (a stolen code
// still couldn't be used from elsewhere without matching state client-side first), but it keeps
// the function from being casually probed/abused from random origins.
const ALLOWED_ORIGINS = new Set([
  'https://stephenh678.github.io',
  'http://localhost:5500',
]);

exports.exchangeToken = async (req, res) => {
  const origin = req.headers.origin;
  if (origin && ALLOWED_ORIGINS.has(origin)) {
    res.set('Access-Control-Allow-Origin', origin);
  }
  res.set('Access-Control-Allow-Methods', 'POST, OPTIONS');
  res.set('Access-Control-Allow-Headers', 'Content-Type');

  if (req.method === 'OPTIONS') {
    res.status(204).send('');
    return;
  }
  if (req.method !== 'POST') {
    res.status(405).json({ error: 'Method not allowed' });
    return;
  }

  // ROADMAP.md #137: best-effort defense-in-depth against a leaked/looping caller hammering the
  // Google token endpoint through this function - see rateLimit.js's header comment for exactly
  // what this does and doesn't cover (it's not a substitute for Cloud Armor/API Gateway).
  if (!checkRateLimit(req)) {
    res.status(429).json({ error: 'Too many requests' });
    return;
  }

  const { code, redirect_uri } = req.body ?? {};
  if (!code || !redirect_uri) {
    res.status(400).json({ error: 'Missing code or redirect_uri' });
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
    const tokenRes = await fetch('https://oauth2.googleapis.com/token', {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({
        code,
        client_id: clientId,
        client_secret: clientSecret,
        redirect_uri,
        grant_type: 'authorization_code',
      }),
    });

    const data = await tokenRes.json();
    if (!tokenRes.ok) {
      res.status(400).json({ error: data.error_description || data.error || 'Token exchange failed' });
      return;
    }

    // Deliberately return only what the frontend needs (access token + lifetime + an opaque
    // session handle) - never the id_token, and never the refresh_token itself (see header
    // comment). The frontend fetches the account email itself via the userinfo endpoint using the
    // returned access token.
    let sessionId = null;
    if (data.refresh_token) {
      sessionId = crypto.randomUUID();
      await sessions.doc(sessionId).set({
        refresh_token: data.refresh_token,
        created_at: Firestore.Timestamp.now(),
      });
    }
    res.status(200).json({ access_token: data.access_token, expires_in: data.expires_in, session_id: sessionId });
  } catch (err) {
    console.error('Token exchange error', err);
    res.status(500).json({ error: 'Token exchange failed' });
  }
};
