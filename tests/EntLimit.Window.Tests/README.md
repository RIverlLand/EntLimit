# Actual Windows window regression

Run only in an interactive Windows desktop. Tests create harmless WinForms windows,
ask those windows to restore/activate themselves, and observe their real HWNDs and
the isolated monitor's IPC. They never enumerate titles or control user windows.

Publish Monitor plus WindowFixture into a dedicated test directory WITHOUT Desktop.
Copy WindowFixture.exe to OtherWindowFixture.exe in that directory (the apphost uses
the same WindowFixture.dll). Pass that directory and an empty isolated data directory:

`EntLimit.Window.Tests.exe <test-publication> <empty-data-directory>`

ENT_LIMIT_INSTANCE is generated before pipe initialization and inherited explicitly
by child processes; ENT_LIMIT_DATA_DIR is isolated. Production monitor is left alone.
Gate UI cannot launch because Desktop is absent. Cleanup first requests monitor
shutdown and closes fixtures via their own pipes; fallback termination checks the
started PID and executable path inside the test publication and is never recursive.

Covers immediate locked save_config gating of an already foreground window; light
exercise, restore and 90-minute activation; finish gating with history retention;
focus churn followed by an unmanaged foreground window; future expiry with the real
overlay enabled transitioning to Draining without minimizing or stealing focus;
fixture exit returning to Locked.
The focus churn case exercises real events but cannot guarantee the OS queued a
particular stale event. It is a regression stress check, not synthetic event proof.

Typical runtime is under 30 seconds. Bounded waits fail if foreground activation is
denied or the user switches apps during assertions; such failure is not evidence
of a product gate defect. It does not test exclusive fullscreen games or anti-cheat.
