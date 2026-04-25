using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace XrayUI.Services
{
    public class XrayService
    {
        private static readonly string ExePath = Path.Combine(
            AppContext.BaseDirectory, "Assets", "engine", "xray.exe");

        public static readonly string RulesDir = Path.Combine(
            AppContext.BaseDirectory, "Assets", "rules");

        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XrayUI", "xray_config.json");

        private const int LogBufferMax = 500;

        private Process? _process;
        private readonly StringBuilder _startupLog = new();

        // Fixed-size ring buffer: O(1) append + oldest-drop, no array shifting.
        private readonly string[] _logBuffer = new string[LogBufferMax];
        private int _logHead;    // index of the next write slot
        private int _logCount;   // number of valid entries (<= LogBufferMax)
        private readonly object _bufferLock = new();

        public bool IsRunning => _process is { HasExited: false };

        public string LastError { get; private set; } = string.Empty;

        public event EventHandler<string>? LogReceived;

        public event EventHandler<bool>? RunningChanged;

        public IReadOnlyList<string> GetLogBuffer()
        {
            lock (_bufferLock)
            {
                if (_logCount == 0)
                {
                    return Array.Empty<string>();
                }

                var snapshot = new string[_logCount];
                if (_logCount < LogBufferMax)
                {
                    // Not yet wrapped — data is contiguous in [0, _logCount)
                    Array.Copy(_logBuffer, 0, snapshot, 0, _logCount);
                }
                else
                {
                    // Wrapped — oldest at _logHead, newest at _logHead-1
                    int tailCount = LogBufferMax - _logHead;
                    Array.Copy(_logBuffer, _logHead, snapshot, 0, tailCount);
                    Array.Copy(_logBuffer, 0, snapshot, tailCount, _logHead);
                }
                return snapshot;
            }
        }

        public void ClearLogBuffer()
        {
            lock (_bufferLock)
            {
                Array.Clear(_logBuffer, 0, _logBuffer.Length);
                _logHead = 0;
                _logCount = 0;
            }
        }

        private void AppendLog(string line)
        {
            lock (_bufferLock)
            {
                _logBuffer[_logHead] = line;
                _logHead = (_logHead + 1) % LogBufferMax;
                if (_logCount < LogBufferMax)
                {
                    _logCount++;
                }
            }

            LogReceived?.Invoke(this, line);
        }

        public async Task<bool> StartAsync(string configJson)
        {
            if (IsRunning)
            {
                await StopAsync();
            }

            LastError = string.Empty;
            _startupLog.Clear();

            if (!File.Exists(ExePath))
            {
                LastError = $"找不到 xray.exe\n路径：{ExePath}";
                AppendLog("[错误] " + LastError);
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                await File.WriteAllTextAsync(ConfigPath, configJson);

                var psi = new ProcessStartInfo
                {
                    FileName = ExePath,
                    Arguments = $"run -config \"{ConfigPath}\"",
                    WorkingDirectory = Path.GetDirectoryName(ExePath)!,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.EnvironmentVariables["XRAY_LOCATION_ASSET"] = RulesDir;

                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

                _process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is null)
                    {
                        return;
                    }

                    _startupLog.AppendLine(e.Data);
                    AppendLog(e.Data);
                };

                _process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is null)
                    {
                        return;
                    }

                    _startupLog.AppendLine(e.Data);
                    AppendLog(e.Data);
                };

                _process.Exited += (_, _) =>
                {
                    AppendLog("[xray 进程已退出]");
                    RunningChanged?.Invoke(this, false);
                };

                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                AppendLog($"[启动] {ExePath}");
                AppendLog($"[配置] {ConfigPath}");

                await Task.Delay(800);

                if (_process.HasExited)
                {
                    LastError = _startupLog.Length > 0
                        ? _startupLog.ToString().Trim()
                        : $"xray 立即退出（退出码 {_process.ExitCode}）";
                    AppendLog("[错误] 启动失败：" + LastError);
                    return false;
                }

                RunningChanged?.Invoke(this, true);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                AppendLog("[异常] " + ex.Message);
                return false;
            }
        }

        public async Task StopAsync()
        {
            if (_process is null)
            {
                return;
            }

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }

                await _process.WaitForExitAsync();
            }
            catch
            {
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }

            AppendLog("[已停止]");
            RunningChanged?.Invoke(this, false);
        }
        public void StopForShutdown()
        {
            var process = _process;
            if (process is null)
            {
                return;
            }

            _process = null;

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(500);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }

            AppendLog("[shutdown] xray stopped");
            RunningChanged?.Invoke(this, false);
        }
    }
}
