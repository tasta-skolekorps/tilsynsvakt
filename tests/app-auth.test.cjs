const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || "playwright");

const baseUrl = process.env.APP_TEST_URL || process.env.ADMIN_TEST_URL || "http://127.0.0.1:8080";
// Mocked API origin; config.js is replaced so the test never reaches a real backend.
const apiOrigin = "https://api.example.test";
const fixturePassword = "fixture-only-password";
const fixtureKey = "tv1.fixture-key";
const guards = [{ id: 1, name: "Testvakt" }, { id: 2, name: "Neste vakt" }];
// Tuesday 06.10.2026 in Oslo, inside the autumn period.
const fixedNow = new Date("2026-10-06T12:00:00+02:00");

function problem(status, code, title, detail) {
  return { status, contentType: "application/problem+json", body: JSON.stringify({ status, code, title, detail }) };
}

async function run() {
  const browser = await chromium.launch({ headless: true, executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH });
  const screenshots = fs.mkdtempSync(path.join(os.tmpdir(), "tilsynsvakt-app-auth-"));
  try {
    async function openPage({ viewport = { width: 390, height: 844 }, storage = {}, login, api } = {}) {
      const context = await browser.newContext({ viewport, serviceWorkers: "block" });
      const page = await context.newPage();
      const errors = [];
      const requests = [];
      page.on("pageerror", error => errors.push(error.message));
      await page.clock.setFixedTime(fixedNow);
      await context.addInitScript(entries => {
        if (sessionStorage.getItem("fixture-seeded")) return;
        sessionStorage.setItem("fixture-seeded", "1");
        for (const [key, value] of Object.entries(entries)) localStorage.setItem(key, value);
      }, storage);
      await page.route("**/config.js", route => route.fulfill({
        contentType: "application/javascript",
        body: `window.TILSYNSVAKT_CONFIG = { apiBaseUrl: "${apiOrigin}" };`,
      }));
      await page.route(`${apiOrigin}/**`, async route => {
        const request = route.request();
        const url = new URL(request.url());
        requests.push({ pathname: url.pathname, method: request.method(), apiKey: request.headers()["x-api-key"] ?? null, body: request.postData() });
        if (url.pathname === "/api/auth/login") {
          if (login) return login(route, request);
          const body = JSON.parse(request.postData() || "{}");
          return body.password === fixturePassword
            ? route.fulfill({ json: { apiKey: fixtureKey }, headers: { "Cache-Control": "no-store" } })
            : route.fulfill(problem(401, "invalid_password", "Feil passord", "Passordet er feil. Prøv igjen."));
        }
        if (request.headers()["x-api-key"] !== fixtureKey) {
          return route.fulfill(problem(401, "access_key_required", "Innlogging kreves", "Logg inn med passordet for å se vaktlisten."));
        }
        if (api) {
          const handled = await api(route, url);
          if (handled !== false) return;
        }
        if (url.pathname === "/api/guards") return route.fulfill({ json: guards });
        if (url.pathname === "/api/shifts") return route.fulfill({ json: { shifts: [] } });
        if (url.pathname === "/api/swap-requests") return route.fulfill({ json: [] });
        return route.fulfill({ status: 404, json: {} });
      });
      await page.goto(`${baseUrl}/index.html`);
      return { context, page, errors, requests };
    }

    const storedValues = page => page.evaluate(() => Object.fromEntries(Object.keys(localStorage).map(key => [key, localStorage.getItem(key)])));
    const apiRequests = requests => requests.filter(request => request.pathname !== "/api/auth/login");

    // No key: login panel shown, no API calls, stale guard cache not used.
    {
      const { context, page, errors, requests } = await openPage({
        storage: { "tilsynsvakt.guards": JSON.stringify(guards), "tilsynsvakt.checklist": JSON.stringify({ "2026-10-06": [0] }) },
      });
      await page.locator("#login-panel").waitFor({ state: "visible" });
      await page.waitForTimeout(300);
      assert.deepEqual(apiRequests(requests), [], "no API request without a key");
      assert.equal(await page.locator("#logout").isVisible(), false);
      assert.equal(await page.locator("#login-password").evaluate(element => element === document.activeElement), true);
      assert.equal(await page.locator("#login-password").getAttribute("type"), "password");
      const stored = await storedValues(page);
      assert.equal(stored["tilsynsvakt.guards"], undefined, "stale guard cache removed");
      assert.equal(stored["tilsynsvakt.apiKey"], undefined);
      assert.equal(await page.locator("#guard-select option").count(), 1, "no cached names offered");
      assert.match(await page.locator("#login-panel").innerText(), /924 23 946/);
      assert.equal(await page.locator('#login-panel a[href="tel:+4792423946"]').count(), 1);
      const box = await page.locator("#login-form button[type=submit]").boundingBox();
      assert.ok(box.height >= 44, `login button tap target ${box.height}px`);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
      await page.screenshot({ path: path.join(screenshots, "login-390.png"), fullPage: true });

      await page.locator("#login-form button[type=submit]").click();
      assert.equal(await page.locator("#login-error").innerText(), "Skriv inn passordet.");
      assert.equal(requests.length, 0, "empty password is not submitted");
      assert.deepEqual(errors, []);
      await context.close();
      console.log("PASS: without key the login panel is shown and the API is not called");
    }

    // Wrong password, then correct password.
    {
      const { context, page, errors, requests } = await openPage();
      await page.locator("#login-password").fill("wrong-fixture");
      await page.locator("#login-form button[type=submit]").click();
      await page.locator("#login-error").waitFor({ state: "visible" });
      assert.equal(await page.locator("#login-error").innerText(), "Feil passord. Prøv igjen.");
      assert.equal(await page.locator("#login-panel").isVisible(), true);
      assert.equal((await storedValues(page))["tilsynsvakt.apiKey"], undefined);
      assert.equal(await page.locator("#login-password").inputValue(), "");

      await page.locator("#login-password").fill(fixturePassword);
      await page.locator("#login-form button[type=submit]").click();
      await page.locator("#login-panel").waitFor({ state: "hidden" });
      await page.waitForFunction(() => document.querySelectorAll("#guard-select option").length > 1);
      const stored = await storedValues(page);
      assert.equal(stored["tilsynsvakt.apiKey"], fixtureKey);
      assert.ok(Object.values(stored).every(value => !value.includes(fixturePassword)), "password never stored in localStorage");
      assert.ok(await page.evaluate(password => !Object.values(sessionStorage).some(value => value.includes(password)), fixturePassword));
      assert.equal(await page.locator("#login-password").inputValue(), "");
      assert.equal(await page.locator("#login-error").isVisible(), false);
      assert.equal(await page.locator("#logout").isVisible(), true);

      const logins = requests.filter(request => request.pathname === "/api/auth/login");
      assert.equal(logins.length, 2);
      assert.ok(logins.every(request => request.method === "POST" && request.apiKey === null), "login sent without X-Api-Key");
      assert.deepEqual(JSON.parse(logins[1].body), { password: fixturePassword });
      const after = apiRequests(requests);
      assert.ok(after.some(request => request.pathname === "/api/guards"));
      assert.ok(after.some(request => request.pathname === "/api/shifts"));
      assert.ok(after.every(request => request.apiKey === fixtureKey), "every API request carries X-Api-Key");
      assert.deepEqual(errors, []);
      await page.screenshot({ path: path.join(screenshots, "logged-in-390.png"), fullPage: true });

      // Reload keeps the session from localStorage.
      const before = requests.length;
      await page.reload();
      await page.waitForFunction(() => document.querySelectorAll("#guard-select option").length > 1);
      assert.equal(await page.locator("#login-panel").isVisible(), false);
      assert.equal(requests.slice(before).some(request => request.pathname === "/api/auth/login"), false);
      assert.ok(requests.slice(before).every(request => request.apiKey === fixtureKey));
      await context.close();
      console.log("PASS: wrong password message, login stores key, API calls carry X-Api-Key, reload stays logged in");
    }

    // Rate limited and network failures on login.
    for (const [name, login, expected] of [
      ["429", route => route.fulfill(problem(429, "rate_limited", "For mange forespørsler", "Vent litt.")), "For mange forsøk. Vent litt og prøv igjen."],
      ["network failure", route => route.abort("failed"), "Får ikke kontakt med vaktlisten. Kontroller nettforbindelsen og prøv igjen."],
    ]) {
      const { context, page, errors } = await openPage({ login });
      await page.locator("#login-password").fill(fixturePassword);
      await page.locator("#login-form button[type=submit]").click();
      await page.locator("#login-error").waitFor({ state: "visible" });
      assert.equal(await page.locator("#login-error").innerText(), expected);
      assert.equal(await page.locator("#login-form button[type=submit]").isDisabled(), false);
      assert.equal((await storedValues(page))["tilsynsvakt.apiKey"], undefined);
      assert.deepEqual(errors, []);
      await context.close();
      console.log(`PASS: login ${name} shows a Norwegian message`);
    }

    // Button shows progress while the login request is pending.
    {
      let release;
      const pending = new Promise(resolve => { release = resolve; });
      const { context, page } = await openPage({ login: async route => { await pending; await route.fulfill({ json: { apiKey: fixtureKey } }); } });
      await page.locator("#login-password").fill(fixturePassword);
      await page.locator("#login-form button[type=submit]").click();
      const button = page.locator("#login-form button[type=submit]");
      await page.waitForFunction(() => document.querySelector("#login-form button[type=submit]").disabled);
      assert.equal((await button.textContent()).trim(), "Logger inn …");
      release();
      await page.locator("#login-panel").waitFor({ state: "hidden" });
      assert.equal((await button.textContent()).trim(), "Logg inn");
      await context.close();
      console.log("PASS: login button shows progress and is disabled while pending");
    }

    // A rejected stored key sends the user back to login.
    {
      const { context, page, errors } = await openPage({
        storage: {
          "tilsynsvakt.apiKey": "tv1.revoked-key",
          "tilsynsvakt.guards": JSON.stringify(guards),
          "tilsynsvakt.guard": JSON.stringify(guards[0]),
        },
      });
      await page.locator("#login-panel").waitFor({ state: "visible" });
      await page.waitForFunction(() => !document.querySelector("#notice").hidden);
      assert.match(await page.locator("#notice").innerText(), /Du må logge inn på nytt\./);
      const stored = await storedValues(page);
      assert.equal(stored["tilsynsvakt.apiKey"], undefined);
      assert.equal(stored["tilsynsvakt.guards"], undefined);
      assert.equal(await page.locator("#logout").isVisible(), false);
      assert.equal(await page.locator("#guard-select option").count(), 1, "stale names not shown after rejection");
      assert.deepEqual(errors, []);
      await context.close();
      console.log("PASS: access_key_required removes the key and shows the login panel");
    }

    // Logout clears session data but keeps the checklist.
    {
      const checklistValue = JSON.stringify({ "2026-10-06": [0] });
      const { context, page, errors } = await openPage({
        viewport: { width: 1280, height: 900 },
        storage: {
          "tilsynsvakt.apiKey": fixtureKey,
          "tilsynsvakt.guard": JSON.stringify(guards[0]),
          "tilsynsvakt.checklist": checklistValue,
        },
      });
      await page.waitForFunction(() => document.querySelectorAll("#guard-select option").length > 1);
      assert.equal(await page.locator("#login-panel").isVisible(), false);
      assert.equal(await page.locator("#guard-select").inputValue(), "1");
      await page.locator("#logout").click();
      await page.locator("#login-panel").waitFor({ state: "visible" });
      const stored = await storedValues(page);
      assert.equal(stored["tilsynsvakt.apiKey"], undefined);
      assert.equal(stored["tilsynsvakt.guards"], undefined);
      assert.equal(stored["tilsynsvakt.guard"], undefined);
      assert.ok(stored["tilsynsvakt.checklist"], "checklist progress is kept");
      assert.match(await page.locator("#notice").innerText(), /Du er logget ut\./);
      assert.equal(await page.locator("#logout").isVisible(), false);
      assert.equal(await page.locator("#guard-select option").count(), 1);
      assert.deepEqual(errors, []);
      await context.close();
      console.log("PASS: logout clears apiKey, guard cache and selected guard");
    }

    console.log(`Screenshots: ${screenshots}`);
  } finally {
    await browser.close();
  }
}

run().catch(error => { console.error(error.stack || error.message); process.exitCode = 1; });
