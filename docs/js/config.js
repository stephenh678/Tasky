// Web OAuth client (separate from the desktop app's Desktop-type credential).
// Public by design - Web application client IDs are meant to be visible in client-side code.
export const GOOGLE_CLIENT_ID = '395690152006-c3he874j1gn548j74chkju1f741opfhi.apps.googleusercontent.com';

// Sign-in uses the Code + redirect flow (see auth.js) so there's never a popup window - but
// Google's token endpoint requires the client_secret for that exchange on a Web-application-type
// client, so the actual code-for-token exchange happens in a small Cloud Function that holds the
// secret, never in the browser. Update this once that function is deployed (its Trigger URL is
// shown in Cloud Console after deploying functions/exchange-token/).
export const TOKEN_EXCHANGE_URL = 'https://exchange-token-395690152006.us-central1.run.app';

// Silently mints a new access token from a server-held refresh token (see auth.js /
// functions/refresh-token/) so the user isn't redirected to Google every ~hour (ROADMAP.md #117).
// Same project/region as TOKEN_EXCHANGE_URL, so its Trigger URL follows the same predictable
// pattern - confirm against Cloud Console once functions/refresh-token/ is actually deployed.
export const TOKEN_REFRESH_URL = 'https://refresh-token-395690152006.us-central1.run.app';

// drive.file: the app can only see/manage files it creates or that the user explicitly opens
// with it - never blanket access to the user's whole Drive. userinfo.email/.profile are just so
// the app can show "Connected as ..." plus the account's name and picture - all three are
// non-sensitive scopes (no extra Google verification required).
export const GOOGLE_DRIVE_SCOPE = 'https://www.googleapis.com/auth/drive.file';
export const GOOGLE_SCOPES = `${GOOGLE_DRIVE_SCOPE} https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/userinfo.profile`;

export const TASKY_FOLDER_NAME = 'Tasky';
export const DEFAULT_DATA_FILE_NAME = 'Tasky.tasky';

// Shown in the sign-in splash screen and About Tasky Web dialog, alongside the auto-derived
// cache-bust build number (see app.js's versionString()). Deliberately NOT auto-derived itself -
// keep this in sync with TodoApp.csproj's <Version> whenever the desktop app's version is bumped,
// since Tasky Web ships inside the same tagged releases rather than on its own version track.
export const DESKTOP_VERSION = '1.1.3';
