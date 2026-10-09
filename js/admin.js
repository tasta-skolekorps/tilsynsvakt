const config = window.TILSYNSVAKT_CONFIG ?? {};
const apiBaseUrl = (config.apiBaseUrl ?? "").replace(/\/$/, "");
const requestTimeoutMs = 15000;
const storageKeys = {
  username: "tilsynsvakt.admin.username",
  password: "tilsynsvakt.admin.password",
};

const state = {
  guards: [],
  duties: [],
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

function selectTab(hash, focus = false) {
  const selectedId = hash === "#vakter" ? "vakter" : "summering";
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
  const [guards, dutyList] = await Promise.all([
    apiFetch("/api/admin/guards"),
    apiFetch(`/api/admin/duties?season=${encodeURIComponent(selectedSeason())}&year=${encodeURIComponent(yearInput.value)}`),
  ]);
  if (version !== state.loadVersion) return;

  state.guards = guards;
  state.duties = dutyList.duties;
  setLoggedIn(true);
  renderTotals(dutyList.totals);
  renderDuties(dutyList.duties);
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

  dutiesElement.innerHTML = duties.map(duty => {
    const selectId = `guard-${duty.date}`;
    const timeId = `checkout-${duty.date}`;
    const options = [`<option value="">Ledig</option>`]
      .concat(state.guards.filter(guard => guard.active).map(guard => `
        <option value="${guard.id}"${duty.guard?.id === guard.id ? " selected" : ""}>${escapeHtml(guard.name)}</option>
      `))
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
          <label for="${selectId}">Tilsynsvakt</label>
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
