// MyRPA recorder (ADR-0039). Added only to the pages of a recording session, never to workflow runs. It watches what the
// user does and reports each action, with selector candidates in the ADR-0038 order, through one binding. It never
// changes the page. The host validates every message and re-checks each selector with the browser.
(() => {
  if (window !== window.top) {
    return; // the main frame only: selectors are resolved from the page
  }

  const binding = '__MYRPA_BINDING__';
  const send = (message) => {
    try {
      window[binding](JSON.stringify(message));
    } catch {
      // the page is going away
    }
  };

  const ids = new WeakMap();
  let next = 0;
  const elementId = (element) => {
    if (!ids.has(element)) {
      ids.set(element, ++next);
    }

    return ids.get(element);
  };

  const norm = (text) => (text || '').replace(/\s+/g, ' ').trim();
  const short = (text, max) => (text.length > max ? text.slice(0, max) : text);
  const looksGenerated = (value) => /\d{4,}|[0-9a-f]{10,}|^[0-9]|:|\s/i.test(value) || value.length > 40;
  const textTypes = ['', 'text', 'email', 'password', 'search', 'tel', 'url', 'number'];

  const implicitRole = (e) => {
    const explicit = e.getAttribute('role');
    if (explicit) {
      return explicit.split(' ')[0];
    }

    const tag = e.tagName.toLowerCase();
    const type = (e.getAttribute('type') || '').toLowerCase();
    if (tag === 'button' || (tag === 'input' && ['button', 'submit', 'reset', 'image'].includes(type))) return 'button';
    if (tag === 'a' && e.hasAttribute('href')) return 'link';
    if (tag === 'input' && type === 'checkbox') return 'checkbox';
    if (tag === 'input' && type === 'radio') return 'radio';
    if ((tag === 'input' && textTypes.includes(type) && type !== 'password') || tag === 'textarea') return 'textbox';
    if (tag === 'select') return e.multiple || e.size > 1 ? 'listbox' : 'combobox';
    if (/^h[1-6]$/.test(tag)) return 'heading';
    if (tag === 'img' && e.getAttribute('alt')) return 'img';
    return '';
  };

  const labelOf = (e) => {
    const aria = e.getAttribute('aria-label');
    if (aria) return norm(aria);
    const by = e.getAttribute('aria-labelledby');
    if (by) {
      const text = by.split(/\s+/).map((id) => norm(document.getElementById(id)?.textContent)).join(' ');
      if (norm(text)) return norm(text);
    }

    if (e.id) {
      const label = document.querySelector(`label[for="${CSS.escape(e.id)}"]`);
      if (label) return norm(label.textContent);
    }

    const wrapping = e.closest('label');
    return wrapping ? norm(wrapping.textContent) : '';
  };

  const accessibleName = (e) => {
    const label = labelOf(e);
    if (label) return label;
    const tag = e.tagName.toLowerCase();
    if (tag === 'input' && ['button', 'submit', 'reset'].includes((e.getAttribute('type') || '').toLowerCase())) return norm(e.value);
    if (tag === 'img') return norm(e.getAttribute('alt'));
    if (['button', 'a', 'summary'].includes(tag) || /^h[1-6]$/.test(tag) || e.getAttribute('role')) return norm(e.textContent);
    return norm(e.getAttribute('title'));
  };

  const cssCount = (css) => {
    try {
      return document.querySelectorAll(css).length;
    } catch {
      return 0;
    }
  };

  // The script's own uniqueness for strategies CSS cannot express (role and name, label, exact text): the host uses it
  // only when the page has already changed (a click that navigated away) and the browser can no longer count.
  const only = (match) => {
    let count = 0;
    for (const e of document.body ? document.body.querySelectorAll('*') : []) {
      if (match(e) && ++count > 1) return false;
    }

    return count === 1;
  };

  const attrCss = (name, value) => `[${name}="${value.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"]`;

  const cssPath = (e) => {
    if (e.id && !looksGenerated(e.id) && cssCount('#' + CSS.escape(e.id)) === 1) {
      return '#' + CSS.escape(e.id);
    }

    const parts = [];
    for (let node = e; node && node.nodeType === 1 && node !== document.body && parts.length < 5; node = node.parentElement) {
      const tag = node.tagName.toLowerCase();
      const same = node.parentElement ? [...node.parentElement.children].filter((c) => c.tagName === node.tagName) : [node];
      parts.unshift(same.length > 1 ? `${tag}:nth-of-type(${same.indexOf(node) + 1})` : tag);
      if (node.id && !looksGenerated(node.id)) {
        parts[0] = '#' + CSS.escape(node.id);
        break;
      }
    }

    return parts.join(' > ');
  };

  const xpath = (e) => {
    const parts = [];
    for (let node = e; node && node.nodeType === 1; node = node.parentElement) {
      const same = node.parentElement ? [...node.parentElement.children].filter((c) => c.tagName === node.tagName) : [node];
      parts.unshift(`${node.tagName.toLowerCase()}[${same.indexOf(node) + 1}]`);
    }

    return '/' + parts.join('/');
  };

  // ADR-0038 §3: test ids, role and name, label, stable attributes, exact text, CSS path, XPath. `unique` is this
  // script's own count; the host prefers the browser's count when the page is still there.
  const candidates = (e) => {
    const list = [];
    const add = (selector, unique) => {
      if (selector && !selector.includes(' >> ') && selector.length <= 512 && !list.some((c) => c.selector === selector)) {
        list.push({ selector, unique });
      }
    };

    for (const name of ['data-testid', 'data-test', 'data-qa']) {
      const value = e.getAttribute(name);
      if (value) add(name === 'data-testid' ? `testid=${value}` : `attr=${name}=${value}`, cssCount(attrCss(name, value)) === 1);
    }

    const role = implicitRole(e);
    const name = short(accessibleName(e), 80);
    if (role && name) add(`role=${role}|${name}`, only((o) => implicitRole(o) === role && short(accessibleName(o), 80) === name));
    const label = labelOf(e);
    const control = (o) => ['input', 'select', 'textarea'].includes(o.tagName.toLowerCase());
    if (label && control(e)) add(`label=${short(label, 80)}`, only((o) => control(o) && labelOf(o) === label));
    for (const attribute of ['name', 'placeholder', 'aria-label', 'title', 'alt']) {
      const value = e.getAttribute(attribute);
      if (value && value.length <= 80) add(`attr=${attribute}=${value}`, cssCount(attrCss(attribute, value)) === 1);
    }

    if (e.tagName === 'A' && e.getAttribute('href') && e.getAttribute('href').length <= 120) {
      add(`attr=href=${e.getAttribute('href')}`, cssCount(attrCss('href', e.getAttribute('href'))) === 1);
    }

    if (e.id && !looksGenerated(e.id)) add(`attr=id=${e.id}`, cssCount(attrCss('id', e.id)) === 1);
    const text = norm(e.textContent);
    if (text && text.length <= 40 && e.children.length === 0) add(`text="${text}"`, only((o) => o.children.length === 0 && norm(o.textContent) === text));
    const css = cssPath(e);
    add(`css=${css}`, cssCount(css) === 1);
    add(`xpath=${xpath(e)}`, true);
    return list.slice(0, 12);
  };

  const describe = (e) => {
    const role = implicitRole(e) || e.tagName.toLowerCase();
    const name = short(accessibleName(e), 60);
    return name ? `${role} "${name}"` : role;
  };

  const report = (kind, e, extra) => send({ kind, element: elementId(e), candidates: candidates(e), label: describe(e), ...extra });

  const clickable = 'button, a, input, select, textarea, summary, label, [role], [onclick], [tabindex]';
  document.addEventListener(
    'click',
    (event) => {
      if (!event.isTrusted || !(event.target instanceof Element)) return; // a label's synthetic click on its control is not the user's
      const target = event.target.closest(clickable) || event.target;
      const tag = target.tagName.toLowerCase();
      const type = (target.getAttribute('type') || '').toLowerCase();
      // Typing, choosing options and choosing files are recorded as such, not as clicks on the field.
      if (tag === 'select' || tag === 'textarea' || (tag === 'input' && (textTypes.includes(type) || type === 'file'))) return;
      report('click', target, {});
    },
    true,
  );

  document.addEventListener(
    'input',
    (event) => {
      const e = event.target;
      if (!(e instanceof HTMLInputElement || e instanceof HTMLTextAreaElement)) return;
      const type = (e.getAttribute('type') || '').toLowerCase();
      if (e instanceof HTMLInputElement && !textTypes.includes(type)) return;
      const secret = type === 'password';
      report('type', e, secret ? { secret: true } : { text: e.value });
    },
    true,
  );

  document.addEventListener(
    'change',
    (event) => {
      const e = event.target;
      if (e instanceof HTMLSelectElement) {
        report('select', e, { values: [...e.selectedOptions].map((o) => o.value) });
      } else if (e instanceof HTMLInputElement && (e.getAttribute('type') || '').toLowerCase() === 'file') {
        report('upload', e, { values: [...(e.files || [])].map((f) => f.name) });
      }
    },
    true,
  );

  // Enter in a field submits its form: the browser clicks the form's submit button (a trusted click), recorded above.
})();
