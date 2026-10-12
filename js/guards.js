// Shared by the public site and admin so both name pickers list the same guards in the same order.
export function normalizeGuardName(name) {
  return String(name).trim().replace(/\s+/g, " ").toLocaleLowerCase("nb");
}

export async function fetchGuardNames() {
  const response = await fetch("./data/guards.json", { cache: "no-cache" });
  if (!response.ok) throw new Error("Kunne ikke laste navnelisten.");
  return response.json();
}

// data/guards.json decides which names are shown and in which order; the backend supplies the IDs.
export function buildGuardEntries(staticGuards, apiGuards) {
  const apiByName = new Map(apiGuards.filter(guard => guard.active !== false).map(guard => [normalizeGuardName(guard.name), guard]));
  return staticGuards.map(guard => ({ name: guard.name, api: apiByName.get(normalizeGuardName(guard.name)) ?? null }));
}
