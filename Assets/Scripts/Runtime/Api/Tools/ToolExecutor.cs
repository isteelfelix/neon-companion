using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NeonCompanion.Runtime.Localization;

namespace NeonCompanion.Runtime.Api.Tools
{
    internal static class ToolExecutor
    {
        private const int MaxResultLength = 10000;
        private const int MaxSearchMatches = 100;
        private const int CodeTimeoutMs = 10000;

        public static string Execute(string toolName, Dictionary<string, string> parameters)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return LocalizationExtensions.Get("tool.error.unknown", "Error: Unknown tool");

            switch (toolName)
            {
                case "read_file":
                    return ExecuteReadFile(parameters);
                case "write_file":
                    return ExecuteWriteFile(parameters);
                case "list_files":
                    return ExecuteListFiles(parameters);
                case "search_files":
                    return ExecuteSearchFiles(parameters);
                case "execute_code":
                    return ExecuteCode(parameters);
                case "run_command":
                    return ExecuteRunCommand(parameters);
                default:
                    return LocalizationExtensions.Get("tool.error.unknown", "Error: Unknown tool '") + toolName + "'";
            }
        }

        private static string ExecuteReadFile(Dictionary<string, string> parameters)
        {
            if (parameters == null || !parameters.ContainsKey("path"))
                return LocalizationExtensions.Get("tool.error.read_path", "Error: 'path' parameter is required for read_file");

            string path = parameters["path"];
            if (string.IsNullOrWhiteSpace(path))
                return LocalizationExtensions.Get("tool.error.path_empty", "Error: path is empty");

            try
            {
                if (!File.Exists(path))
                    return LocalizationExtensions.Get("tool.error.file_not_found", "Error: file not found: ") + path;

                string content = File.ReadAllText(path);
                if (content.Length > MaxResultLength)
                    content = content.Substring(0, MaxResultLength) + "\n... [output truncated]";

                return content;
            }
            catch (Exception ex)
            {
                return LocalizationExtensions.Get("tool.error.read", "Error reading file: ") + ex.Message;
            }
        }

        private static string ExecuteWriteFile(Dictionary<string, string> parameters)
        {
            if (parameters == null || !parameters.ContainsKey("path"))
                return LocalizationExtensions.Get("tool.error.write_path", "Error: 'path' parameter is required for write_file");

            string path = parameters["path"];
            string content = parameters.ContainsKey("content") ? parameters["content"] : string.Empty;

            if (string.IsNullOrWhiteSpace(path))
                return LocalizationExtensions.Get("tool.error.path_empty", "Error: path is empty");

            try
            {
                File.WriteAllText(path, content);
                string success = LocalizationExtensions.Get("tool.success.write", "Success: wrote ");
                return success + content.Length + " chars to " + path;
            }
            catch (Exception ex)
            {
                return LocalizationExtensions.Get("tool.error.write", "Error writing file: ") + ex.Message;
            }
        }

        private static string ExecuteListFiles(Dictionary<string, string> parameters)
        {
            if (parameters == null || !parameters.ContainsKey("path"))
                return LocalizationExtensions.Get("tool.error.list_path", "Error: 'path' parameter is required for list_files");

            string path = parameters["path"];
            if (string.IsNullOrWhiteSpace(path))
                return LocalizationExtensions.Get("tool.error.path_empty", "Error: path is empty");

            try
            {
                if (!Directory.Exists(path))
                    return LocalizationExtensions.Get("tool.error.dir_not_found", "Error: directory not found: ") + path;

                string[] entries = Directory.GetFileSystemEntries(path);
                var sb = new StringBuilder();
                for (int i = 0; i < entries.Length; i++)
                {
                    if (i > 0)
                        sb.Append('\n');

                    string entry = entries[i];
                    bool isDir = Directory.Exists(entry);
                    sb.Append(isDir ? "[DIR] " : "[FILE] ");
                    sb.Append(Path.GetFileName(entry));
                }

                string result = sb.ToString();
                if (result.Length == 0)
                    result = "(empty directory)";
                else if (result.Length > MaxResultLength)
                    result = result.Substring(0, MaxResultLength) + "\n... [truncated]";

                return result;
            }
            catch (Exception ex)
            {
                return LocalizationExtensions.Get("tool.error.list", "Error listing directory: ") + ex.Message;
            }
        }

        private static string ExecuteSearchFiles(Dictionary<string, string> parameters)
        {
            if (parameters == null || !parameters.ContainsKey("path"))
                return LocalizationExtensions.Get("tool.error.search_path", "Error: 'path' parameter is required for search_files");
            if (!parameters.ContainsKey("pattern"))
                return LocalizationExtensions.Get("tool.error.search_pattern", "Error: 'pattern' parameter is required for search_files");

            string path = parameters["path"];
            string pattern = parameters["pattern"];

            if (string.IsNullOrWhiteSpace(path))
                return LocalizationExtensions.Get("tool.error.path_empty", "Error: path is empty");
            if (string.IsNullOrEmpty(pattern))
                return LocalizationExtensions.Get("tool.error.pattern_empty", "Error: pattern is empty");

            try
            {
                if (!Directory.Exists(path))
                    return LocalizationExtensions.Get("tool.error.dir_not_found", "Error: directory not found: ") + path;

                string[] files = Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly);
                var sb = new StringBuilder();
                int matchCount = 0;

                for (int f = 0; f < files.Length && matchCount < MaxSearchMatches; f++)
                {
                    string file = files[f];
                    try
                    {
                        string[] lines = File.ReadAllLines(file);
                        for (int l = 0; l < lines.Length; l++)
                        {
                            string line = lines[l];
                            bool matches = false;

                            try
                            {
                                var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                                matches = regex.IsMatch(line);
                            }
                            catch
                            {
                                matches = line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                            }

                            if (matches)
                            {
                                matchCount++;
                                if (sb.Length > 0)
                                    sb.Append('\n');

                                string fileName = Path.GetFileName(file);
                                string displayLine = line.Length > 200 ? line.Substring(0, 200) + "..." : line;
                                sb.Append(fileName).Append(':').Append(l + 1).Append(": ").Append(displayLine);

                                if (matchCount >= MaxSearchMatches)
                                    break;
                            }
                        }
                    }
                    catch
                    {
                        // skip files that cannot be read
                    }
                }

                string result = sb.ToString();
                if (string.IsNullOrEmpty(result))
                    result = "No matches found.";
                else if (result.Length > MaxResultLength)
                    result = result.Substring(0, MaxResultLength) + "\n... [truncated]";

                return result;
            }
            catch (Exception ex)
            {
                return LocalizationExtensions.Get("tool.error.search", "Error searching files: ") + ex.Message;
            }
        }

        private static string ExecuteCode(Dictionary<string, string> parameters)
        {
            if (parameters == null || !parameters.ContainsKey("code"))
                return LocalizationExtensions.Get("tool.error.code_required", "Error: 'code' parameter is required for execute_code");

            string code = parameters["code"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(code))
                return LocalizationExtensions.Get("tool.error.code_empty", "Error: code is empty");

            string scriptPath = null;
            try
            {
                string tempDir = Path.GetTempPath();
                scriptPath = Path.Combine(tempDir, "neon_exec_" + Guid.NewGuid().ToString("N") + ".py");
                File.WriteAllText(scriptPath, code);

                return ExecutePythonScript(scriptPath);
            }
            catch (Exception ex)
            {
                return LocalizationExtensions.Get("tool.error.code_exec", "Error executing Python code: ") + ex.Message;
            }
            finally
            {
                if (!string.IsNullOrEmpty(scriptPath))
                {
                    try { File.Delete(scriptPath); } catch { }
                }
            }
        }

        private static string ExecuteRunCommand(Dictionary<string, string> parameters)
        {
            if (parameters == null || !parameters.ContainsKey("command"))
                return LocalizationExtensions.Get("tool.error.command_required", "Error: 'command' parameter is required for run_command");

            string command = parameters["command"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command))
                return LocalizationExtensions.Get("tool.error.command_empty", "Error: command is empty");

            int timeoutMs = 60000;
            string timeoutRaw;
            if (parameters.TryGetValue("timeout_ms", out timeoutRaw) && !string.IsNullOrEmpty(timeoutRaw))
            {
                int parsed;
                if (int.TryParse(timeoutRaw, out parsed) && parsed > 0)
                    timeoutMs = parsed;
            }

            bool isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            try
            {
                if (isWindows)
                {
                    // Force UTF-8 out of PowerShell so non-ASCII output isn't mojibake.
                    string prelude = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $OutputEncoding=[System.Text.Encoding]::UTF8; ";
                    // -EncodedCommand (base64 UTF-16LE) avoids all quoting/escaping issues:
                    // backslash paths, embedded quotes, and Unicode pass through verbatim.
                    string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(prelude + command));
                    return ExecuteProcess("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + encoded, true, timeoutMs, true);
                }
                else
                {
                    string escaped = command.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    return ExecuteProcess("/bin/bash", "-c \"" + escaped + "\"", true, timeoutMs, true);
                }
            }
            catch (Exception ex)
            {
                return LocalizationExtensions.Get("tool.error.command_exec", "Error running command: ") + ex.Message;
            }
        }

        private static string ExecutePythonScript(string scriptPath)
        {
            string[] fileNames = new string[] { "python3", "python", "py" };
            string lastError = null;

            for (int i = 0; i < fileNames.Length; i++)
            {
                string fileName = fileNames[i];
                string arguments = string.Equals(fileName, "py", StringComparison.OrdinalIgnoreCase)
                    ? "-3 \"" + scriptPath + "\""
                    : "\"" + scriptPath + "\"";

                try
                {
                    return ExecuteProcess(fileName, arguments);
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            string prefix = LocalizationExtensions.Get("tool.error.code_start", "Error: failed to start Python process");
            return string.IsNullOrEmpty(lastError) ? prefix : prefix + ": " + lastError;
        }

        private static string ExecuteProcess(string fileName, string arguments, bool utf8 = false, int timeoutMs = CodeTimeoutMs, bool includeExitCode = false)
        {
            int exitCode = 0;
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            object stdoutLock = new object();
            object stderrLock = new object();
            var stdoutClosed = new ManualResetEvent(false);
            var stderrClosed = new ManualResetEvent(false);

            var psi = new ProcessStartInfo();
            psi.FileName = fileName;
            psi.Arguments = arguments;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            if (utf8)
            {
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                psi.StandardErrorEncoding = new UTF8Encoding(false);
            }

            using (var process = new Process())
            {
                process.StartInfo = psi;
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data == null)
                    {
                        stdoutClosed.Set();
                        return;
                    }

                    lock (stdoutLock)
                    {
                        AppendLimitedLine(stdout, e.Data);
                    }
                };
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data == null)
                    {
                        stderrClosed.Set();
                        return;
                    }

                    lock (stderrLock)
                    {
                        AppendLimitedLine(stderr, e.Data);
                    }
                };

                if (!process.Start())
                    return LocalizationExtensions.Get("tool.error.code_start", "Error: failed to start Python process");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool exited = process.WaitForExit(timeoutMs);
                if (!exited)
                {
                    try { process.Kill(); } catch { }
                    try { process.WaitForExit(1000); } catch { }
                    return LocalizationExtensions.Get("tool.error.code_timeout", "Error: execution timed out");
                }

                stdoutClosed.WaitOne(1000);
                stderrClosed.WaitOne(1000);

                try { exitCode = process.ExitCode; } catch { exitCode = 0; }
            }

            var output = new StringBuilder();
            string stdoutText;
            string stderrText;
            lock (stdoutLock) { stdoutText = stdout.ToString(); }
            lock (stderrLock) { stderrText = stderr.ToString(); }

            if (!string.IsNullOrEmpty(stdoutText))
                output.Append(stdoutText);
            if (!string.IsNullOrEmpty(stderrText))
            {
                if (output.Length > 0)
                    output.Append('\n');
                output.Append("STDERR:\n").Append(stderrText);
            }

            if (includeExitCode)
            {
                if (output.Length > 0)
                    output.Append('\n');
                output.Append("[exit ").Append(exitCode).Append(']');
            }

            string result = output.ToString();
            if (string.IsNullOrWhiteSpace(result))
                result = "(no output)";

            if (result.Length > MaxResultLength)
                result = result.Substring(0, MaxResultLength) + "\n... [output truncated]";

            return result;
        }

        private static void AppendLimitedLine(StringBuilder sb, string line)
        {
            if (sb == null || line == null)
                return;

            if (sb.Length > MaxResultLength + 1024)
                return;

            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(line);
        }
    }
}
