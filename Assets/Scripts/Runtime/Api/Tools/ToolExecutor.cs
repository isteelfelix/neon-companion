using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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

                var psi = new ProcessStartInfo();
                psi.FileName = "python3";
                psi.Arguments = "\"" + scriptPath + "\"";
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                        return LocalizationExtensions.Get("tool.error.code_start", "Error: failed to start Python process");

                    bool exited = process.WaitForExit(CodeTimeoutMs);
                    if (!exited)
                    {
                        try { process.Kill(); } catch { }
                        return LocalizationExtensions.Get("tool.error.code_timeout", "Error: code execution timed out after 10 seconds");
                    }

                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();

                    var output = new StringBuilder();
                    if (!string.IsNullOrEmpty(stdout))
                        output.Append(stdout);
                    if (!string.IsNullOrEmpty(stderr))
                    {
                        if (output.Length > 0)
                            output.Append('\n');
                        output.Append("STDERR:\n").Append(stderr);
                    }

                    string result = output.ToString();
                    if (string.IsNullOrWhiteSpace(result))
                        result = "(no output)";

                    if (result.Length > MaxResultLength)
                        result = result.Substring(0, MaxResultLength) + "\n... [output truncated]";

                    return result;
                }
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
    }
}
