# Isolated startup and emergency recovery checks

Run on Windows after publishing Monitor and NativeHost:

```
EntLimit.Recovery.Tests <app-directory> <empty-dedicated-test-base> <emergency-stop.ps1>
```

The harness generates an isolated pipe/mutex instance and test data directory containing spaces. It executes `StartupRegistration.FormatCommand` via Windows `CreateProcessW` from a foreign working directory, including a trailing directory slash and a deliberately incorrect inherited data variable. No real Run key is read or written. It checks singleton behavior and absence of the isolated Desktop mutex.

To verify that the emergency script is independent of responsive IPC, the test suspends **only its own freshly created monitor process** and passes that exact PID with `-Instance` and `-ProcessIds`. Another test-owned monitor in the same executable directory must remain alive. The independent marker stops the test watchdog; configuration and earned-session bytes must remain unchanged. Further checks cover corrupt-config browser fallback, marker-preserving paused restart, and explicit resume preserving permits while leaving startup and watchdog disabled.

Cleanup resumes a still-suspended owned process if necessary, commits an emergency marker in the generated test data directory, and closes only precise held test process handles. No entertainment application, browser, production monitor, startup entry, or production data is a target. Dedicated data is retained for inspection.
