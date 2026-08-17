// Web OAuth client (separate from the desktop app's Desktop-type credential).
// Public by design - Web application client IDs are meant to be visible in client-side code.
export const GOOGLE_CLIENT_ID = '395690152006-c3he874j1gn548j74chkju1f741opfhi.apps.googleusercontent.com';

// Sign-in uses the Code + redirect flow (see auth.js) so there's never a popup window - but
// Google's token endpoint requires the client_secret for that exchange on a Web-application-type
// client, so the actual code-for-token exchange happens in a small Cloud Function that holds the
// secret, never in the browser. Update this once that function is deployed (its Trigger URL is
// shown in Cloud Console after deploying functions/exchange-token/).
export const TOKEN_EXCHANGE_URL = 'https://exchange-token-395690152006.us-central1.run.app';

// drive.file: the app can only see/manage files it creates or that the user explicitly opens
// with it - never blanket access to the user's whole Drive. userinfo.email is just so the app
// can show "Connected as ..." - both are non-sensitive scopes (no extra Google verification).
export const GOOGLE_DRIVE_SCOPE = 'https://www.googleapis.com/auth/drive.file';
export const GOOGLE_SCOPES = `${GOOGLE_DRIVE_SCOPE} https://www.googleapis.com/auth/userinfo.email`;

export const TASKY_FOLDER_NAME = 'Tasky';
export const DEFAULT_DATA_FILE_NAME = 'Tasky.tasky';
