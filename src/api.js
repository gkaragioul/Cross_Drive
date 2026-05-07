import { BACKEND_URL } from './config';

async function fetchJson(url, options = {}) {
  const res = await fetch(url, options);
  const text = await res.text();
  try {
    return JSON.parse(text);
  } catch {
    throw new Error('Backend returned invalid response');
  }
}

async function postJson(url, body, options = {}) {
  return fetchJson(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
    ...options,
  });
}

export async function fetchDrives() {
  const data = await fetchJson(`${BACKEND_URL}/api/drives`);
  if (data.error) {
    return { drives: data.mockData || [], error: data.error };
  }
  return { drives: Array.isArray(data) ? data : [], error: null };
}

export async function fetchStatus() {
  return fetchJson(`${BACKEND_URL}/api/status`);
}

export async function fetchRuntimeConfig() {
  return fetchJson(`${BACKEND_URL}/api/runtime/config`);
}

export async function fetchPreflight() {
  return fetchJson(`${BACKEND_URL}/api/preflight/check`);
}

export async function fixPreflight() {
  return postJson(`${BACKEND_URL}/api/preflight/fix`, {});
}

export async function fetchNativeStatus() {
  try {
    return await fetchJson(`${BACKEND_URL}/api/native/status`);
  } catch {
    return { available: false };
  }
}

export async function fetchLogs() {
  return fetchJson(`${BACKEND_URL}/api/logs`);
}

export async function postLog(message, type = 'info') {
  await postJson(`${BACKEND_URL}/api/logs`, { message, type });
}

export async function mountDrive(id, password = '') {
  const res = await fetch(`${BACKEND_URL}/api/mount`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ id, password }),
  });
  const text = await res.text();
  let result;
  try {
    result = JSON.parse(text);
  } catch {
    throw new Error('Backend returned an invalid response');
  }
  if (!res.ok) {
    const err = new Error(result.error || 'Mount failed');
    err.result = result;
    throw err;
  }
  return result;
}

export async function unmountDrive(id) {
  const res = await fetch(`${BACKEND_URL}/api/unmount`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ id }),
  });
  const result = await res.json();
  if (!res.ok) {
    const err = new Error(result.error || 'Unmount failed');
    err.result = result;
    throw err;
  }
  return result;
}

export async function openInExplorer(pathStr) {
  await postJson(`${BACKEND_URL}/api/open`, { path: pathStr });
}

export async function generateSupportBundle() {
  return fetchJson(`${BACKEND_URL}/api/support/bundle`);
}

export async function checkForUpdate(auto = false) {
  return fetchJson(`${BACKEND_URL}/api/update/check?auto=${auto ? 1 : 0}`);
}

export async function startUpdateDownload(downloadUrl, sha256, version) {
  return postJson(`${BACKEND_URL}/api/update/download`, { downloadUrl, sha256, version });
}

export async function fetchUpdateProgress() {
  return fetchJson(`${BACKEND_URL}/api/update/progress`);
}

export async function launchUpdateInstaller(installerPath, version) {
  return postJson(`${BACKEND_URL}/api/update/launch`, { installerPath, version });
}

export async function dismissUpdate(version) {
  return postJson(`${BACKEND_URL}/api/update/dismiss`, { version });
}
