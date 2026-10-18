const config = window.TILSYNSVAKT_CONFIG ?? {};
const apiBaseUrl = (config.apiBaseUrl ?? "").replace(/\/$/, "");
const requestTimeoutMs = 15000;
const storageKeys = {
  username: "tilsynsvakt.admin.username",
  password: "tilsynsvakt.admin.password",
};

const state = {
  guards: [],
  allGuards: [],
  duties: [],
  showPast: false,
  loadVersion: 0,
};

const authForm = document.querySelector("#auth-form");
const panel = document.querySelector("#admin-panel");
const notice = document.querySelector("#notice");
const logoutButton = document.querySelector("#logout");
const seasonForm = document.querySelector("#season-form");
const dutiesElement = document.querySelector("#duties");
const totalsElement = document.querySelector("#totals");
const seasonRangeElement = document.querySelector("#season-range");
const seasonInputs = document.querySelectorAll("input[name=season]");
const yearInput = document.querySelector("#year");
const usernameInput = document.querySelector("#username");
const passwordInput = document.querySelector("#password");
const tabs = [...document.querySelectorAll('.admin-tabs [role="tab"]')];
const guardListElement = document.querySelector("#guard-list");
const guardCreateForm = document.querySelector("#guard-create-form");

function selectTab(hash, focus = false) {
  const selectedId = ["#vakter", "#musikanter"].includes(hash) ? hash.slice(1) : "summering";
  tabs.forEach(tab => {
    const selected = tab.getAttribute("aria-controls") === selectedId;
    tab.setAttribute("aria-selected", String(selected));
    tab.tabIndex = selected ? 0 : -1;
    document.getElementById(tab.getAttribute("aria-controls")).hidden = !selected;
    if (selected && focus) tab.focus();
  });
}

tabs.forEach((tab, index) => {
  tab.addEventListener("click", () => {
    window.location.hash = tab.getAttribute("aria-controls");
    selectTab(window.location.hash);
  });
  tab.addEventListener("keydown", event => {
    let nextIndex;
    if (event.key === "ArrowRight") nextIndex = (index + 1) % tabs.length;
    if (event.key === "ArrowLeft") nextIndex = (index + tabs.length - 1) % tabs.length;
    if (event.key === "Home") nextIndex = 0;
    if (event.key === "End") nextIndex = tabs.length - 1;
    if (nextIndex === undefined) return;
    event.preventDefault();
    window.location.hash = tabs[nextIndex].getAttribute("aria-controls");
    selectTab(window.location.hash, true);
  });
});
window.addEventListener("hashchange", () => selectTab(window.location.hash));
selectTab(window.location.hash);

const defaultSeason = getDefaultSeason(new Date());
seasonInputs.forEach(input => { input.checked = input.value === defaultSeason.season; });
yearInput.value = defaultSeason.year;
usernameInput.value = sessionStorage.getItem(storageKeys.username) ?? "";
passwordInput.value = sessionStorage.getItem(storageKeys.password) ?? "";

authForm.addEventListener("submit", async event => {
  event.preventDefault();
  rememberCredentials();
  await loadAdminDataSafely();
});

logoutButton.addEventListener("click", () => {
  state.loadVersion++;
  sessionStorage.removeItem(storageKeys.username);
  sessionStorage.removeItem(storageKeys.password);
  passwordInput.value = "";
  setLoggedIn(false);
  renderNotice("Du er logget ut.", "success");
});

document.querySelector("#year-prev").addEventListener("click", () => stepYear(-1));
document.querySelector("#year-next").addEventListener("click", () => stepYear(1));
seasonForm.addEventListener("change", () => loadAdminDataSafely());

guardCreateForm.addEventListener("submit", async event => {
  event.preventDefault();
  const form = event.currentTarget;
  const input = readGuardForm(form);
  if (!input) return;
  const saved = await saveGuard("/api/admin/guards", "POST", input, "Musikanten er lagt til.");
  if (saved) form.reset();
});

seasonForm.addEventListener("submit", async event => {
  event.preventDefault();
  await loadAdminDataSafely();
});

if (hasCredentials()) {
  loadAdminDataSafely();
}

function setLoggedIn(loggedIn) {
  panel.hidden = !loggedIn;
  authForm.hidden = loggedIn;
}

function selectedSeason() {
  return [...seasonInputs].find(input => input.checked)?.value ?? "autumn";
}

function stepYear(delta) {
  yearInput.value = Number(yearInput.value) + delta;
  return loadAdminDataSafely();
}

async function loadAdminDataSafely() {
  const version = ++state.loadVersion;
  try {
    await loadAdminData(version);
  } catch (error) {
    if (version !== state.loadVersion) return;
    totalsElement.innerHTML = '<p class="empty-state">Kunne ikke laste summeringen. Prøv igjen.</p>';
    dutiesElement.innerHTML = '<p class="empty-state">Kunne ikke laste vaktene. Prøv igjen.</p>';
    guardListElement.innerHTML = '<p class="empty-state">Kunne ikke laste musikantene. Prøv igjen.</p>';
    if (error.message === "Unauthorized") {
      setLoggedIn(false);
      renderNotice("Ugyldig brukernavn eller passord.", "error");
    } else {
      renderNotice(error.name === "TimeoutError"
        ? "Forespørselen tok for lang tid. Prøv igjen."
        : "Kunne ikke laste administrasjonssiden. Prøv igjen.", "error");
    }
  }
}

async function loadAdminData(version) {
  if (!hasCredentials()) {
    setLoggedIn(false);
    renderNotice("Oppgi brukernavn og passord for å åpne administrasjonssiden.", "warm");
    return;
  }

  renderNotice("Laster …");
  totalsElement.innerHTML = '<p class="loading">Laster summering …</p>';
  dutiesElement.innerHTML = '<p class="loading">Laster vakter …</p>';
  guardListElement.innerHTML = '<p class="loading">Laster musikanter …</p>';
  const [guards, dutyList] = await Promise.all([
    apiFetch("/api/admin/guards"),
    apiFetch(`/api/admin/duties?season=${encodeURIComponent(selectedSeason())}&year=${encodeURIComponent(yearInput.value)}`),
  ]);
  if (version !== state.loadVersion) return;

  state.allGuards = sortGuardsByName(guards);
  state.guards = state.allGuards.filter(guard => guard.active !== false);
  state.duties = dutyList.duties;
  setLoggedIn(true);
  renderTotals(dutyList.totals);
  renderDuties(dutyList.duties);
  renderGuardList();
  seasonRangeElement.textContent = `${formatDate(dutyList.from)}–${formatDate(dutyList.to)}`;
  renderNotice("");
}

async function apiFetch(path, options = {}) {
  const controller = new AbortController();
  let timeoutId;
  try {
    return await Promise.race([
      fetchResponse(path, options, controller.signal),
      new Promise((resolve, reject) => {
        timeoutId = setTimeout(() => {
          const error = new Error("Forespørselen tok for lang tid. Prøv igjen.");
          error.name = "TimeoutError";
          reject(error);
          controller.abort();
        }, requestTimeoutMs);
      }),
    ]);
  } finally {
    clearTimeout(timeoutId);
  }
}

async function fetchResponse(path, options, signal) {
  const response = await fetch(`${apiBaseUrl}${path}`, {
    ...options,
    signal,
    headers: {
      "Content-Type": "application/json",
      "Authorization": getAuthorizationHeader(),
      ...(options.headers ?? {}),
    },
  });

  if (response.status === 401) {
    throw new Error("Unauthorized");
  }

  if (!response.ok) {
    const problem = await readProblem(response);
    throw new Error(problem?.detail ?? "Ukjent feil");
  }

  if (response.status === 204) {
    return null;
  }

  return await response.json();
}

function renderTotals(totals) {
  if (!totals.length) {
    totalsElement.innerHTML = '<p class="empty-state">Ingen vakter er tildelt i valgt sesong.</p>';
    return;
  }

  totalsElement.innerHTML = `
    <table class="week-table admin-total-table" aria-labelledby="totals-heading">
      <thead>
        <tr>
          <th scope="col">Tilsynsvakt</th>
          <th scope="col">Timer</th>
          <th scope="col">Vakter</th>
          <th scope="col">Utsjekk</th>
        </tr>
      </thead>
      <tbody>
        ${totals.map(total => `
          <tr>
            <th scope="row">${escapeHtml(total.guard.name)}</th>
            <td>${formatHours(total.totalHours)}</td>
            <td>${total.dutyCount} ${total.dutyCount === 1 ? "vakt" : "vakter"}</td>
            <td class="muted">${total.dutiesWithoutCheckOut === 0 ? "Alle registrert" : `${total.dutiesWithoutCheckOut} ${total.dutiesWithoutCheckOut === 1 ? "vakt" : "vakter"} bruker planlagt slutt`}</td>
          </tr>
        `).join("")}
      </tbody>
    </table>
  `;
}

function renderDuties(allDuties) {
  const duties = allDuties.filter(duty => duty.dayOfWeek !== "monday");
  if (!duties.length) {
    dutiesElement.innerHTML = '<p class="empty-state">Fant ingen vakter for valgt sesong.</p>';
    return;
  }

  const today = osloTodayIso();
  const pastCount = duties.filter(duty => duty.date < today).length;
  const visible = state.showPast ? duties : duties.filter(duty => duty.date >= today);
  const toggle = pastCount
    ? `<button id="toggle-past" class="button button-secondary admin-past-toggle" type="button" aria-expanded="${state.showPast}">${state.showPast ? "Skjul tidligere vakter" : `Vis tidligere vakter (${pastCount})`}</button>`
    : "";
  const empty = visible.length ? "" : '<p class="empty-state">Ingen kommende vakter i valgt sesong.</p>';

  dutiesElement.innerHTML = toggle + empty + visible.map(duty => {
    const selectId = `guard-${duty.date}`;
    const timeId = `checkout-${duty.date}`;
    const assignedListed = state.guards.some(guard => guard.id === duty.guard?.id);
    const options = [`<option value="">Ledig</option>`]
      .concat(state.guards.map(guard => `
        <option value="${guard.id}"${duty.guard?.id === guard.id ? " selected" : ""}>${escapeHtml(guard.name)}</option>
      `))
      // Keep an assigned inactive guard selectable so saving does not clear the duty.
      .concat(duty.guard && !assignedListed ? [`<option value="${duty.guard.id}" selected>${escapeHtml(duty.guard.name)}</option>`] : [])
      .join("");

    return `
      <article class="admin-duty-card">
        <div class="shift-card-header">
          <div class="shift-date">
            <strong>${formatDate(duty.date)}</strong>
            <span>${translateDay(duty.dayOfWeek)}</span>
          </div>
          <span class="status-tag ${duty.guard ? "status-taken" : "status-open"}">${duty.guard ? "Tatt" : "Ledig"}</span>
        </div>
        <p class="shift-guard">${duty.guard ? escapeHtml(duty.guard.name) : "Ingen tilsynsvakt valgt"}</p>
        <div class="admin-meta">
          <span>${escapeHtml(duty.scheduledStart)}–${escapeHtml(duty.endTime)}</span>
          <span>${formatHours(duty.durationHours)} timer</span>
          <span class="badge ${duty.hasRecordedCheckOut ? "badge-actual" : "badge-planned"}">${duty.hasRecordedCheckOut ? "Registrert utsjekk" : "Planlagt slutt"}</span>
        </div>
        <form class="admin-duty-form" data-date="${duty.date}">
          <label for="${selectId}">Tilsynsvakt (musikantens etternavn)</label>
          <select id="${selectId}" name="guardId">${options}</select>
          <label for="${timeId}">Utsjekk (hh:mm)</label>
          <input id="${timeId}" name="checkOutTime" type="time" step="1800" value="${duty.hasRecordedCheckOut ? escapeHtml(duty.endTime) : ""}">
          <div class="shift-actions">
            <button class="button button-primary" type="submit">Lagre</button>
          </div>
        </form>
      </article>
    `;
  }).join("");

  dutiesElement.querySelector("#toggle-past")?.addEventListener("click", () => {
    state.showPast = !state.showPast;
    renderDuties(state.duties);
    dutiesElement.querySelector("#toggle-past")?.focus();
  });

  dutiesElement.querySelectorAll(".admin-duty-form").forEach(form => {
    form.addEventListener("submit", async event => {
      event.preventDefault();
      const target = event.currentTarget;
      const formData = new FormData(target);
      const selectedGuard = formData.get("guardId");
      const guardId = selectedGuard ? Number(selectedGuard) : null;
      const checkOutTime = formData.get("checkOutTime")?.toString().trim() || null;

      try {
        await apiFetch(`/api/admin/duties/${target.dataset.date}`, {
          method: "PUT",
          body: JSON.stringify({
            guardId,
            checkOutTime: guardId ? checkOutTime : null,
          }),
        });
        await loadAdminDataSafely();
      } catch (error) {
        if (error.message === "Unauthorized") setLoggedIn(false);
        renderNotice(error.message === "Unauthorized"
          ? "Ugyldig brukernavn eller passord."
          : "Kunne ikke lagre vakten. Prøv igjen.", "error");
      }
    });
  });
}

function sortGuardsByName(guards) {
  return [...guards].sort((a, b) => a.name.localeCompare(b.name, "nb", { sensitivity: "base" }));
}

function renderGuardList() {
  if (!state.allGuards.length) {
    guardListElement.innerHTML = '<p class="empty-state">Ingen musikanter er registrert ennå.</p>';
    return;
  }

  guardListElement.innerHTML = state.allGuards.map(guard => {
    const nameId = `guard-name-${guard.id}`;
    const phoneId = `guard-phone-${guard.id}`;
    const activeId = `guard-active-${guard.id}`;
    return `
      <form class="admin-duty-card admin-guard-form" data-id="${guard.id}" novalidate>
        <label for="${nameId}">Etternavn (musikant)</label>
        <input id="${nameId}" name="name" type="text" autocomplete="off" required minlength="2" maxlength="80" value="${escapeHtml(guard.name)}" aria-describedby="${nameId}-help">
        <p id="${nameId}-help" class="small-copy muted">Bruk kun musikantens etternavn – ikke foreldrenes navn.</p>
        <label for="${phoneId}">Telefon (forelder)</label>
        <input id="${phoneId}" name="phone" type="tel" autocomplete="off" inputmode="tel" required value="${escapeHtml(guard.phone ?? "")}">
        <label class="admin-check" for="${activeId}"><input id="${activeId}" name="active" type="checkbox"${guard.active !== false ? " checked" : ""}> Aktiv i navnelisten</label>
        <div class="shift-actions">
          <button class="button button-primary" type="submit">Lagre</button>
        </div>
      </form>
    `;
  }).join("");

  guardListElement.querySelectorAll(".admin-guard-form").forEach(form => {
    form.addEventListener("submit", async event => {
      event.preventDefault();
      const target = event.currentTarget;
      const input = readGuardForm(target);
      if (!input) return;
      await saveGuard(`/api/admin/guards/${target.dataset.id}`, "PUT",
        { ...input, active: target.elements.active.checked }, "Musikanten er oppdatert.");
    });
  });
}

function readGuardForm(form) {
  const nameInput = form.elements.name;
  const phoneInput = form.elements.phone;
  const name = nameInput.value.trim().replace(/\s+/g, " ");
  const phone = phoneInput.value.trim();
  nameInput.value = name;
  if (name.length < 2 || name.length > 80) {
    renderNotice("Skriv musikantens etternavn (2–80 tegn).", "error");
    nameInput.focus();
    return null;
  }
  if (!phone) {
    renderNotice("Skriv telefonnummeret til forelderen.", "error");
    phoneInput.focus();
    return null;
  }
  return { name, phone };
}

async function saveGuard(path, method, body, successMessage) {
  try {
    await apiFetch(path, { method, body: JSON.stringify(body) });
    await loadAdminDataSafely();
    renderNotice(successMessage, "success");
    return true;
  } catch (error) {
    if (error.message === "Unauthorized") setLoggedIn(false);
    renderNotice(error.message === "Unauthorized"
      ? "Ugyldig brukernavn eller passord."
      : `Kunne ikke lagre musikanten. ${error.message}`, "error");
    return false;
  }
}

function rememberCredentials() {
  sessionStorage.setItem(storageKeys.username, usernameInput.value.trim());
  sessionStorage.setItem(storageKeys.password, passwordInput.value);
}

function hasCredentials() {
  return Boolean((sessionStorage.getItem(storageKeys.username) ?? "").trim())
    && Boolean(sessionStorage.getItem(storageKeys.password));
}

function getAuthorizationHeader() {
  const username = sessionStorage.getItem(storageKeys.username) ?? "";
  const password = sessionStorage.getItem(storageKeys.password) ?? "";
  return `Basic ${encodeBase64(`${username}:${password}`)}`;
}

function encodeBase64(value) {
  const bytes = new TextEncoder().encode(value);
  let binary = "";
  bytes.forEach(byte => {
    binary += String.fromCharCode(byte);
  });
  return btoa(binary);
}

function renderNotice(message, tone = "") {
  notice.textContent = message;
  notice.hidden = !message;
  notice.className = `notice${tone ? ` notice-${tone}` : ""}`;
}

async function readProblem(response) {
  try {
    return await response.json();
  } catch {
    return null;
  }
}

function osloTodayIso() {
  return new Intl.DateTimeFormat("en-CA", { timeZone: "Europe/Oslo", year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
}

function formatDate(isoDate) {
  const [year, month, day] = isoDate.split("-");
  return `${day}.${month}.${year}`;
}

function formatHours(value) {
  return value.toLocaleString("nb-NO", { minimumFractionDigits: 1, maximumFractionDigits: 1 });
}

function translateDay(day) {
  return {
    monday: "Mandag",
    tuesday: "Tirsdag",
    wednesday: "Onsdag",
    thursday: "Torsdag",
  }[day] ?? day;
}

function getDefaultSeason(now) {
  const month = now.getMonth() + 1;
  if (month >= 8) {
    return { season: "autumn", year: now.getFullYear() };
  }

  return { season: "spring", year: now.getFullYear() };
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}
