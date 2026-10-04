const osloZone = "Europe/Oslo";
const guardStorageKey = "tilsynsvakt.guard";
const checklistStorageKey = "tilsynsvakt.checklist";
const weekdayNames = ["søndag", "mandag", "tirsdag", "onsdag", "torsdag", "fredag", "lørdag"];
const checklist = [
  { group: "Før vakten", label: "Motta permen med nøkler og instruks fra forrige vakt." },
  { group: "Før vakten", label: "Møt kl. 16:45 og gjør gymsalen klar til første aktivitet kl. 17:00." },
  { group: "Før vakten", label: "Still hoveddørene permanent åpne: trykk + 1, skann kortet og bruk koden fra permen." },
  { group: "Før vakten", label: "Åpne nedre inngang til garderobene når Tasta turn bruker den." },
  { group: "Ved start", label: "Kontroller at ytterdører og garderober er låst, bortsett fra hovedinngangen." },
  { group: "Under vakten", label: "Vær tilgjengelig for å hjelpe gruppene i gymsalen." },
  { group: "Ved avslutning", label: "Vent til siste gruppe har forlatt gymsalen. Lås hvis ingen flere grupper kommer." },
  { group: "Ved avslutning", label: "Gå inspeksjonsrunden. Lås nedre inngang og hoveddører; koden står i permen." },
  { group: "Ved avslutning", label: "Lås hoveddørene: trykk + 2, skann kortet og bruk koden fra permen." },
  { group: "Overlevering", label: "Lever permen hjemme hos neste vakt. Etter torsdag er neste vakt tirsdag." }
];

const state = {
  guardList: [],
  selectedGuardId: null,
  shifts: [],
  usagePlan: null,
  contacts: [],
  staticAvailable: false,
  apiAvailable: false
};

const elements = {
  guardSelect: document.querySelector("#guard-select"),
  guardStatus: document.querySelector("#guard-status"),
  notice: document.querySelector("#notice"),
  todayLabel: document.querySelector("#today-label"),
  currentActivity: document.querySelector("#current-activity"),
  currentTime: document.querySelector("#current-time"),
  planWarning: document.querySelector("#plan-warning"),
  planDay: document.querySelector("#plan-day"),
  todaySchedule: document.querySelector("#today-schedule"),
  nextHandover: document.querySelector("#next-handover"),
  rosterHandover: document.querySelector("#roster-handover"),
  myShifts: document.querySelector("#my-shifts"),
  allShifts: document.querySelector("#all-shifts"),
  signupShifts: document.querySelector("#signup-shifts"),
  openShiftSelect: document.querySelector("#open-shift-select"),
  contactList: document.querySelector("#contact-list"),
  checklistItems: document.querySelector("#checklist-items"),
  checklistProgress: document.querySelector("#checklist-progress"),
  checklistDate: document.querySelector("#checklist-date"),
  progressTrack: document.querySelector(".progress-track"),
  progressFill: document.querySelector("#progress-fill"),
  incidentDate: document.querySelector("#incident-date"),
  incidentPreview: document.querySelector("#incident-preview")
};

function node(tag, className, text) {
  const item = document.createElement(tag);
  if (className) item.className = className;
  if (text !== undefined) item.textContent = text;
  return item;
}

function osloDateParts(date = new Date()) {
  const parts = new Intl.DateTimeFormat("en-CA", {
    timeZone: osloZone,
    year: "numeric",
    month: "2-digit",
    day: "2-digit"
  }).formatToParts(date);
  return Object.fromEntries(parts.filter(part => part.type !== "literal").map(part => [part.type, part.value]));
}

function todayIso() {
  const { year, month, day } = osloDateParts();
  return `${year}-${month}-${day}`;
}

function dateFromIso(value) {
  const [year, month, day] = value.split("-").map(Number);
  return new Date(Date.UTC(year, month - 1, day, 12));
}

function formatDate(value, options = { day: "2-digit", month: "2-digit", year: "numeric" }) {
  return new Intl.DateTimeFormat("nb-NO", { ...options, timeZone: osloZone }).format(dateFromIso(value));
}

function dayIndex(value) {
  return dateFromIso(value).getUTCDay();
}

function currentOsloMinutes() {
  const parts = new Intl.DateTimeFormat("en-GB", {
    timeZone: osloZone,
    hour: "2-digit",
    minute: "2-digit",
    hourCycle: "h23"
  }).formatToParts(new Date());
  const values = Object.fromEntries(parts.filter(part => part.type !== "literal").map(part => [part.type, part.value]));
  return Number(values.hour) * 60 + Number(values.minute);
}

function minuteOfDay(value) {
  const [hour, minute] = value.split(":").map(Number);
  return hour * 60 + minute;
}

function prettyPhone(value) {
  return value.length === 8 ? `${value.slice(0, 3)} ${value.slice(3, 5)} ${value.slice(5)}` : value;
}

function getSelectedGuard() {
  return state.guardList.find(guard => guard.id === state.selectedGuardId) ?? null;
}

function showNotice(message, kind = "info") {
  elements.notice.textContent = message;
  elements.notice.className = `notice notice-${kind}`;
  elements.notice.hidden = false;
}

function clearNotice() {
  elements.notice.hidden = true;
  elements.notice.textContent = "";
}

function shiftUrl(path) {
  const base = window.TILSYNSVAKT_CONFIG?.apiBaseUrl?.replace(/\/$/, "");
  if (!base) throw new Error("API-adressen mangler i konfigurasjonen.");
  return `${base}${path}`;
}

async function apiRequest(path, options = {}) {
  let response;
  try {
    response = await fetch(shiftUrl(path), {
      ...options,
      cache: "no-store",
      headers: { ...(options.body ? { "Content-Type": "application/json" } : {}), ...options.headers }
    });
  } catch {
    throw new Error("Får ikke kontakt med vaktlisten. Kontroller nettforbindelsen og prøv igjen.");
  }
  if (response.ok) return response.status === 204 ? null : response.json();

  let problem;
  try {
    problem = await response.json();
  } catch {
    problem = {};
  }
  const error = new Error([problem.title, problem.detail].filter(Boolean).join(": ") || `Forespørselen mislyktes (${response.status}).`);
  error.status = response.status;
  error.code = problem.code;
  error.currentShift = problem.currentShift;
  throw error;
}

async function loadStaticData() {
  const [planResult, contactsResult] = await Promise.allSettled([
    fetch("./data/usage-plan.json", { cache: "no-cache" }).then(response => {
      if (!response.ok) throw new Error("usage-plan");
      return response.json();
    }),
    fetch("./data/contacts.json", { cache: "no-cache" }).then(response => {
      if (!response.ok) throw new Error("contacts");
      return response.json();
    })
  ]);
  if (planResult.status === "fulfilled") state.usagePlan = planResult.value;
  if (contactsResult.status === "fulfilled") state.contacts = contactsResult.value;
  state.staticAvailable = planResult.status === "fulfilled" || contactsResult.status === "fulfilled";
  renderPlan();
  renderContacts();
  if (!state.staticAvailable) showNotice("Kunne ikke laste den lokale informasjonen. Prøv å laste siden på nytt.", "error");
}

async function loadGuards() {
  try {
    state.guardList = await apiRequest("/api/guards");
    let saved;
    try {
      saved = JSON.parse(localStorage.getItem(guardStorageKey) || "null");
    } catch {
      localStorage.removeItem(guardStorageKey);
    }
    const savedId = Number(saved?.id);
    elements.guardSelect.replaceChildren(new Option("Velg navn", ""));
    for (const guard of state.guardList) elements.guardSelect.add(new Option(guard.name, String(guard.id)));
    if (state.guardList.some(guard => guard.id === savedId)) {
      state.selectedGuardId = savedId;
      elements.guardSelect.value = String(savedId);
    } else if (saved) {
      localStorage.removeItem(guardStorageKey);
    }
    state.apiAvailable = true;
    elements.guardStatus.textContent = `${state.guardList.length} tilsynsvakter tilgjengelig.`;
    renderHandover();
  } catch (error) {
    elements.guardStatus.textContent = "Kunne ikke laste navn fra vaktlisten.";
    elements.guardSelect.disabled = true;
    showNotice(error.message, "error");
  }
}

async function loadShifts({ message } = {}) {
  const from = todayIso();
  const until = new Date(dateFromIso(from).getTime() + 89 * 86400000).toISOString().slice(0, 10);
  for (const container of [elements.myShifts, elements.allShifts, elements.signupShifts]) {
    container.replaceChildren(node("p", "loading", "Laster vaktliste …"));
  }
  try {
    const result = await apiRequest(`/api/shifts?from=${from}&to=${until}`);
    state.shifts = result.shifts;
    state.apiAvailable = true;
    renderShifts();
    renderSignupOptions();
    renderHandover();
    if (message) showNotice(message, "success");
  } catch (error) {
    for (const container of [elements.myShifts, elements.allShifts, elements.signupShifts]) {
      container.replaceChildren(node("p", "empty-state", error.message));
    }
    elements.openShiftSelect.replaceChildren(new Option("Vaktlisten er ikke tilgjengelig", ""));
    showNotice(error.message, "error");
  }
}

function renderPlan() {
  const today = todayIso();
  const date = dateFromIso(today);
  const day = dayIndex(today);
  const minutes = currentOsloMinutes();
  const humanDate = new Intl.DateTimeFormat("nb-NO", {
    timeZone: osloZone,
    weekday: "long",
    day: "2-digit",
    month: "2-digit",
    year: "numeric"
  }).format(date);
  elements.todayLabel.textContent = humanDate.charAt(0).toLocaleUpperCase("nb-NO") + humanDate.slice(1);
  elements.currentTime.textContent = `Klokken ${new Intl.DateTimeFormat("nb-NO", { timeZone: osloZone, hour: "2-digit", minute: "2-digit" }).format(new Date())}`;
  elements.planDay.textContent = weekdayNames[day];
  elements.todaySchedule.replaceChildren();

  const plan = state.usagePlan;
  if (!plan) {
    elements.currentActivity.textContent = "Dagens plan er ikke tilgjengelig.";
    elements.planWarning.textContent = "Den lokale ukeplanen kunne ikke lastes.";
    return;
  }
  elements.planWarning.textContent = `Ukeplan ${plan.schoolYear} · foreldet etter ${formatDate(plan.validTo)}. Ny plan for 2026–2027 er ikke publisert.`;
  const isCurrentPlan = today >= plan.validFrom && today <= plan.validTo;
  const entries = isCurrentPlan
    ? plan.entries.filter(entry => entry.day === (day || 7) && isEntryActive(entry, today)).sort((a, b) => a.start.localeCompare(b.start))
    : [];
  const now = entries.find(entry => minuteOfDay(entry.start) <= minutes && minutes < minuteOfDay(entry.end));
  const next = entries.filter(entry => minuteOfDay(entry.start) > minutes);
  elements.currentActivity.textContent = now ? now.group : "Ingen aktivitet registrert nå.";
  if (!isCurrentPlan) elements.currentActivity.textContent = "Plan for denne datoen er ikke publisert.";

  if (entries.length === 0) {
    elements.todaySchedule.append(node("li", "empty-state", isCurrentPlan ? "Ingen aktiviteter i gymsalen i dag." : "Denne datoen ligger utenfor den publiserte ukeplanen."));
    return;
  }
  for (const entry of entries) {
    const item = node("li", `schedule-item${entry === now ? " schedule-current" : ""}`);
    const time = node("span", "schedule-time", `${entry.start}–${entry.end}`);
    const detail = node("div", "schedule-detail");
    detail.append(node("strong", "", entry.group));
    if (entry.contactId) {
      const contact = state.contacts.find(candidate => candidate.id === entry.contactId);
      if (contact) detail.append(node("span", "muted", contact.name));
    }
    const status = node("span", "schedule-status", entry === now ? "Nå" : minuteOfDay(entry.start) > minutes ? "Kommer" : "Ferdig");
    item.append(time, detail, status);
    elements.todaySchedule.append(item);
  }
}

function isEntryActive(entry, date) {
  if (!entry.periods?.length) return false;
  const monthDay = date.slice(5);
  return entry.periods.some(period => monthDay >= period.from && monthDay <= period.to);
}

function renderContacts() {
  elements.contactList.replaceChildren();
  if (!state.contacts.length) {
    elements.contactList.append(node("p", "empty-state", "Kontaktlisten er ikke tilgjengelig."));
    return;
  }
  for (const contact of state.contacts) {
    const item = node("article", "contact-row");
    const detail = node("div", "contact-detail");
    detail.append(node("p", "eyebrow", contact.group), node("h2", "", contact.name));
    const actions = node("div", "contact-actions");
    const phone = node("a", "contact-action", prettyPhone(contact.phone));
    phone.href = `tel:+47${contact.phone}`;
    phone.setAttribute("aria-label", `Ring ${contact.name}, ${prettyPhone(contact.phone)}`);
    actions.append(phone);
    if (contact.email) {
      const email = node("a", "contact-action contact-email", "E-post");
      email.href = `mailto:${contact.email}`;
      email.setAttribute("aria-label", `Send e-post til ${contact.name}`);
      actions.append(email);
    }
    item.append(detail, actions);
    elements.contactList.append(item);
  }
}

function renderShifts() {
  const guard = getSelectedGuard();
  const assigned = state.shifts.filter(shift => shift.status === "taken");
  const mine = guard ? assigned.filter(shift => shift.guard?.id === guard.id) : [];
  elements.myShifts.replaceChildren();
  elements.allShifts.replaceChildren();
  elements.signupShifts.replaceChildren();
  if (!guard) elements.myShifts.append(node("p", "empty-state", "Velg navnet ditt øverst for å se vaktene dine."));
  else if (!mine.length) elements.myShifts.append(node("p", "empty-state", "Du har ingen vakter i den viste perioden."));
  else for (const shift of mine) elements.myShifts.append(shiftCard(shift, true));

  if (!state.shifts.length) {
    elements.allShifts.append(node("p", "empty-state", "Ingen vakter i den viste perioden."));
    elements.signupShifts.append(node("p", "empty-state", "Ingen vakter i den viste perioden."));
    return;
  }
  elements.allShifts.append(weekTable());
  for (const shift of state.shifts) elements.signupShifts.append(shiftCard(shift, false, true));
}

function isoWeek(value) {
  const date = dateFromIso(value);
  const day = (date.getUTCDay() + 6) % 7;
  const monday = new Date(date.getTime() - day * 86400000);
  const thursday = new Date(monday.getTime() + 3 * 86400000);
  const yearStart = Date.UTC(thursday.getUTCFullYear(), 0, 1);
  return {
    week: Math.floor((thursday.getTime() - yearStart) / (7 * 86400000)) + 1,
    key: monday.toISOString().slice(0, 10),
    monday,
    thursday
  };
}

function weekTable() {
  const weeks = new Map();
  for (const shift of state.shifts) {
    const info = isoWeek(shift.date);
    if (!weeks.has(info.key)) weeks.set(info.key, { info, shifts: {} });
    weeks.get(info.key).shifts[dayIndex(shift.date)] = shift;
  }
  const days = [[1, "Mandag"], [2, "Tirsdag"], [3, "Onsdag"], [4, "Torsdag"]];
  const table = node("table", "week-table");
  table.append(node("caption", "sr-only", "Vaktliste per uke"));
  const head = node("tr");
  for (const label of ["Uke", "Dato", ...days.map(day => day[1]), "Ledige"]) {
    const cell = node("th", "", label);
    cell.scope = "col";
    head.append(cell);
  }
  const thead = node("thead");
  thead.append(head);
  table.append(thead);
  const body = node("tbody");
  const currentWeek = isoWeek(todayIso()).key;
  const format = date => formatDate(date.toISOString().slice(0, 10), { day: "2-digit", month: "2-digit" });
  for (const { info, shifts } of weeks.values()) {
    const row = node("tr", info.key === currentWeek ? "week-current" : "");
    const week = node("th", "", String(info.week));
    week.scope = "row";
    row.append(week, node("td", "week-dates", `${format(info.monday)} - ${format(info.thursday)}`));
    let open = 0;
    for (const [index] of days) {
      const shift = shifts[index];
      const cell = node("td");
      if (index === 1) {
        cell.className = "week-board";
        cell.textContent = "Styrevakt";
      } else if (!shift) {
        cell.className = "week-none";
        cell.textContent = "Ingen vakt";
      } else if (shift.status === "open") {
        open++;
        cell.className = "week-open";
        const link = node("a", "", "Ledig");
        link.href = "#pamelding";
        cell.append(link);
      } else {
        cell.textContent = shift.guard.name;
        if (shift.guard.id === state.selectedGuardId) cell.className = "week-mine";
      }
      row.append(cell);
    }
    row.append(node("td", "week-count", String(open)));
    body.append(row);
  }
  table.append(body);
  const wrap = node("div", "week-table-wrap");
  wrap.tabIndex = 0;
  wrap.setAttribute("role", "region");
  wrap.setAttribute("aria-label", "Vaktliste per uke");
  wrap.append(table);
  return wrap;
}

function shiftCard(shift, mineOnly, signupView = false) {
  const card = node("article", `shift-card${shift.status === "open" ? " shift-open" : ""}`);
  const header = node("div", "shift-card-header");
  const date = node("div", "shift-date");
  date.append(node("strong", "", formatDate(shift.date)), node("span", "muted", weekdayNames[dayIndex(shift.date)]));
  header.append(date, node("span", `status-tag status-${shift.status}`, shift.status === "open" ? "Ledig" : "Påmeldt"));
  card.append(header);
  if (shift.guard) card.append(node("p", "shift-guard", shift.guard.name));
  if (shift.status === "open") {
    card.append(node("p", "muted", "Ingen tilsynsvakt er påmeldt."));
  } else if (!signupView || shift.guard?.id === state.selectedGuardId) {
    const actions = node("div", "shift-actions");
    actions.append(makeReplaceControl(shift));
    const cancel = node("button", "button button-danger", "Avmeld");
    cancel.type = "button";
    cancel.addEventListener("click", () => cancelShift(shift));
    actions.append(cancel);
    card.append(actions);
  } else {
    card.append(node("p", "muted", "Vakten er allerede tatt."));
  }
  if (mineOnly && shift.guard) card.dataset.mine = "true";
  return card;
}

function makeReplaceControl(shift) {
  const form = node("form", "replace-form");
  const label = node("label", "sr-only", `Velg tilsynsvakt for ${formatDate(shift.date)}`);
  const select = node("select", "guard-target");
  select.setAttribute("aria-label", `Ny tilsynsvakt for ${formatDate(shift.date)}`);
  const selectedId = shift.guard?.id;
  for (const guard of state.guardList) select.add(new Option(guard.name, String(guard.id), false, guard.id === selectedId));
  const button = node("button", "button button-secondary", "Endre vakt");
  button.type = "submit";
  label.append(select);
  form.append(label, button);
  form.addEventListener("submit", async event => {
    event.preventDefault();
    if (!selectedId) return;
    const targetId = Number(select.value);
    await mutateShift(`/api/shifts/${shift.date}`, {
      method: "PUT",
      body: JSON.stringify({ guardId: targetId, expectedGuardId: selectedId })
    }, "Vakten er endret.");
  });
  return form;
}

async function cancelShift(shift) {
  if (!shift.guard) return;
  await mutateShift(`/api/shifts/${shift.date}?expectedGuardId=${shift.guard.id}`, { method: "DELETE" }, "Påmeldingen er avmeldt.");
}

async function mutateShift(path, options, successMessage) {
  clearNotice();
  if (!getSelectedGuard()) {
    showNotice("Velg navnet ditt øverst før du endrer vaktlisten.", "error");
    return;
  }
  try {
    await apiRequest(path, options);
    await loadShifts({ message: successMessage });
  } catch (error) {
    if (error.status === 409) {
      const current = error.currentShift;
      const currentText = current
        ? ` Nå står vakten ${formatDate(current.date)} som ${current.status === "open" ? "ledig" : `påmeldt ${current.guard?.name ?? "en annen vakt"}`}.`
        : "";
      await loadShifts();
      showNotice(`${error.message}${currentText} Vaktlisten er oppdatert.`, "error");
      return;
    }
    showNotice(error.message, "error");
  }
}

function renderSignupOptions() {
  const openShifts = state.shifts.filter(shift => shift.status === "open");
  elements.openShiftSelect.replaceChildren(new Option(openShifts.length ? "Velg dato" : "Ingen ledige vakter", ""));
  for (const shift of openShifts) {
    elements.openShiftSelect.add(new Option(`${formatDate(shift.date)} · ${weekdayNames[dayIndex(shift.date)]}`, shift.date));
  }
  elements.openShiftSelect.disabled = !getSelectedGuard() || openShifts.length === 0;
}

function renderHandover() {
  renderSignupOptions();
  const guard = getSelectedGuard();
  if (!guard) {
    elements.nextHandover.textContent = "Velg navnet ditt for å se neste vakt.";
    elements.rosterHandover.textContent = "Velg navnet ditt for å se overleveringen.";
    return;
  }
  const ownShifts = state.shifts.filter(shift => shift.guard?.id === guard.id).sort((a, b) => a.date.localeCompare(b.date));
  const nextOwn = ownShifts[0];
  const nextAfter = nextOwn
    ? state.shifts.find(shift => shift.date > nextOwn.date)
    : state.shifts.find(shift => shift.guard?.id !== guard.id && shift.status === "taken");
  const message = nextOwn
    ? nextAfter
      ? nextAfter.status === "taken"
        ? `Etter vakten ${formatDate(nextOwn.date)} leverer du permen til ${nextAfter.guard.name} (${formatDate(nextAfter.date)}).`
        : `Neste vakt etter ${formatDate(nextOwn.date)} er ${formatDate(nextAfter.date)} og er ledig. Ingen mottaker er påmeldt ennå.`
      : `Neste registrerte vakt etter ${formatDate(nextOwn.date)} er ikke påmeldt ennå. Se vaktlisten for oppdateringer.`
    : "Du har ingen kommende vakter påmeldt.";
  elements.nextHandover.textContent = message;
  elements.rosterHandover.textContent = message;
  renderShifts();
}

function todayChecklistState() {
  try {
    const stored = JSON.parse(localStorage.getItem(checklistStorageKey) || "{}");
    return stored[todayIso()] ?? [];
  } catch {
    return [];
  }
}

function saveChecklistState(done) {
  try {
    const stored = JSON.parse(localStorage.getItem(checklistStorageKey) || "{}");
    stored[todayIso()] = done;
    localStorage.setItem(checklistStorageKey, JSON.stringify(stored));
  } catch {
    showNotice("Nettleseren kunne ikke lagre sjekklisten lokalt.", "error");
  }
}

function renderChecklist() {
  const done = new Set(todayChecklistState());
  elements.checklistItems.replaceChildren();
  elements.checklistDate.textContent = `Fremdriften lagres bare på denne enheten for ${formatDate(todayIso())}.`;
  let groupName = "";
  checklist.forEach((item, index) => {
    if (item.group !== groupName) {
      groupName = item.group;
      elements.checklistItems.append(node("h2", "checklist-heading", groupName));
    }
    const label = node("label", `checklist-row${done.has(index) ? " is-done" : ""}`);
    const input = document.createElement("input");
    input.type = "checkbox";
    input.checked = done.has(index);
    input.addEventListener("change", () => {
      if (input.checked) done.add(index);
      else done.delete(index);
      saveChecklistState([...done]);
      label.classList.toggle("is-done", input.checked);
      updateChecklistProgress(done.size);
    });
    label.append(input, node("span", "", item.label));
    elements.checklistItems.append(label);
  });
  updateChecklistProgress(done.size);
}

function updateChecklistProgress(count) {
  elements.checklistProgress.textContent = `${count} / ${checklist.length}`;
  elements.progressTrack.setAttribute("aria-valuemax", String(checklist.length));
  elements.progressTrack.setAttribute("aria-valuenow", String(count));
  elements.progressFill.style.width = `${count / checklist.length * 100}%`;
}

function setView() {
  const viewName = location.hash.slice(1) || "plan";
  const allowed = ["plan", "vaktliste", "pamelding", "kontakter", "sjekkliste", "rapport"];
  const selected = allowed.includes(viewName) ? viewName : "plan";
  for (const view of document.querySelectorAll(".view")) view.hidden = view.id !== `view-${selected}`;
  for (const link of document.querySelectorAll(".main-nav a")) {
    if (link.dataset.view === selected) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  }
}

function setupForms() {
  elements.guardSelect.addEventListener("change", () => {
    state.selectedGuardId = Number(elements.guardSelect.value) || null;
    const guard = getSelectedGuard();
    if (guard) localStorage.setItem(guardStorageKey, JSON.stringify({ id: guard.id, name: guard.name }));
    else localStorage.removeItem(guardStorageKey);
    renderHandover();
    renderShifts();
  });

  document.querySelector("#signup-form").addEventListener("submit", async event => {
    event.preventDefault();
    const guard = getSelectedGuard();
    const date = elements.openShiftSelect.value;
    if (!guard) {
      showNotice("Velg navnet ditt øverst før du melder deg på.", "error");
      return;
    }
    if (!date) return;
    await mutateShift(`/api/shifts/${date}/signup`, {
      method: "POST",
      body: JSON.stringify({ guardId: guard.id })
    }, "Du er meldt på vakten.");
  });

  document.querySelector("#refresh-shifts").addEventListener("click", () => loadShifts());
  document.querySelector("#incident-form").addEventListener("submit", event => {
    event.preventDefault();
    const preview = elements.incidentPreview;
    preview.replaceChildren();
    preview.append(node("h2", "", "Utkast · ikke sendt"));
    preview.append(node("p", "", `Type: ${document.querySelector("#incident-type").value}`));
    preview.append(node("p", "", `Dato: ${formatDate(elements.incidentDate.value)}`));
    const notes = document.querySelector("#incident-notes").value.trim();
    if (notes) preview.append(node("p", "", `Beskrivelse: ${notes}`));
    preview.hidden = false;
  });
}

function setupOfflineSupport() {
  if ("serviceWorker" in navigator && (location.protocol === "https:" || location.hostname === "localhost" || location.hostname === "127.0.0.1")) {
    navigator.serviceWorker.register("./sw.js").catch(() => {});
  }
}

async function start() {
  const today = todayIso();
  elements.incidentDate.value = today;
  setupForms();
  renderChecklist();
  setView();
  window.addEventListener("hashchange", setView);
  setupOfflineSupport();
  await loadStaticData();
  await Promise.all([loadGuards(), loadShifts()]);
  renderPlan();
}

start();