.pragma library

// Mirrors DownloadInstallDialog's component rules and Core/Install/PclLoaderRules.
function parseVersion(text, exact) {
    const match = String(text).match(exact ? /^\d+(?:\.\d+){1,3}$/ : /\d+(?:\.\d+){1,3}/);
    if (!match) return null;
    const parts = match[0].split(".").map(Number);
    while (parts.length < 4) parts.push(-1);
    return parts;
}
function compare(a, b) {
    for (let i = 0; i < 4; ++i) { if (a[i] !== b[i]) return a[i] > b[i] ? 1 : -1; }
    return 0;
}
function primary(chosen) {
    for (const key of ["Forge", "NeoForge", "Fabric"]) if (chosen[key]) return key;
    return "";
}
function supportsOptiFineWithFabric(game) {
    const version = parseVersion(game, false);
    return !version || compare(version, parseVersion("1.20.4", true)) <= 0;
}
function unsupportedForgeRange(game) {
    const version = parseVersion(game, false);
    return !!version && compare(version, parseVersion("1.13", true)) >= 0 && compare(version, parseVersion("1.14.3", true)) <= 0;
}
function supportsOptiFabric(game) {
    const version = parseVersion(game, false);
    return !version || compare(version, parseVersion("1.14", true)) < 0 || compare(version, parseVersion("1.16", true)) >= 0;
}
function forgeCompatible(row, game, forge) {
    if (!row || row.compatibility === undefined || row.compatibility === null) return false;
    const required = String(row.compatibility);
    if (!required.trim()) return true;
    if (required.toUpperCase().indexOf("N/A") >= 0) return false;
    const prefix = String(forge).match(/^(\d+(?:\.\d+){1,2})-/);
    if (prefix && prefix[1].toLowerCase() !== String(game).toLowerCase()) return false;
    const actual = prefix ? String(forge).slice(prefix[0].length) : String(forge);
    const match = required.match(/\d+(?:\.\d+)*/);
    if (!match) return false;
    if (match[0].indexOf(".") >= 0) {
        const a = parseVersion(actual, true), b = parseVersion(match[0], true);
        return !!a && !!b && compare(a, b) === 0;
    }
    return actual.split(".").pop().toLowerCase() === match[0].toLowerCase();
}
function visible(game, key, chosen) {
    const loader = primary(chosen);
    if (key === "LiteLoader") {
        const version = parseVersion(String(game).split(/[-+]/)[0], true);
        return !version || compare(version, parseVersion("1.13", true)) < 0;
    }
    if (key === "Fabric API") return loader === "Fabric";
    if (key === "OptiFabric") return loader === "Fabric" && !!chosen.OptiFine && supportsOptiFabric(game);
    return true;
}
function blocked(game, key, chosen, cache) {
    const loader = primary(chosen), opti = chosen.OptiFine;
    if (["Forge", "NeoForge", "Fabric"].indexOf(key) >= 0) {
        if (loader && loader !== key) return "与 " + loader + " 不兼容";
        if (key === "NeoForge" && opti) return "与 OptiFine 不兼容";
        if (key === "Fabric" && opti && !supportsOptiFineWithFabric(game)) return "与 OptiFine 不兼容";
        if (key === "Forge" && opti && unsupportedForgeRange(game)) return "与 OptiFine 不兼容";
    }
    if (key === "OptiFine") {
        if (loader === "NeoForge") return "与 NeoForge 不兼容";
        if (loader === "Fabric" && !supportsOptiFineWithFabric(game)) return "与 Fabric 不兼容";
        if (loader === "Forge" && unsupportedForgeRange(game)) return "与 Forge 不兼容";
        const versions = cache[game + "/OptiFine"];
        if (loader === "Forge" && versions && !versions.some(row => forgeCompatible(row, game, chosen.Forge.id || chosen.Forge.version))) return "无兼容的 Forge 版本";
    }
    return "";
}
function rows(game, key, chosen, cache) {
    let result = cache[game + "/" + key] || [];
    const loader = primary(chosen);
    if (key === "OptiFine") {
        if (loader === "Forge") result = result.filter(row => forgeCompatible(row, game, chosen.Forge.id || chosen.Forge.version));
        if (loader === "NeoForge" || loader === "Fabric" && !supportsOptiFineWithFabric(game)) result = [];
    }
    if (key === "Forge" && chosen.OptiFine) result = result.filter(row => !unsupportedForgeRange(game) && forgeCompatible(chosen.OptiFine, game, row.version));
    if (!result.length) return [];
    const latest = result.find(row => row.isStable) || result[0];
    const recommended = key === "Forge" ? result.find(row => row.isRecommended) : null;
    const display = [];
    display.push(Object.assign({}, latest, {badge: "最新版本"}));
    if (recommended && recommended.version !== latest.version) display.push(Object.assign({}, recommended, {badge: "推荐版"}));
    display.push({section: true, title: "全部版本 (" + result.length + ")"});
    for (const row of result) display.push(Object.assign({}, row, {badge: chosen[key] && (chosen[key].id === row.id && !!row.id || chosen[key].version === row.version) ? "✓" : ""}));
    return display;
}
function suggestedName(game, chosen) {
    let name = game;
    const loader = primary(chosen);
    if (loader) {
        const version = chosen[loader].id || chosen[loader].version;
        name += loader === "Fabric" ? "-Fabric " + version.split("+")[0] : loader === "Forge" ? "-Forge_" + version : "-NeoForge_" + version.split("+")[0];
    }
    if (chosen.LiteLoader) name += "-LiteLoader";
    if (chosen.OptiFine) {
        const version = chosen.OptiFine.version;
        name += "-OptiFine_" + (version.toLowerCase().indexOf((game + " ").toLowerCase()) === 0 ? version.slice(game.length + 1) : version).replace(/ /g, "_");
    }
    return name;
}
function validate(game, chosen) {
    const loader = primary(chosen);
    if (chosen["Fabric API"] && loader !== "Fabric") return "Fabric API 需要同时选择 Fabric。";
    if (chosen.OptiFabric && (loader !== "Fabric" || !chosen.OptiFine)) return "OptiFabric 需要同时选择 Fabric 和 OptiFine。";
    if (chosen.OptiFine && loader === "NeoForge") return "与 NeoForge 不兼容";
    if (chosen.OptiFine && loader === "Fabric" && !supportsOptiFineWithFabric(game)) return "与 Fabric 不兼容";
    if (chosen.OptiFine && loader === "Forge" && (unsupportedForgeRange(game) || !forgeCompatible(chosen.OptiFine, game, chosen.Forge.id || chosen.Forge.version))) return "所选 OptiFine 与 Forge 不兼容。";
    return "";
}
