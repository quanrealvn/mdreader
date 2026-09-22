import { spawn } from "child_process";
import * as path from "path";
import * as vscode from "vscode";
import { fileExistsSync, queryAppPathsDefault, resolveMdReaderExecutable } from "./pathResolver";

const MARKDOWN_EXTENSIONS = new Set([".md", ".markdown", ".mdown", ".mkd", ".mkdn"]);
const COMMAND_ID = "mdreader.openInMdReader";

export function activate(context: vscode.ExtensionContext): void {
    context.subscriptions.push(
        vscode.commands.registerCommand(COMMAND_ID, (uri?: vscode.Uri, uris?: vscode.Uri[]) =>
            openInMdReader(uri, uris)
        )
    );
}

// eslint-disable-next-line @typescript-eslint/no-empty-function -- nothing to dispose: launched
// MdReader processes are detached and intentionally outlive this extension/window.
export function deactivate(): void {}

async function openInMdReader(uri?: vscode.Uri, uris?: vscode.Uri[]): Promise<void> {
    if (process.platform !== "win32") {
        void vscode.window.showErrorMessage(
            "MdReader is a Windows application. \"Open in MdReader\" only works when VS Code itself is " +
                "running on Windows (for a remote/WSL workspace, run VS Code Desktop on Windows)."
        );
        return;
    }

    const targets = resolveTargetUris(uri, uris);
    if (targets.length === 0) {
        void vscode.window.showErrorMessage("No Markdown file is selected to open in MdReader.");
        return;
    }

    const localTargets = targets.filter((target) => target.scheme === "file");
    const skipped = targets.length - localTargets.length;
    if (skipped > 0) {
        void vscode.window.showWarningMessage(
            localTargets.length > 0
                ? `MdReader runs on your local machine and can't open ${skipped} file(s) from a remote ` +
                      "or virtual file system; opening the remaining local file(s) only."
                : "MdReader runs on your local machine and can't open files from a remote or virtual " +
                      "file system. Open the file from a workspace on this machine instead."
        );
    }
    if (localTargets.length === 0) {
        return;
    }

    for (const target of localTargets) {
        notifyIfUnsaved(target);
    }

    const exePath = await resolveExecutable();
    if (!exePath) {
        const choice = await vscode.window.showErrorMessage(
            "MdReader.exe was not found. Install MdReader, or set its location in Settings.",
            "Configure Path"
        );
        if (choice === "Configure Path") {
            await vscode.commands.executeCommand("workbench.action.openSettings", "mdreader.executablePath");
        }
        return;
    }

    launchMdReader(
        exePath,
        localTargets.map((target) => target.fsPath)
    );
}

/**
 * Determines which files the command should open, given the arguments VS Code passes
 * for the menu/command that triggered it:
 *   - explorer/context with a multi-selection: `uris` holds every selected resource;
 *     it is filtered to Markdown-looking files so a mixed selection only opens the
 *     Markdown ones (falling back to the right-clicked file if none look like Markdown).
 *   - explorer/context or editor/title(/context) with a single resource: `uri` (VS Code
 *     may also pass a one-element `uris`).
 *   - command palette: no arguments; use the active editor's document (the command's
 *     `when` clause already restricts the palette entry to Markdown editors).
 */
function resolveTargetUris(uri?: vscode.Uri, uris?: vscode.Uri[]): vscode.Uri[] {
    if (uris && uris.length > 1) {
        const markdownOnly = uris.filter((candidate) => isMarkdownExtension(candidate.fsPath));
        if (markdownOnly.length > 0) {
            return markdownOnly;
        }
        return uri ? [uri] : uris;
    }
    if (uris && uris.length === 1) {
        return uris;
    }
    if (uri) {
        return [uri];
    }
    const active = vscode.window.activeTextEditor;
    return active ? [active.document.uri] : [];
}

function isMarkdownExtension(fsPath: string): boolean {
    return MARKDOWN_EXTENSIONS.has(path.extname(fsPath).toLowerCase());
}

/**
 * If `uri` is currently open with unsaved changes, tells the user MdReader will show the
 * last-saved contents (and reload once they save) and offers a one-click Save. This is an
 * informational nudge only: it never blocks or delays launching MdReader.
 */
function notifyIfUnsaved(uri: vscode.Uri): void {
    const target = uri.toString();
    const document = vscode.workspace.textDocuments.find((doc) => doc.isDirty && doc.uri.toString() === target);
    if (!document) {
        return;
    }
    void (async () => {
        const choice = await vscode.window.showInformationMessage(
            "MdReader shows the saved file and reloads automatically when you save.",
            "Save"
        );
        if (choice === "Save") {
            await document.save();
        }
    })();
}

async function resolveExecutable(): Promise<string | undefined> {
    const config = vscode.workspace.getConfiguration("mdreader");
    return resolveMdReaderExecutable({
        getConfiguredPath: () => config.get<string>("executablePath", ""),
        fileExists: fileExistsSync,
        queryAppPathsDefault,
        env: process.env,
    });
}

function launchMdReader(exePath: string, fsPaths: string[]): void {
    try {
        const child = spawn(exePath, fsPaths, {
            detached: true,
            stdio: "ignore",
            windowsHide: false,
            shell: false,
        });
        child.on("error", (error) => {
            void vscode.window.showErrorMessage(`Failed to launch MdReader: ${error.message}`);
        });
        child.unref();
    } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        void vscode.window.showErrorMessage(`Failed to launch MdReader: ${message}`);
    }
}
