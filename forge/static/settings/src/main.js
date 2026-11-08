import { invokeRemote } from '@forge/bridge';

// 1) Ask the backend (FIT-authenticated via Forge, identifies the signed-in user) for this user's pending requests.
//    Each item carries a one-time, job-bound ticket.
// 2) Post the secret DIRECTLY to the backend with that ticket (declared in permissions.external.fetch.client),
//    so passwords never pass through Rovo chat or Forge functions.

const app = document.getElementById('app');

function el(tag, attrs = {}, children = []) {
  const node = document.createElement(tag);
  Object.entries(attrs).forEach(([k, v]) => {
    if (k === 'text') node.textContent = v;
    else node.setAttribute(k, v);
  });
  children.forEach((c) => node.appendChild(c));
  return node;
}

async function loadPending() {
  const response = await invokeRemote({ path: '/forge/credentials/pending', method: 'POST', body: {} });
  // invokeRemote resolves with { status, headers, body } — tolerate a plain body as well.
  const body = response && response.body !== undefined ? response.body : response;
  return typeof body === 'string' ? JSON.parse(body) : body;
}

async function submit(baseUrl, item, path, payload, message) {
  const res = await fetch(`${baseUrl.replace(/\/$/, '')}/api/jobs/${item.jobId}/${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Submission-Ticket': item.ticket },
    body: JSON.stringify(payload),
    credentials: 'omit'
  });
  if (res.ok) {
    message.className = 'msg ok';
    message.textContent = 'Received securely. Reply "continue" in the agent conversation in Jira.';
    return true;
  }
  message.className = 'msg err';
  message.textContent = res.status === 401
    ? 'This form expired. Reload the page and try again.'
    : res.status === 409 ? 'The investigation is no longer waiting for this input.' : 'Submission failed.';
  return false;
}

function credentialsForm(baseUrl, item) {
  const user = el('input', { type: 'text', autocomplete: 'username' });
  const pass = el('input', { type: 'password', autocomplete: 'current-password' });
  const remember = el('input', { type: 'checkbox' });
  const message = el('div', { class: 'msg' });
  const button = el('button', { text: `Send credentials for ${item.environmentName || item.environmentId}` });
  button.onclick = async () => {
    if (!user.value || !pass.value) {
      message.className = 'msg err';
      message.textContent = 'Enter username and password.';
      return;
    }
    button.disabled = true;
    const ok = await submit(baseUrl, item, 'credentials',
      { environmentId: item.environmentId, username: user.value, password: pass.value, remember: remember.checked }, message);
    pass.value = '';
    button.disabled = ok;
  };
  return el('div', { class: 'card' }, [
    el('strong', { text: `${item.issueKey} — sign in to ${item.environmentName || item.environmentId}` }),
    el('label', { text: 'Username' }), user,
    el('label', { text: 'Password' }), pass,
    el('div', { class: 'row' }, [remember, el('span', { text: `Remember securely for ${item.environmentId} (encrypted, only for you)` })]),
    button, message
  ]);
}

function mfaForm(baseUrl, item) {
  const code = el('input', { type: 'password', autocomplete: 'one-time-code', inputmode: 'numeric' });
  const message = el('div', { class: 'msg' });
  const button = el('button', { text: 'Send verification code' });
  button.onclick = async () => {
    button.disabled = true;
    const ok = await submit(baseUrl, item, 'mfa-code', { code: code.value }, message);
    code.value = '';
    button.disabled = ok;
  };
  return el('div', { class: 'card' }, [
    el('strong', { text: `${item.issueKey} — verification code for ${item.environmentName || item.environmentId}` }),
    el('label', { text: 'Code' }), code, button, message
  ]);
}

(async () => {
  try {
    const data = await loadPending();
    app.textContent = '';
    if (!data || !data.items || data.items.length === 0) {
      app.textContent = 'No investigation is waiting for your input.';
      return;
    }
    data.items.forEach((item) =>
      app.appendChild(item.kind === 'mfa-code' ? mfaForm(data.submitBaseUrl, item) : credentialsForm(data.submitBaseUrl, item)));
  } catch (e) {
    app.textContent = 'Could not load pending requests. Try again later.';
  }
})();
