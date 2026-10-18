const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const vm = require("node:vm");
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || "playwright");

const baseUrl = process.env.ADMIN_TEST_URL || "http://127.0.0.1:8080";
const guard = { id: 1, name: "Testvakt med et langt visningsnavn", phone: "", active: true };
// Deliberately unsorted; the UI sorts with Norwegian collation and hides inactive guards from duty pickers.
const adminGuards = [
  { id: 3, name: "Ådland", phone: "+4790000001", active: true },
  guard,
  { id: 2, name: "Øvrebø", phone: "", active: false },
  { id: 9, name: "Bare i backend", phone: "", active: true },
];
const dutyList = {
  season: "autumn", year: 2026, from: "2026-09-01", to: "2026-11-28",
  duties: [{ date: "2026-10-06", dayOfWeek: "tuesday", status: "taken", guard,
    scheduledStart: "16:45", endTime: "22:00", hasRecordedCheckOut: false, durationHours: 5.5 },
  { date: "2026-10-05", dayOfWeek: "monday", status: "open", guard: null,
    scheduledStart: "16:45", endTime: "22:00", hasRecordedCheckOut: false, durationHours: 5.5 },
  { date: "2026-10-08", dayOfWeek: "thursday", status: "open", guard: null,
    scheduledStart: "16:45", endTime: "21:00", hasRecordedCheckOut: false, durationHours: 4.25 }],
  totals: [{ guard, dutyCount: 1, totalHours: 5.5, dutiesWithoutCheckOut: 1 }],
};
// Wednesday 07.10.2026 in Oslo: the 06.10 duty is in the past, 08.10 is upcoming.
const fixedNow = new Date("2026-10-07T12:00:00+02:00");

async function run() {
  const browser = await chromium.launch({ headless: true, executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH });
  const screenshots = fs.mkdtempSync(path.join(os.tmpdir(), "tilsynsvakt-admin-"));
  try {
    async function openPage({ viewport = { width: 1280, height: 900 }, hash = "", responder } = {}) {
      const context = await browser.newContext({ viewport });
      const page = await context.newPage();
      const errors = [];
      const consoleErrors = [];
      const requests = [];
      const guards = adminGuards.map(candidate => ({ ...candidate }));
      page.on("pageerror", error => errors.push(error.message));
      page.on("console", message => { if (message.type() === "error") consoleErrors.push(message.text()); });
      await page.clock.setFixedTime(fixedNow);
      await page.route("**/api/admin/**", async route => {
        const url = new URL(route.request().url());
        requests.push({ pathname: url.pathname, search: url.search, method: route.request().method(), body: route.request().postData() });
        const guardUpdate = url.pathname.match(/\/guards\/(\d+)$/);
        if (url.pathname.endsWith("/guards")) {
          await route.fulfill({ json: guards });
        } else if (guardUpdate && route.request().method() === "PUT") {
          const existing = guards.find(candidate => candidate.id === Number(guardUpdate[1]));
          Object.assign(existing, JSON.parse(route.request().postData()));
          await route.fulfill({ json: existing });
        } else if (responder) {
          await responder(route, url);
        } else {
          await route.fulfill({ json: dutyList });
        }
      });
      await page.goto(`${baseUrl}/admin.html${hash}`);
      await page.locator("#username").fill("fixture-user");
      await page.locator("#password").fill("fixture-only");
      await page.locator("#auth-form button[type=submit]").click();
      return { context, page, errors, consoleErrors, requests };
    }

    for (const viewport of [{ width: 1280, height: 900 }, { width: 390, height: 844 }]) {
      const { context, page, errors, consoleErrors, requests } = await openPage({ viewport });
      await page.locator("#totals table").waitFor();
      assert.equal(await page.locator("#notice").isVisible(), false);
      assert.equal(await page.getByRole("tab", { name: "Summering" }).getAttribute("aria-selected"), "true");
      assert.equal(await page.locator("#vakter").isVisible(), false);
      assert.match(await page.locator("#totals").innerText(), /5,5/);
      const beforeTabSwitch = requests.length;
      await page.getByRole("tab", { name: "Vakter", exact: true }).click();
      assert.equal(new URL(page.url()).hash, "#vakter");
      assert.equal(await page.locator("#summering").isVisible(), false);
      assert.equal(await page.locator(".admin-duty-form").count(), 1);
      assert.equal(await page.locator(".admin-duty-form").getAttribute("data-date"), "2026-10-08");
      const toggle = page.locator("#toggle-past");
      assert.equal(await toggle.textContent(), "Vis tidligere vakter (1)");
      assert.equal(await toggle.getAttribute("aria-expanded"), "false");
      const options = await page.locator("#guard-2026-10-08 option").evaluateAll(items =>
        items.map(item => ({ text: item.textContent.trim(), value: item.value, disabled: item.disabled })));
      assert.deepEqual(options, [
        { text: "Ledig", value: "", disabled: false },
        { text: "Bare i backend", value: "9", disabled: false },
        { text: "Testvakt med et langt visningsnavn", value: "1", disabled: false },
        { text: "Ådland", value: "3", disabled: false },
      ]);
      await toggle.click();
      assert.equal(await page.locator(".admin-duty-form").count(), 2);
      assert.equal(await page.locator("#toggle-past").textContent(), "Skjul tidligere vakter");
      assert.equal(await page.locator("#toggle-past").getAttribute("aria-expanded"), "true");
      assert.equal(await page.locator("#guard-2026-10-06").inputValue(), "1");
      await page.locator("#toggle-past").click();
      assert.equal(await page.locator(".admin-duty-form").count(), 1);
      assert.equal(requests.length, beforeTabSwitch);
      await page.getByRole("tab", { name: "Vakter", exact: true }).press("ArrowRight");
      assert.equal(await page.locator("#tab-musikanter").evaluate(element => element === document.activeElement), true);
      await page.locator("#tab-musikanter").press("ArrowRight");
      assert.equal(await page.locator("#tab-summering").evaluate(element => element === document.activeElement), true);
      await page.locator("#tab-summering").press("ArrowLeft");
      assert.equal(await page.locator("#tab-musikanter").getAttribute("aria-selected"), "true");
      await page.locator("#tab-musikanter").press("Home");
      assert.equal(await page.locator("#tab-summering").getAttribute("aria-selected"), "true");
      await page.locator("#tab-summering").press("End");
      assert.equal(await page.locator("#tab-musikanter").getAttribute("aria-selected"), "true");
      await page.locator("#tab-musikanter").press("ArrowLeft");
      assert.equal(await page.locator("#tab-vakter").getAttribute("aria-selected"), "true");
      await page.locator("#year-next").click();
      await page.waitForFunction(() => document.querySelector("#notice").hidden);
      assert.equal(await page.locator("#year").inputValue(), "2027");
      assert.ok(requests.some(request => request.search.includes("year=2027")));
      await page.locator(".season-option", { hasText: "Januar–juni" }).click();
      await page.waitForFunction(() => document.querySelector("#notice").hidden);
      assert.ok(requests.some(request => request.search.includes("season=spring")));
      await page.locator(".admin-duty-form button[type=submit]").click();
      await page.waitForFunction(() => document.querySelector("#notice").hidden);
      assert.ok(requests.some(request => request.method === "PUT"));
      await page.screenshot({ path: path.join(screenshots, `duties-${viewport.width}.png`), fullPage: true });
      await page.getByRole("tab", { name: "Summering" }).click();
      await page.screenshot({ path: path.join(screenshots, `summary-${viewport.width}.png`), fullPage: true });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
      const overlappingCells = await page.locator("#totals td, #totals th").evaluateAll(cells =>
        cells.some(cell => cell.scrollWidth > cell.clientWidth + 1));
      assert.equal(overlappingCells, false);
      await page.getByRole("tab", { name: "Vakter", exact: true }).click();
      await page.reload();
      await page.locator(".admin-duty-form").waitFor();
      assert.equal(await page.locator("#tab-vakter").getAttribute("aria-selected"), "true");
      assert.equal(await page.locator("#auth-form").isVisible(), false);
      await page.evaluate(() => { location.hash = "summering"; });
      await page.locator("#summering").waitFor({ state: "visible" });
      await page.locator("#logout").click();
      assert.equal(await page.locator("#auth-form").isVisible(), true);
      assert.equal(await page.evaluate(() => sessionStorage.getItem("tilsynsvakt.admin.password")), null);
      assert.deepEqual(errors, []);
      assert.deepEqual(consoleErrors, []);
      await context.close();
      console.log(`PASS: tabs, keyboard, hashes, reload, guard picker/session, save and layout at ${viewport.width}px`);
    }

    const guardsPage = await openPage({ hash: "#musikanter" });
    {
      const { page, errors, requests } = guardsPage;
      await page.locator("#guard-list .admin-guard-form").first().waitFor();
      assert.equal(await page.locator("label[for=guard-new-name]").textContent(), "Etternavn (musikant)");
      assert.equal(await page.locator("#guard-name-help").textContent(), "Bruk kun musikantens etternavn – ikke foreldrenes navn.");
      const names = selector => page.locator(`${selector} input[name=name]`).evaluateAll(inputs => inputs.map(input => input.value));
      assert.deepEqual(await names("#guard-list-active"), ["Bare i backend", "Testvakt med et langt visningsnavn", "Ådland"]);
      assert.deepEqual(await names("#guard-list-inactive"), ["Øvrebø"]);
      const inactive = page.locator("#guard-list-inactive");
      assert.equal(await inactive.locator("summary").textContent(), "Vis inaktive (1)");
      assert.equal(await inactive.evaluate(element => element.open), false);
      assert.equal(await page.locator("#guard-name-2").isVisible(), false);
      await inactive.locator("summary").click();
      assert.equal(await page.locator("#guard-name-2").isVisible(), true);
      assert.equal(await page.locator("#guard-active-2").isChecked(), false);
      await page.locator("#guard-new-name").fill("   ");
      await page.locator("#guard-new-phone").fill("900 00 000");
      await page.locator("#guard-create-form button[type=submit]").click();
      assert.match(await page.locator("#notice").innerText(), /etternavn/);
      assert.equal(requests.some(request => request.method === "POST"), false);
      await page.locator("#guard-new-name").fill("  Nord   Hansen ");
      await page.locator("#guard-create-form button[type=submit]").click();
      await page.waitForFunction(() => document.querySelector("#notice").classList.contains("notice-success"));
      const created = requests.find(request => request.method === "POST");
      assert.equal(created.pathname, "/api/admin/guards");
      assert.deepEqual(JSON.parse(created.body), { name: "Nord Hansen", phone: "900 00 000" });
      assert.equal(await page.locator("#guard-new-name").inputValue(), "");
      await page.locator("#guard-name-3").fill("Ådlandsvik");
      await page.locator(".admin-guard-form[data-id='3'] button[type=submit]").click();
      await page.waitForFunction(() => document.querySelector("#notice").textContent.includes("oppdatert"));
      const updated = requests.find(request => request.method === "PUT" && request.pathname === "/api/admin/guards/3");
      assert.deepEqual(JSON.parse(updated.body), { name: "Ådlandsvik", phone: "+4790000001", active: true });

      const waitForNotice = () => page.waitForFunction(() => {
        const notice = document.querySelector("#notice");
        return !notice.hidden && notice.textContent.includes("oppdatert");
      });
      await page.evaluate(() => { document.querySelector("#notice").hidden = true; });
      await page.locator("#guard-list-inactive summary").click();
      await page.locator("#guard-phone-2").fill("900 00 002");
      await page.locator("#guard-active-2").check();
      await page.locator(".admin-guard-form[data-id='2'] button[type=submit]").click();
      await waitForNotice();
      assert.deepEqual(await names("#guard-list-active"), ["Bare i backend", "Testvakt med et langt visningsnavn", "Øvrebø", "Ådlandsvik"]);
      assert.equal(await page.locator("#guard-list-inactive").count(), 0);
      await page.evaluate(() => { document.querySelector("#notice").hidden = true; });
      await page.locator("#guard-active-3").uncheck();
      await page.locator(".admin-guard-form[data-id='3'] button[type=submit]").click();
      await waitForNotice();
      assert.deepEqual(await names("#guard-list-active"), ["Bare i backend", "Testvakt med et langt visningsnavn", "Øvrebø"]);
      assert.deepEqual(await names("#guard-list-inactive"), ["Ådlandsvik"]);
      assert.equal(await page.locator("#guard-list-inactive summary").textContent(), "Vis inaktive (1)");
      assert.equal(await page.locator("#guard-list-inactive").evaluate(element => element.open), false);
      await page.getByRole("tab", { name: "Vakter", exact: true }).click();
      assert.deepEqual(await page.locator("#guard-2026-10-08 option").evaluateAll(items => items.map(item => item.textContent.trim())),
        ["Ledig", "Bare i backend", "Testvakt med et langt visningsnavn", "Øvrebø"]);
      await page.getByRole("tab", { name: "Musikanter" }).click();
      await page.setViewportSize({ width: 390, height: 844 });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
      assert.deepEqual(errors, []);
      await guardsPage.context.close();
      console.log("PASS: musician last-name labels, nb sorting, active list, collapsed inactive list, (de)activation, create/edit with trimmed names");
    }

    for (const hash of ["#summering", "#vakter", "#musikanter"]) {
      const { context, page, errors } = await openPage({ hash });
      await page.waitForFunction(() => document.querySelector("#notice").hidden);
      assert.equal(await page.locator(`#tab-${hash.slice(1)}`).getAttribute("aria-selected"), "true");
      assert.deepEqual(errors, []);
      await context.close();
    }
    console.log("PASS: both authenticated deep links");

    const failureCases = [
      ...[400, 401, 403, 429, 500].map(status => ({ name: `HTTP ${status}`, responder: route => route.fulfill({ status, json: { detail: "Fixture failure" } }) })),
      { name: "network failure", responder: route => route.abort("failed") },
      { name: "invalid JSON", responder: route => route.fulfill({ contentType: "application/json", body: "invalid" }) },
      { name: "invalid response shape", responder: route => route.fulfill({ json: {} }) },
      { name: "network hang", responder: () => new Promise(() => {}), timeout: 20000 },
    ];
    for (const failure of failureCases) {
      const { context, page, errors } = await openPage({ responder: failure.responder });
      await page.waitForFunction(() => document.querySelector("#notice").classList.contains("notice-error"), null, { timeout: failure.timeout || 5000 });
      assert.equal(await page.locator(".loading").count(), 0);
      assert.match(await page.locator("#notice").innerText(), failure.name === "network hang" ? /lang tid/ : /Kunne ikke|Ugyldig/);
      assert.deepEqual(errors, []);
      await context.close();
      console.log(`PASS: ${failure.name} ends loading with a Norwegian error`);
    }

    const empty = await openPage({ responder: route => route.fulfill({ json: { ...dutyList, duties: [], totals: [] } }) });
    await empty.page.waitForFunction(() => document.querySelector("#notice").hidden);
    assert.match(await empty.page.locator("#totals").innerText(), /Ingen vakter/);
    assert.match(await empty.page.locator("#duties").textContent(), /Fant ingen/);
    await empty.context.close();
    console.log("PASS: empty season states");

    const inactiveGuard = adminGuards.find(candidate => candidate.active === false);
    const outside = await openPage({ hash: "#vakter", responder: route => route.fulfill({ json: { ...dutyList,
      duties: [{ ...dutyList.duties[2], status: "taken", guard: inactiveGuard }] } }) });
    await outside.page.locator(".admin-duty-form").waitFor();
    assert.equal(await outside.page.locator("#toggle-past").count(), 0);
    assert.equal(await outside.page.locator("#guard-2026-10-08").inputValue(), "2");
    assert.equal(await outside.page.locator("#guard-2026-10-08 option").count(), 5);
    await outside.context.close();

    const onlyPast = await openPage({ hash: "#vakter", responder: route => route.fulfill({ json: { ...dutyList, duties: dutyList.duties.slice(0, 2) } }) });
    await onlyPast.page.locator("#toggle-past").waitFor();
    assert.match(await onlyPast.page.locator("#duties").textContent(), /Ingen kommende vakter/);
    await onlyPast.context.close();
    console.log("PASS: assigned inactive guard is kept; season with only past duties");

    const pendingRoutes = [];
    const stale = await openPage({ responder: route => new Promise(resolve => pendingRoutes.push({ route, resolve })) });
    while (pendingRoutes.length < 1) await stale.page.waitForTimeout(10);
    await stale.page.locator("#tab-summering").evaluate(() => document.querySelector("#admin-panel").hidden = false);
    await stale.page.locator("#year-next").click();
    while (pendingRoutes.length < 2) await stale.page.waitForTimeout(10);
    await pendingRoutes[1].route.fulfill({ json: { ...dutyList, from: "2027-09-01" } });
    pendingRoutes[1].resolve();
    await stale.page.waitForFunction(() => document.querySelector("#notice").hidden);
    await pendingRoutes[0].route.fulfill({ json: dutyList });
    pendingRoutes[0].resolve();
    await stale.page.waitForTimeout(100);
    assert.match(await stale.page.locator("#season-range").innerText(), /2027/);
    await stale.page.locator("#year-next").click();
    while (pendingRoutes.length < 3) await stale.page.waitForTimeout(10);
    await stale.page.locator("#logout").click();
    await pendingRoutes[2].route.fulfill({ json: dutyList });
    pendingRoutes[2].resolve();
    await stale.page.waitForTimeout(100);
    assert.equal(await stale.page.locator("#admin-panel").isVisible(), false);
    assert.match(await stale.page.locator("#notice").innerText(), /logget ut/);
    await stale.context.close();
    console.log("PASS: stale season responses and logout do not restore old data/session");

    const source = fs.readFileSync(path.join(__dirname, "../js/admin.js"), "utf8");
    const context = vm.createContext({ window: { addEventListener() {}, location: { hash: "" } },
      document: { querySelector: () => ({ value: "", addEventListener() {} }), querySelectorAll: () => [] },
      sessionStorage: { getItem: () => "fixture-only" }, TextEncoder, btoa, AbortController,
      setTimeout: callback => setTimeout(callback, 25), clearTimeout,
      fetch: async () => ({ status: 200, ok: true, json: () => new Promise(() => {}) }),
    });
    vm.runInContext(source.replace(/^import .*$/m, "").replace("if (hasCredentials()) {", "if (false) {"), context);
    await assert.rejects(vm.runInContext('apiFetch("/api/admin/duties")', context), error => error.name === "TimeoutError");
    console.log("PASS: stalled response body also times out");
    console.log(`Screenshots: ${screenshots}`);
  } finally {
    await browser.close();
  }
}

run().catch(error => { console.error(error.message); process.exitCode = 1; });