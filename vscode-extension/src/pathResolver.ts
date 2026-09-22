/**
 * Pure(ish) logic for locating MdReader.exe.
 *
 * The resolution algorithm itself (`resolveMdReaderExecutable`) takes its file-system,
 * registry and environment access as injected dependencies so it can be unit tested
 * without touching the real registry or disk. The real implementations
 * (`fileExistsSync`, `queryAppPathsDefault`) live in this module too, but are thin
 * wrappers that `extension.ts` wires in; tests exercise the pure functions directly.
 *
 * The registry lookup deliberately does NOT shell out to `reg.exe` and parse its text
 * output. Two problems with that approach ruled it out:
 *   - `reg.exe` labels the key's unnamed default value with a localized string (e.g.
 *     "(Default)" in English, "(Par défaut)" in French, "(Standard)" in German), so
 *     matching a hardcoded English label breaks on non-English Windows installs.
 *   - `reg.exe` writes its console output in the OS's OEM/console code page (not UTF-8),
 *     but `child_process.execFile` decodes stdout as UTF-8 by default; there is no
 *     built-in Node API to decode an arbitrary Windows OEM code page without adding a
 *     runtime dependency (e.g. iconv-lite) that would then have to be bundled into the
 *     packaged extension. A non-ASCII install path (accented characters, CJK, etc.)
 *     would silently mis-decode and the lookup would wrongly fall through.
 *
 * Instead this queries the registry via a short, constant (no interpolated user data)
 * PowerShell command run through `execFile` (never a shell string): it reads the key's
 * default value with .NET's `RegistryKey.GetValue`, which is locale-independent (no
 * label text to parse), and sets `[Console]::OutputEncoding` to UTF-8 before writing the
 * result so `execFile`'s default UTF-8 stdout decoding round-trips non-ASCII paths
 * correctly (verified manually against a scratch registry key containing "MdRéader").
 * `DoNotExpandEnvironmentNames` is passed explicitly so a REG_EXPAND_SZ value (e.g.
 * containing `%LOCALAPPDATA%`) comes back raw — PowerShell's `Get-ItemProperty` would
 * otherwise auto-expand it using the child process's own environment, bypassing the
 * `expandEnvironmentVariables` step below (and its `env` dependency) entirely.
 *
 * `powershell.exe` itself is resolved to an absolute path (`resolvePowerShellExecutablePath`)
 * rather than spawned by bare name, so this doesn't depend on the caller's `PATH` (which a
 * minimal shell or test runner may not include `System32` on) or on libuv's Windows
 * executable search order, which also considers the current working directory.
 */

import { execFile } from "child_process";
import * as fs from "fs";
import * as path from "path";

export const APP_PATHS_SUBKEY = "Software\\Microsoft\\Windows\\CurrentVersion\\App Paths\\MdReader.exe";

export type RegistryHive = "HKCU" | "HKLM";

const HIVE_ROOTS: Record<RegistryHive, string> = {
    HKCU: "HKEY_CURRENT_USER",
    HKLM: "HKEY_LOCAL_MACHINE",
};

/**
 * Builds the default, well-known absolute path to Windows PowerShell 5.1
 * (`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`), using `SystemRoot` (or
 * `windir`, falling back to `C:\Windows`) from the given environment. Pure: does not touch
 * the filesystem.
 */
export function buildPowerShellCandidatePath(env: NodeJS.ProcessEnv): string {
    const systemRoot = env.SystemRoot || env.windir || "C:\\Windows";
    return path.join(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
}

/**
 * Resolves the executable to pass to `execFile` for running PowerShell commands.
 *
 * Spawning a bare `"powershell.exe"` makes resolution depend on the current process's
 * `PATH` — which a VS Code extension host normally has, but a stripped-down shell (or a
 * test runner invoked with a minimal `PATH`) may not — and on libuv's Windows executable
 * search, which (like `CreateProcess`) also considers the current working directory
 * before `PATH`, an untrustworthy place to resolve a program name from. Preferring the
 * well-known absolute path from `buildPowerShellCandidatePath` avoids both: it only falls
 * back to the bare name (still attempted via `PATH` as a last resort, rather than failing
 * outright) if that absolute path doesn't exist, e.g. an unusual Windows installation.
 */
export function resolvePowerShellExecutablePath(
    env: NodeJS.ProcessEnv,
    fileExists: (filePath: string) => boolean
): string {
    const candidate = buildPowerShellCandidatePath(env);
    return fileExists(candidate) ? candidate : "powershell.exe";
}

/**
 * Builds the PowerShell command that prints the given key's default (unnamed) value, raw
 * and unexpanded, or nothing if the key doesn't exist. `registryPath` must be a trusted,
 * constant string (it is embedded in a single-quoted PowerShell string as-is); callers in
 * this module only ever pass the fixed App Paths path for a fixed hive, never user data.
 */
export function buildRegistryDefaultValueCommand(registryPath: string): string {
    return (
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
        `$k = Get-Item -LiteralPath '${registryPath}' -ErrorAction SilentlyContinue; ` +
        "if ($k) { $k.GetValue('', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }"
    );
}

/**
 * Parses the stdout of the command `buildRegistryDefaultValueCommand` produces: the raw
 * value on its own line (PowerShell adds a trailing CRLF), or empty/whitespace-only output
 * when the key or its default value doesn't exist.
 */
export function parseRegistryDefaultValueOutput(stdout: string): string | undefined {
    const value = stdout.trim();
    return value.length > 0 ? value : undefined;
}

/** Expands `%VAR%`-style environment variable references. Unknown variables are left as-is. */
export function expandEnvironmentVariables(value: string, env: NodeJS.ProcessEnv): string {
    return value.replace(/%([^%]+)%/g, (original, name: string) => {
        const resolved = env[name];
        return resolved !== undefined ? resolved : original;
    });
}

export interface ResolveDependencies {
    /** Reads the raw `mdreader.executablePath` setting value (possibly empty). */
    getConfiguredPath(): string;
    /** True if the given path exists and is a file. */
    fileExists(filePath: string): boolean;
    /** Resolves the App Paths default value for the given hive; undefined if absent or on error. */
    queryAppPathsDefault(hive: RegistryHive): Promise<string | undefined>;
    /** Environment used to expand `%VAR%` references and to locate `%LOCALAPPDATA%`. */
    env: NodeJS.ProcessEnv;
}

/**
 * Resolves the MdReader executable using the documented precedence:
 *   1. the `mdreader.executablePath` setting, if non-empty and it exists;
 *   2. the `App Paths\MdReader.exe` registry default value, HKCU then HKLM, if it exists;
 *   3. `%LOCALAPPDATA%\Programs\MdReader\MdReader.exe`, if it exists.
 * Returns undefined if none of the above resolves to an existing file.
 */
export async function resolveMdReaderExecutable(deps: ResolveDependencies): Promise<string | undefined> {
    const configured = deps.getConfiguredPath().trim();
    if (configured.length > 0) {
        const expanded = expandEnvironmentVariables(configured, deps.env);
        if (deps.fileExists(expanded)) {
            return expanded;
        }
    }

    for (const hive of ["HKCU", "HKLM"] as const) {
        const raw = await deps.queryAppPathsDefault(hive);
        if (raw) {
            const expanded = expandEnvironmentVariables(raw, deps.env);
            if (deps.fileExists(expanded)) {
                return expanded;
            }
        }
    }

    const localAppData = deps.env.LOCALAPPDATA;
    if (localAppData) {
        const fallback = path.join(localAppData, "Programs", "MdReader", "MdReader.exe");
        if (deps.fileExists(fallback)) {
            return fallback;
        }
    }

    return undefined;
}

/** Real filesystem check used outside of tests. */
export function fileExistsSync(filePath: string): boolean {
    try {
        return fs.statSync(filePath).isFile();
    } catch {
        return false;
    }
}

/**
 * Real registry lookup used outside of tests. Runs the constant PowerShell command from
 * `buildRegistryDefaultValueCommand` via `execFile` (never a shell string; the command
 * text embeds only this module's own constant hive/subkey, never user data) and parses
 * its stdout. Resolves to undefined (never rejects) if the key/value is missing or the
 * command fails for any reason.
 */
export function queryAppPathsDefault(hive: RegistryHive): Promise<string | undefined> {
    const registryPath = `Registry::${HIVE_ROOTS[hive]}\\${APP_PATHS_SUBKEY}`;
    return queryRegistryDefaultValue(registryPath);
}

/**
 * Runs `buildRegistryDefaultValueCommand(registryPath)` via `powershell.exe` (through
 * `execFile`, never a shell string) and returns its parsed result. Exported separately
 * from `queryAppPathsDefault` so tests can point it at a disposable scratch registry key
 * instead of the real App Paths key, while production code only ever calls it with the
 * fixed App Paths path built above.
 */
export function queryRegistryDefaultValue(registryPath: string): Promise<string | undefined> {
    return new Promise((resolve) => {
        const command = buildRegistryDefaultValueCommand(registryPath);
        const powershellExe = resolvePowerShellExecutablePath(process.env, fileExistsSync);
        execFile(
            powershellExe,
            ["-NoProfile", "-NonInteractive", "-Command", command],
            { windowsHide: true },
            (error, stdout) => {
                if (error) {
                    resolve(undefined);
                    return;
                }
                resolve(parseRegistryDefaultValueOutput(stdout));
            }
        );
    });
}
