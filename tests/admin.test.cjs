const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const vm = require("node:vm");
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || "playwright");

const baseUrl = process.env.ADMIN_TEST_URL || "http://127.0.0.1:8080";
const guard = { id: 1, name: "Testvakt med et langt visningsnavn", phone: "", active: true };
const dutyList = {
  season: "autumn", year: 2026, from: "2026-09-01", to: "2026-11-28",
  duties: [{ date: "2026-10-06", dayOfWeek: "tuesday", status: "taken", guard,
    scheduledStart: "16:45", endTime: "22:00", hasRecordedCheckOut: false, durationHours: 5.5 }],
  totals: [{ guard, dutyCount: 1, totalHours: 5.5, dutiesWithoutCheckOut: 1 }],
};

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
      page.on("pageerror", error => errors.push(error.message));
      page.on("console", message => { if (message.type() === "error") consoleErrors.push(message.text()); });
      await page.route("**/api/admin/**", async route => {
        const url = new URL(route.request().url());
        requests.push({ pathname: url.pathname, search: url.search, method: route.request().method() });
        if (url.pathname.endsWith("/guards")) {
          await route.fulfill({ json: [guard] });
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
      assert.equal(requests.length, beforeTabSwitch);
      await page.getByRole("tab", { name: "Vakter", exact: true }).press("ArrowRight");
      assert.equal(await page.locator("#tab-summering").evaluate(element => element === document.activeElement), true);
      await page.locator("#tab-summering").press("ArrowLeft");
      assert.equal(await page.locator("#tab-vakter").getAttribute("aria-selected"), "true");
      await page.locator("#tab-vakter").press("Home");
      assert.equal(await page.locator("#tab-summering").getAttribute("aria-selected"), "true");
      await page.locator("#tab-summering").press("End");
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
      console.log(`PASS: tabs, keyboard, hashes, reload, shared picker/session, save and layout at ${viewport.width}px`);
    }

    for (const hash of ["#summering", "#vakter"]) {
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
    vm.runInContext(source.replace("if (hasCredentials()) {", "if (false) {"), context);
    await assert.rejects(vm.runInContext('apiFetch("/api/admin/duties")', context), error => error.name === "TimeoutError");
    console.log("PASS: stalled response body also times out");
    console.log(`Screenshots: ${screenshots}`);
  } finally {
    await browser.close();
  }
}

run().catch(error => { console.error(error.message); process.exitCode = 1; });