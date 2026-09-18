// One builder for every JS-constructed modal in Tasky Web (Add Link, Save View, the confirm
// prompt, the signed-out reminder). Each of those used to hand-assemble the same overlay/card/
// heading/actions DOM with its own copy of the Escape / outside-click / focus wiring, so a fix to
// one (e.g. the danger-focuses-Cancel rule) had to be repeated in four places. Static-HTML modals
// (Shortcuts, Onboarding, About) are deliberately not built here - they're show/hide, not create/
// destroy, and their markup is easier to read in index.html - but they share trapFocus below.
//
// openDialog({ title, message, fields, actions, variant, dismissValue })
//   title        h2 text.
//   message      optional paragraph under the title.
//   fields       [{ key, label, type = 'text', placeholder = '', error }] - text inputs, rendered
//                in order; `error` is the message shown under the field when an action's
//                validate() rejects it.
//   actions      [{ label, value, primary, danger, submit, focus, validate }] - buttons, left to
//                right. `value` is what the promise resolves with when the button is pressed, or a
//                function of the field values (`{ key: trimmedString }`) returning that. A `submit`
//                action also fires on Enter and runs `validate(values)` first, which returns the key
//                of the field to flag (its `error` is shown and it's refocused) or null to proceed.
//                `focus: true` picks the initially focused button; otherwise the first field, else
//                the primary action, else the first action.
//   variant      'modal' (the wider .modal-card, for forms) or 'confirm' (the compact .confirm-card).
//   dismissValue what Escape / clicking the backdrop resolve with (default null).
//
// Resolves once, on the first button press or dismissal, and removes itself from the DOM.
let dialogSeq = 0;

export function openDialog({
  title,
  message = '',
  fields = [],
  actions,
  variant = 'modal',
  dismissValue = null,
}) {
  return new Promise((resolve) => {
    const isConfirm = variant === 'confirm';
    const overlay = document.createElement('div');
    overlay.className = isConfirm ? 'confirm-overlay' : 'modal-overlay';
    const card = document.createElement('div');
    card.className = isConfirm ? 'confirm-card' : 'modal-card link-modal-card';
    card.setAttribute('role', 'dialog');
    card.setAttribute('aria-modal', 'true');

    const heading = document.createElement('h2');
    heading.textContent = title;
    heading.id = `dialog-title-${++dialogSeq}`;
    card.setAttribute('aria-labelledby', heading.id);
    card.appendChild(heading);

    if (message) {
      const body = document.createElement('p');
      body.textContent = message;
      card.appendChild(body);
    }

    const inputs = new Map();
    const errors = new Map();
    for (const field of fields) {
      const label = document.createElement('label');
      label.className = 'link-modal-field';
      label.textContent = field.label;
      const input = document.createElement('input');
      input.type = field.type ?? 'text';
      input.placeholder = field.placeholder ?? '';
      label.appendChild(input);
      card.appendChild(label);
      inputs.set(field.key, input);
      if (field.error) {
        const errorMsg = document.createElement('p');
        errorMsg.className = 'link-modal-error hidden';
        errorMsg.textContent = field.error;
        card.appendChild(errorMsg);
        errors.set(field.key, errorMsg);
      }
    }

    const actionRow = document.createElement('div');
    actionRow.className = 'link-modal-actions';
    const buttons = [];
    let submitAction = null;
    for (const action of actions) {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = action.danger ? 'btn btn-ghost danger' : action.primary ? 'btn btn-primary' : 'btn btn-ghost';
      btn.textContent = action.label;
      btn.addEventListener('click', () => run(action));
      actionRow.appendChild(btn);
      buttons.push({ action, btn });
      if (action.submit) submitAction = action;
    }
    card.appendChild(actionRow);
    overlay.appendChild(card);
    document.body.appendChild(overlay);

    const initialFocus =
      buttons.find((b) => b.action.focus)?.btn ??
      inputs.values().next().value ??
      (buttons.find((b) => b.action.primary) ?? buttons[0])?.btn;
    const releaseFocus = trapFocus(card, { initialFocus });

    function readValues() {
      const values = {};
      for (const [key, input] of inputs) values[key] = input.value.trim();
      return values;
    }
    function close(result) {
      document.removeEventListener('keydown', onKeydown);
      overlay.remove();
      releaseFocus();
      resolve(result);
    }
    function run(action) {
      const values = readValues();
      if (action.validate) {
        const badKey = action.validate(values);
        if (badKey) {
          errors.get(badKey)?.classList.remove('hidden');
          inputs.get(badKey)?.focus();
          return;
        }
      }
      close(typeof action.value === 'function' ? action.value(values) : action.value);
    }
    function onKeydown(e) {
      if (e.key === 'Escape') {
        close(dismissValue);
      } else if (e.key === 'Enter' && submitAction) {
        // Enter on a focused button activates THAT button (natively, via its click) - hijacking it
        // here made Enter on "Cancel" submit the form instead.
        if (document.activeElement?.tagName === 'BUTTON' && card.contains(document.activeElement)) return;
        e.preventDefault();
        run(submitAction);
      }
    }
    document.addEventListener('keydown', onKeydown);
    overlay.addEventListener('click', (e) => {
      if (e.target === overlay) close(dismissValue);
    });
  });
}

// Modal focus management, shared by openDialog and the static modals (Shortcuts, Onboarding, the
// photo lightbox): moves focus into `container` (initialFocus, else its first focusable element),
// keeps Tab / Shift+Tab cycling inside it while it's open, and puts focus back where it was once
// the returned release function runs - so a keyboard or screen-reader user can't tab out behind
// the backdrop and isn't dropped at the top of the page when the modal closes. The listener is on
// the document (capture phase) rather than the container so a stray Tab from outside is pulled
// back in too.
const FOCUSABLE_SELECTOR =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

export function trapFocus(container, { initialFocus = null } = {}) {
  const previouslyFocused = document.activeElement;
  const focusables = () => [...container.querySelectorAll(FOCUSABLE_SELECTOR)].filter((el) => el.getClientRects().length > 0);

  const onKeydown = (e) => {
    if (e.key !== 'Tab') return;
    const items = focusables();
    if (items.length === 0) {
      e.preventDefault();
      return;
    }
    const first = items[0];
    const last = items[items.length - 1];
    const active = document.activeElement;
    const outside = !container.contains(active);
    if (e.shiftKey && (outside || active === first)) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && (outside || active === last)) {
      e.preventDefault();
      first.focus();
    }
  };
  document.addEventListener('keydown', onKeydown, true);

  if (initialFocus) initialFocus.focus();
  else if (!container.contains(document.activeElement)) focusables()[0]?.focus();

  return function release() {
    document.removeEventListener('keydown', onKeydown, true);
    if (previouslyFocused && previouslyFocused.isConnected && typeof previouslyFocused.focus === 'function' && previouslyFocused !== document.body) {
      previouslyFocused.focus();
    }
  };
}
