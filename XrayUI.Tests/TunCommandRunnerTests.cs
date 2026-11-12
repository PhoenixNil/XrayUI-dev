using System.Diagnostics;
using System.Text;
using XrayUI.Services;

namespace XrayUI.Tests
{
    public class TunCommandRunnerTests
    {
        [Theory]
        [InlineData(7)]
        [InlineData(-1)]
        public void Run_EarlierFailureSurvivesLaterSuccess(int failedExitCode)
        {
            var log = new List<string>();
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var succeeded = TunCommandRunner.Run(
                new[] { $"{cmd} /d /c exit /b {failedExitCode}", "echo continued-after-failure" }, false, log.Add);

            Assert.False(succeeded);
            Assert.Contains(log, line => line.Contains("continued-after-failure"));
            Assert.Contains(log, line => line.Contains($"exit={failedExitCode}"));
        }

        [Fact]
        public void Run_CapturesBothStreamsBeyondPipeCapacity()
        {
            var log = new List<string>();
            var succeeded = TunCommandRunner.Run(new[]
            {
                "for /L %i in (1,1,2500) do @(echo stdout-%i & echo stderr-%i 1>&2)",
            }, false, log.Add);

            Assert.True(succeeded);
            Assert.Contains(log, line => line.Contains("stdout-2500"));
            Assert.Contains(log, line => line.Contains("stderr-2500"));
            Assert.True(log.Sum(line => line.Length) > 32 * 1024);
        }

        [Fact]
        public async Task Run_TimeoutTerminatesTheCommandAndItsChild()
        {
            var pidPath = Path.Combine(Path.GetTempPath(), $"XrayUI-TunCommand-{Guid.NewGuid():N}.pid");
            var script = $"Set-Content -LiteralPath '{pidPath.Replace("'", "''")}' -Value $PID; Start-Sleep -Seconds 30";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var run = Task.Run(() => TunCommandRunner.Run(
                new[] { $"{powershell} -NoProfile -NonInteractive -EncodedCommand {encoded}" }, false, _ => { }),
                TestContext.Current.CancellationToken);
            var childId = 0;
            try
            {
                var readyDeadline = DateTime.UtcNow.AddSeconds(4);
                while ((!File.Exists(pidPath) || new FileInfo(pidPath).Length == 0) && DateTime.UtcNow < readyDeadline)
                    await Task.Delay(20, TestContext.Current.CancellationToken);
                childId = int.Parse((await File.ReadAllTextAsync(pidPath, TestContext.Current.CancellationToken)).Trim());
                Assert.True(IsRunning(childId));

                Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                Assert.False(IsRunning(childId));
            }
            finally
            {
                if (childId != 0 && IsRunning(childId))
                {
                    using var child = Process.GetProcessById(childId);
                    child.Kill(entireProcessTree: true);
                }
                await run.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                File.Delete(pidPath);
            }
        }

        [Fact]
        public async Task Run_ShutdownCleanupDoesNotNeedTheCallingDispatcher()
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new UnpumpedContext());
                try
                {
                    completion.SetResult(TunCommandRunner.Run(new[] { "echo shutdown-cleanup" }, false, _ => { }));
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }) { IsBackground = true };
            thread.Start();

            Assert.True(await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }

        private sealed class UnpumpedContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback callback, object? state) { }
        }

        private static bool IsRunning(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
