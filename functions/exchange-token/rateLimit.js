// Best-effort, in-memory per-instance rate limit (ROADMAP.md #137). Deliberately simple rather
// than a Firestore/Redis-backed distributed limiter: this function's whole reason to exist is
// "hold one secret server-side," not "be a rate-limiting service," and a real distributed limiter
// is what Cloud Armor / API Gateway are for (an infra-level setup outside this repo's control -
// see the review's own note that Function source review can't verify infra, only what's here).
//
// What this DOES catch: a single misbehaving/looping client hammering this function from one
// Cloud Functions instance, which is the realistic threat for a function like this (a
// stolen/leaked authorization code or session_id is still one-shot/short-lived on Google's side
// regardless of how many times it's retried here).
//
// What this does NOT catch: a distributed attack spread across many IPs, or an attacker who
// happens to land on a fresh instance (each instance's counters start empty, and idle instances
// get recycled - Cloud Functions can and does run more than one instance concurrently under load).
// That's an accepted gap, not an oversight - closing it needs the infra-level tooling above.
const WINDOW_MS = 60_000;
const MAX_REQUESTS_PER_WINDOW = 20;

const hits = new Map(); // key -> array of request timestamps within the current window

function clientKey(req) {
  // Cloud Functions (2nd gen) runs behind Google's front end, which sets x-forwarded-for to the
  // real client IP as the first entry - req.ip alone is the proxy's own address here, not useful.
  const forwarded = req.headers['x-forwarded-for'];
  return (typeof forwarded === 'string' && forwarded.split(',')[0].trim()) || req.ip || 'unknown';
}

function checkRateLimit(req) {
  const key = clientKey(req);
  const now = Date.now();
  const timestamps = (hits.get(key) || []).filter((t) => now - t < WINDOW_MS);
  timestamps.push(now);
  hits.set(key, timestamps);

  // Prevents unbounded growth of the map itself across the instance's lifetime - cheap since this
  // only runs on an actual request, not on a timer.
  if (hits.size > 5000) {
    for (const [k, v] of hits) {
      if (v.every((t) => now - t >= WINDOW_MS)) hits.delete(k);
    }
  }

  return timestamps.length <= MAX_REQUESTS_PER_WINDOW;
}

module.exports = { checkRateLimit };
