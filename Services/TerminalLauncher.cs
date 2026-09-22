using System;
using System.Diagnostics;
using System.IO;
using ClaudeCodeExplorer.Models;

namespace ClaudeCodeExplorer.Services;

/// <summary>
/// Opens Windows Terminal in a conversation's working directory. Falls back to a plain
/// PowerShell window if Windows Terminal (wt.exe) is not installed.
/// </summary>
public static class TerminalLauncher
{
    private static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Open a terminal in the conversation's folder and resume that exact session.</summary>
    public static void Resume(ConversationInfo conv)
    {
        var cwd = conv.WorkingDirectory;

        // `claude --resume <id>` only finds a session when launched from its original
        // working directory, because sessions are stored under the path's encoding in
        // ~/.claude/projects. If that folder was deleted (e.g. a temp/Downloads folder
        // removed after a reboot), recreate the (empty) folder so the path matches again.
        if (!string.IsNullOrWhiteSpace(cwd) && !Directory.Exists(cwd))
        {
            try
            {
                Directory.CreateDirectory(cwd);
            }
            catch (Exception ex)
            {
                // Truly unrecoverable (e.g. the drive no longer exists): open a terminal in
                // the home folder so the user isn't stuck, then explain what happened.
                LaunchTerminal(UserProfile, null);
                throw new InvalidOperationException(
                    "The original folder for this conversation no longer exists and could not be recreated:\n\n"
                    + cwd + "\n\n(" + ex.Message + ")\n\n"
                    + "A terminal was opened in your home folder instead. Recreate that path to resume this session.");
            }
        }

        var startDir = string.IsNullOrWhiteSpace(cwd) ? UserProfile : cwd;
        LaunchTerminal(startDir, $"claude --resume {conv.SessionId}");
    }

    /// <summary>Open a terminal in the conversation's folder without running anything.</summary>
    public static void OpenTerminal(ConversationInfo conv)
        => LaunchTerminal(ResolveExistingDir(conv.WorkingDirectory), command: null);

    /// <summary>Open the conversation's working directory in File Explorer.</summary>
    public static void OpenInExplorer(ConversationInfo conv)
    {
        var cwd = conv.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            throw new DirectoryNotFoundException(
                "This folder no longer exists:\n\n" + (string.IsNullOrWhiteSpace(cwd) ? "(unknown)" : cwd));
        Process.Start(new ProcessStartInfo { FileName = cwd, UseShellExecute = true });
    }

    private static string ResolveExistingDir(string? cwd)
        => !string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd) ? cwd! : UserProfile;

    private static void LaunchTerminal(string cwd, string? command)
    {
        // Never hand Windows Terminal a non-existent starting directory – it errors with
        // 0x8007010B ("The directory name is invalid"). Substitute the home folder instead.
        var startDir = Directory.Exists(cwd) ? cwd : UserProfile;
        var shellExe = ResolveShellExe();

        // Preferred path: Windows Terminal. UseShellExecute=true lets the app-execution
        // alias (wt.exe) resolve, and throws cleanly if it is not installed.
        try
        {
            var wt = new ProcessStartInfo { FileName = "wt.exe", UseShellExecute = true };
            wt.ArgumentList.Add("-d");
            wt.ArgumentList.Add(startDir);
            if (command is not null)
            {
                wt.ArgumentList.Add(shellExe);
                wt.ArgumentList.Add("-NoExit");
                wt.ArgumentList.Add("-Command");
                wt.ArgumentList.Add(command);
            }
            Process.Start(wt);
            return;
        }
        catch
        {
            // Windows Terminal unavailable – fall through to a plain PowerShell window.
        }

        var ps = new ProcessStartInfo
        {
            FileName = shellExe,
            UseShellExecute = true,
            WorkingDirectory = startDir,
        };
        ps.ArgumentList.Add("-NoExit");
        if (command is not null)
        {
            ps.ArgumentList.Add("-Command");
            ps.ArgumentList.Add(command);
        }
        Process.Start(ps);
    }

    /// <summary>
    /// Prefers PowerShell 7+ (<c>pwsh.exe</c>) when it's on PATH, falling back to the legacy
    /// Windows PowerShell (<c>powershell.exe</c>, always present) otherwise. Resolved once per
    /// launch rather than cached, since it's cheap and the user could install/remove pwsh
    /// between runs.
    /// </summary>
    private static string ResolveShellExe()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "pwsh.exe")))
                    return "pwsh.exe";
            }
            catch
            {
                // Malformed PATH entry – skip it.
            }
        }
        return "powershell.exe";
    }
}
