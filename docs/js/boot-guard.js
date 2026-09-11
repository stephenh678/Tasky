// Old-browser / boot-failure guard for Tasky Web. Deliberately a classic script written in plain
// ES5 (no const/let, arrows, template strings or modules): it has to run in exactly the browsers
// where everything else on this page can't.
//
// Every real script here is an ES module, and a module that uses one piece of syntax the browser
// doesn't know (ROADMAP: regex lookbehind in model.js was the live case - Safari < 16.4) fails at
// parse time, which silently fails the entire import graph. Nothing in app.js ever runs, so the
// sign-in screen's static "Loading…" button just sits there forever with no hint why. This script
// watches for that: app.js calls window.__taskyBootGuard.dismiss() once it's genuinely running,
// and if that hasn't happened by the time a module-level error is reported (or a generous
// timeout passes, for browsers that don't report module parse errors to window.onerror) the
// sign-in screen says what's wrong instead.
//
// Minimum supported browser is documented in README.md's "Tasky Web" section - keep the message
// below in sync with it.
(function () {
  var TIMEOUT_MS = 8000;
  var fired = false;
  var timer = null;

  function setText(id, text) {
    var node = document.getElementById(id);
    if (node) node.textContent = text;
  }

  function fail(detail) {
    if (fired || window.__taskyBootGuard.booted) return;
    fired = true;
    var btn = document.getElementById('signin-btn');
    if (btn) {
      btn.disabled = true;
      btn.textContent = "Tasky couldn't start";
    }
    setText(
      'signin-status',
      'This usually means the browser is too old. Tasky Web needs Safari / iOS 16.4 or newer, ' +
        'or a current version of Chrome, Edge or Firefox.' +
        (detail ? ' (' + detail + ')' : '')
    );
  }

  window.__taskyBootGuard = {
    booted: false,
    dismiss: function () {
      this.booted = true;
      if (timer) clearTimeout(timer);
      // A slow first load on a bad connection can outlast the timeout and then finish booting
      // anyway - app.js resets the button itself, but the stale message here would linger.
      if (fired) {
        fired = false;
        setText('signin-status', '');
      }
    }
  };

  // A module that fails to parse is reported here as a SyntaxError before anything else runs;
  // the message (e.g. "Invalid regular expression: invalid group specifier name") is worth
  // showing since it's the one clue a bug report would need.
  window.addEventListener('error', function (e) {
    fail(e && e.message ? String(e.message) : '');
  });

  timer = setTimeout(function () {
    fail('');
  }, TIMEOUT_MS);
})();
