using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace NeonCompanion.Runtime.Core
{
    public sealed class ProcessExecutionService
    {
        public async Task<ProcessResult> ExecuteAsync(string command, int timeoutMs = 30000)
        {
            var result = new ProcessResult();

            if (string.IsNullOrWhiteSpace(command))
            {
                result.exitCode = 0;
                result.stdout = string.Empty;
                result.stderr = string.Empty;
                return result;
            }

            var process = new Process();
            try
            {
                bool isWindows = Application.platform == RuntimePlatform.WindowsEditor ||
                                 Application.platform == RuntimePlatform.WindowsPlayer;

                if (isWindows)
                {
                    process.StartInfo.FileName = "powershell.exe";
                    // Use -NoProfile -NonInteractive for cleaner non-interactive run
                    string escaped = command.Replace("\"", "`\"");
                    process.StartInfo.Arguments = "-NoProfile -NonInteractive -Command \"" + escaped + "\"";
                }
                else
                {
                    process.StartInfo.FileName = "/bin/bash";
                    string escaped = command.Replace("\"", "\\\"");
                    process.StartInfo.Arguments = "-c \"" + escaped + "\"";
                }

                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                process.StartInfo.StandardErrorEncoding = Encoding.UTF8;

                if (!process.Start())
                {
                    result.exitCode = -1;
                    result.stderr = "Failed to start shell process.";
                    return result;
                }

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                var timeoutTask = Task.Delay(timeoutMs);
                var waitExitTask = WaitForExitAsync(process);

                var completedTask = await Task.WhenAny(
                    Task.WhenAll(stdoutTask, stderrTask, waitExitTask),
                    timeoutTask);

                if (completedTask == timeoutTask)
                {
                    result.timedOut = true;
                    result.exitCode = -1;

                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                        }
                    }
                    catch (Exception)
                    {
                        // ignore kill errors
                    }

                    string so = string.Empty;
                    string se = string.Empty;
                    var drainTimeout = Task.Delay(2000);
                    try { await Task.WhenAny(stdoutTask, drainTimeout); so = stdoutTask.IsCompleted ? stdoutTask.Result : string.Empty; } catch { }
                    try { await Task.WhenAny(stderrTask, drainTimeout); se = stderrTask.IsCompleted ? stderrTask.Result : string.Empty; } catch { }

                    result.stdout = so ?? string.Empty;
                    result.stderr = (se ?? string.Empty) + "\n[Process timed out after " + timeoutMs + " ms]";
                }
                else
                {
                    await Task.WhenAll(stdoutTask, stderrTask);

                    result.stdout = stdoutTask.Result ?? string.Empty;
                    result.stderr = stderrTask.Result ?? string.Empty;
                    result.exitCode = process.HasExited ? process.ExitCode : -1;
                }
            }
            catch (Exception ex)
            {
                result.exitCode = -1;
                result.stderr = "Execution error: " + ex.Message;
            }
            finally
            {
                try
                {
                    if (process != null)
                        process.Dispose();
                }
                catch { }
            }

            return result;
        }

        private async Task WaitForExitAsync(Process process)
        {
            while (process != null && !process.HasExited)
            {
                await Task.Delay(30);
            }
        }
    }

    [Serializable]
    public class ProcessResult
    {
        public string stdout;
        public string stderr;
        public int exitCode;
        public bool timedOut;
    }
}
