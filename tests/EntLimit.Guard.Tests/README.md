# Guard integration checks

Run the built console harness with exactly two arguments:

```
EntLimit.Guard.Tests <published-app-dir> <empty-dedicated-test-data-dir>
```

Requires Windows and published Monitor + NativeHost executables. The harness generates a unique `ENT_LIMIT_INSTANCE=guard-...`, sets a dedicated `ENT_LIMIT_DATA_DIR`, and refuses a non-empty data directory. It never registers a real login startup item. Startup command/value tests link the implementation and always inject `FakeStartupValue`; isolated startup=true is separately checked through actual IPC.

The integration checks cover earned-permit preservation across configuration changes/recovery, guard refusal of ordinary shutdown, recovery after terminating the exact monitor process created by the test, debugging pause and full stop, and NativeHost live/offline paused domain clearing. Process ownership comes from the isolated named pipe's exact server PID plus its verified watchdog parent; cleanup never kills a process by name. Fake domains use `.invalid` and no managed applications are added.

The dedicated data directory is retained for inspection. Cleanup sends debug_stop; an emergency fallback can change only that generated test configuration to paused before closing exact owned process handles.
