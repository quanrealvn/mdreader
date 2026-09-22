import { execFile } from "node:child_process";
import assert from "node:assert/strict";
import { test } from "node:test";
import {
    buildPowerShellCandidatePath,
    buildRegistryDefaultValueCommand,
    expandEnvironmentVariables,
    fileExistsSync,
    parseRegistryDefaultValueOutput,
    queryRegistryDefaultValue,
    resolveMdReaderExecutable,
    resolvePowerShellExecutablePath,
    type ResolveDependencies,
} from "../pathResolver";

// ---------------------------------------------------------------------------------------
// buildPowerShellCandidatePath / resolvePowerShellExecutablePath
//
// These resolve powershell.exe to an absolute path instead of a bare name, so the
// registry lookup doesn't depend on the caller's PATH (a stripped-down shell, or this
// very test runner invoked with a minimal PATH, may not have System32 on it) or on
// libuv's Windows executable search order, which also considers the current directory.
// ---------------------------------------------------------------------------------------

test("buildPowerShellCandidatePath uses SystemRoot", () => {
    assert.equal(
        buildPowerShellCandidatePath({ SystemRoot: "C:\\Windows" }),
        "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"
    );
});

test("buildPowerShellCandidatePath falls back to windir when SystemRoot is unset", () => {
    assert.equal(
        buildPowerShellCandidatePath({ windir: "D:\\WINNT" }),
        "D:\\WINNT\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"
    );
});

test("buildPowerShellCandidatePath falls back to C:\\Windows when neither is set", () => {
    assert.equal(
        buildPowerShellCandidatePath({}),
        "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"
    );
});

test("buildPowerShellCandidatePath prefers SystemRoot over windir", () => {
    assert.equal(
        buildPowerShellCandidatePath({ SystemRoot: "C:\\Windows", windir: "C:\\WINNT" }),
        "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"
    );
});

test("resolvePowerShellExecutablePath returns the absolute candidate when it exists", () => {
    const env = { SystemRoot: "C:\\Windows" };
    const candidate = buildPowerShellCandidatePath(env);
    const result = resolvePowerShellExecutablePath(env, (p) => p === candidate);
    assert.equal(result, candidate);
});

test("resolvePowerShellExecutablePath falls back to the bare name when the candidate does not exist", () => {
    const result = resolvePowerShellExecutablePath({ SystemRoot: "C:\\Windows" }, () => false);
    assert.equal(result, "powershell.exe");
});

// ---------------------------------------------------------------------------------------
// Pure functions: buildRegistryDefaultValueCommand / parseRegistryDefaultValueOutput
// ---------------------------------------------------------------------------------------

test("buildRegistryDefaultValueCommand embeds the registry path and requests a raw, unexpanded, UTF-8 value", () => {
    const command = buildRegistryDefaultValueCommand("Registry::HKEY_CURRENT_USER\\Software\\Test Key");
    assert.match(command, /Registry::HKEY_CURRENT_USER\\Software\\Test Key/);
    assert.match(command, /DoNotExpandEnvironmentNames/);
    assert.match(command, /OutputEncoding=\[Text\.Encoding\]::UTF8/);
});

test("parseRegistryDefaultValueOutput trims the trailing CRLF PowerShell adds", () => {
    assert.equal(
        parseRegistryDefaultValueOutput("C:\\Users\\me\\AppData\\Local\\Programs\\MdReader\\MdReader.exe\r\n"),
        "C:\\Users\\me\\AppData\\Local\\Programs\\MdReader\\MdReader.exe"
    );
});

test("parseRegistryDefaultValueOutput returns undefined for empty or whitespace-only output (missing key/value)", () => {
    assert.equal(parseRegistryDefaultValueOutput(""), undefined);
    assert.equal(parseRegistryDefaultValueOutput("\r\n"), undefined);
    assert.equal(parseRegistryDefaultValueOutput("   \r\n"), undefined);
});

test("parseRegistryDefaultValueOutput passes non-ASCII content through unchanged (no locale-specific label to parse)", () => {
    // Unlike `reg query`, this command never prints a localized "(Default)"/"(Par défaut)"/
    // "(Standard)" label — .NET's RegistryKey.GetValue returns just the raw value, so
    // there is no label text whose locale could break parsing.
    const nonAscii = "C:\\Users\\日本語\\MdRéader\\MdReader.exe";
    assert.equal(parseRegistryDefaultValueOutput(nonAscii + "\r\n"), nonAscii);
});

// ---------------------------------------------------------------------------------------
// expandEnvironmentVariables
// ---------------------------------------------------------------------------------------

test("expandEnvironmentVariables substitutes known variables", () => {
    const env = { LOCALAPPDATA: "C:\\Users\\me\\AppData\\Local" };
    assert.equal(
        expandEnvironmentVariables("%LOCALAPPDATA%\\Programs\\MdReader\\MdReader.exe", env),
        "C:\\Users\\me\\AppData\\Local\\Programs\\MdReader\\MdReader.exe"
    );
});

test("expandEnvironmentVariables leaves unknown variables untouched", () => {
    const env = {};
    assert.equal(expandEnvironmentVariables("%NOT_SET%\\MdReader.exe", env), "%NOT_SET%\\MdReader.exe");
});

// ---------------------------------------------------------------------------------------
// resolveMdReaderExecutable (full pipeline, injected dependencies)
// ---------------------------------------------------------------------------------------

function makeDeps(overrides: Partial<ResolveDependencies>): ResolveDependencies {
    return {
        getConfiguredPath: () => "",
        fileExists: () => false,
        queryAppPathsDefault: async () => undefined,
        env: {},
        ...overrides,
    };
}

test("resolveMdReaderExecutable prefers a valid configured setting", async () => {
    const deps = makeDeps({
        getConfiguredPath: () => "D:\\Custom\\MdReader.exe",
        fileExists: (p) => p === "D:\\Custom\\MdReader.exe",
        queryAppPathsDefault: async () => {
            throw new Error("should not be called when the setting resolves");
        },
    });
    assert.equal(await resolveMdReaderExecutable(deps), "D:\\Custom\\MdReader.exe");
});

test("resolveMdReaderExecutable ignores a configured setting that does not exist on disk", async () => {
    const deps = makeDeps({
        getConfiguredPath: () => "D:\\Missing\\MdReader.exe",
        fileExists: (p) => p === "C:\\FromRegistry\\MdReader.exe",
        queryAppPathsDefault: async (hive) => (hive === "HKCU" ? "C:\\FromRegistry\\MdReader.exe" : undefined),
    });
    assert.equal(await resolveMdReaderExecutable(deps), "C:\\FromRegistry\\MdReader.exe");
});

test("resolveMdReaderExecutable falls back from HKCU to HKLM", async () => {
    const deps = makeDeps({
        fileExists: (p) => p === "C:\\FromHklm\\MdReader.exe",
        queryAppPathsDefault: async (hive) => {
            if (hive === "HKCU") return "C:\\FromHkcuButMissing\\MdReader.exe";
            if (hive === "HKLM") return "C:\\FromHklm\\MdReader.exe";
            return undefined;
        },
    });
    assert.equal(await resolveMdReaderExecutable(deps), "C:\\FromHklm\\MdReader.exe");
});

test("resolveMdReaderExecutable expands environment variables from the registry value", async () => {
    const deps = makeDeps({
        env: { ProgramFiles: "C:\\Program Files" },
        fileExists: (p) => p === "C:\\Program Files\\MdReader\\MdReader.exe",
        queryAppPathsDefault: async (hive) => (hive === "HKCU" ? "%ProgramFiles%\\MdReader\\MdReader.exe" : undefined),
    });
    assert.equal(await resolveMdReaderExecutable(deps), "C:\\Program Files\\MdReader\\MdReader.exe");
});

test("resolveMdReaderExecutable expands a REG_EXPAND_SZ %LOCALAPPDATA% value using the injected env (non-ASCII path)", async () => {
    // Guards against regressing to PowerShell's own Get-ItemProperty auto-expansion, which
    // would use the child process's environment instead of `deps.env` and make this
    // resolution step untestable/non-deterministic.
    const deps = makeDeps({
        env: { LOCALAPPDATA: "C:\\Users\\日本語\\AppData\\Local" },
        fileExists: (p) => p === "C:\\Users\\日本語\\AppData\\Local\\Programs\\MdReader\\MdReader.exe",
        queryAppPathsDefault: async (hive) =>
            hive === "HKCU" ? "%LOCALAPPDATA%\\Programs\\MdReader\\MdReader.exe" : undefined,
    });
    assert.equal(
        await resolveMdReaderExecutable(deps),
        "C:\\Users\\日本語\\AppData\\Local\\Programs\\MdReader\\MdReader.exe"
    );
});

test("resolveMdReaderExecutable falls back to the default install location", async () => {
    const deps = makeDeps({
        env: { LOCALAPPDATA: "C:\\Users\\me\\AppData\\Local" },
        fileExists: (p) => p === "C:\\Users\\me\\AppData\\Local\\Programs\\MdReader\\MdReader.exe",
    });
    assert.equal(
        await resolveMdReaderExecutable(deps),
        "C:\\Users\\me\\AppData\\Local\\Programs\\MdReader\\MdReader.exe"
    );
});

test("resolveMdReaderExecutable returns undefined when nothing resolves", async () => {
    const deps = makeDeps({ env: { LOCALAPPDATA: "C:\\Users\\me\\AppData\\Local" } });
    assert.equal(await resolveMdReaderExecutable(deps), undefined);
});

// ---------------------------------------------------------------------------------------
// queryRegistryDefaultValue integration tests: exercise the real powershell.exe command
// against disposable scratch registry keys (never the real MdReader App Paths key).
// Skipped on non-Windows hosts, where the module isn't used anyway.
// ---------------------------------------------------------------------------------------

const SCRATCH_KEY_SUBPATH = "Software\\MdReaderVSCodeExtensionScratchTest";
const SCRATCH_KEY_PSPATH = `HKCU:\\${SCRATCH_KEY_SUBPATH}`;
const SCRATCH_REGISTRY_PATH = `Registry::HKEY_CURRENT_USER\\${SCRATCH_KEY_SUBPATH}`;
const MISSING_REGISTRY_PATH = "Registry::HKEY_CURRENT_USER\\Software\\MdReaderVSCodeExtensionScratchTestDoesNotExist";

function runPowerShell(command: string): Promise<string> {
    return new Promise((resolve, reject) => {
        // Use the same PATH-independent resolution as the production code (see
        // `resolvePowerShellExecutablePath`), so this scratch-key setup/teardown helper
        // doesn't fail under a minimal PATH either.
        const powershellExe = resolvePowerShellExecutablePath(process.env, fileExistsSync);
        execFile(
            powershellExe,
            ["-NoProfile", "-NonInteractive", "-Command", command],
            { windowsHide: true },
            (error, stdout, stderr) => {
                if (error) {
                    reject(new Error(`${error.message}\n${stderr}`));
                    return;
                }
                resolve(stdout);
            }
        );
    });
}

async function withScratchRegistryDefaultValue<T>(
    value: string,
    valueType: "String" | "ExpandString",
    fn: () => Promise<T>
): Promise<T> {
    await runPowerShell(
        `New-Item -Path '${SCRATCH_KEY_PSPATH}' -Force | Out-Null; ` +
            `Set-ItemProperty -LiteralPath '${SCRATCH_KEY_PSPATH}' -Name '(Default)' -Value '${value}' -Type ${valueType}`
    );
    try {
        return await fn();
    } finally {
        await runPowerShell(`Remove-Item -Path '${SCRATCH_KEY_PSPATH}' -Force -Recurse -ErrorAction SilentlyContinue`);
    }
}

test("queryRegistryDefaultValue (integration): reads a non-ASCII REG_SZ value from the real registry", async (t) => {
    if (process.platform !== "win32") {
        t.skip("Windows-only");
        return;
    }
    const nonAsciiValue = "C:\\Users\\test\\AppData\\Local\\Programs\\MdRéader\\MdReader.exe";
    await withScratchRegistryDefaultValue(nonAsciiValue, "String", async () => {
        const result = await queryRegistryDefaultValue(SCRATCH_REGISTRY_PATH);
        assert.equal(result, nonAsciiValue);
    });
});

test("queryRegistryDefaultValue (integration): REG_EXPAND_SZ with %LOCALAPPDATA% comes back raw, not pre-expanded", async (t) => {
    if (process.platform !== "win32") {
        t.skip("Windows-only");
        return;
    }
    const raw = "%LOCALAPPDATA%\\Programs\\MdReader\\MdReader.exe";
    await withScratchRegistryDefaultValue(raw, "ExpandString", async () => {
        const result = await queryRegistryDefaultValue(SCRATCH_REGISTRY_PATH);
        // Must be the literal, unexpanded string — proves DoNotExpandEnvironmentNames took
        // effect and this test isn't accidentally passing because it happens to match the
        // current process's own %LOCALAPPDATA%.
        assert.equal(result, raw);
    });
});

test("queryRegistryDefaultValue (integration): returns undefined for a key that does not exist", async (t) => {
    if (process.platform !== "win32") {
        t.skip("Windows-only");
        return;
    }
    const result = await queryRegistryDefaultValue(MISSING_REGISTRY_PATH);
    assert.equal(result, undefined);
});
