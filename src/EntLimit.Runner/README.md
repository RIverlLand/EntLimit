# Local Python practice runner

`PythonRunner.RunAsync` runs only on demand, using a selected or discovered Python 3.8+ executable. Discovery checks Python registry installations, the app's optional `runtime/python/python.exe`, Local Programs/Python, then PATH; WindowsApps installer aliases are skipped. No runtime is downloaded or installed.

The runner uses `ProcessStartInfo.ArgumentList` without a shell, Python `-I -X utf8`, an owned temporary working directory, a five-second execution deadline, cancellation, 64 KiB source limit, bounded 16 KiB stdout and stderr, and a separate bounded JSON result file. It requires exit code zero and one ordered result per test. Examples run a subset; the monitor decides when a full successful submission earns a permit.

On Windows a fresh unnamed Job Object enforces 256 MiB per process, one active process and kill-on-close. Timeout/cancellation kills only the owned Python process tree. No unrelated application/game process is controlled. The process starts before Job attachment, so these are best-effort resource limits, not a suspended-start security boundary. Failure to attach limits aborts the run.

This is a personal exercise runner for user-authored or trusted code, **not a security sandbox**. Python retains the user's file/network permissions and can inspect harness files; hostile code can deliberately forge results. Do not silently execute downloaded submissions, import third-party packages or claim hostile-code isolation. Result and stdout separation prevent accidental/debug-output confusion, not adversarial tampering.

To test: set `ENT_LIMIT_TEST_PYTHON` to an actual Python executable, then run the console project `tests/EntLimit.Runner.Tests`. The user's registered Python 3.14 was used for validation. Tests never grant permits or modify managed application settings.
