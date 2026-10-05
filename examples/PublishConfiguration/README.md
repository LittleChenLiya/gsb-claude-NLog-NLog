# Explicit configuration publish with acknowledgment receipt

`LogFactory.PublishConfiguration(LoggingConfiguration)` publishes a new logging
configuration and returns a `ConfigPublishReceipt` that acknowledges the outcome:

- The new targets are initialized **before** the new configuration is activated.
  When initialization fails, only the targets from the failed attempt are closed,
  and the active configuration stays unchanged.
- Logevents that already entered the logging pipeline keep writing to the
  target-collection from when they entered, and are never mixed with the new
  configuration.
- The previous configuration does not close its targets while they still have
  in-flight writes. The close happens exactly once, after the in-flight writes
  have completed, and can be awaited through
  `ConfigPublishReceipt.WaitForPreviousConfigurationClosed`.
- `LogFactory.Flush()` binds to the target-collection that is active at
  call-time, and does not wait for future logevents.
- Other `LogFactory`-instances are not affected. The default reconfig API
  (`LogFactory.Configuration` setter and `LogFactory.ReconfigExistingLoggers`)
  stays supported.

See [Program.cs](Program.cs) for a two-target example (console + memory,
swapped for file + memory), and
`tests/NLog.UnitTests/LogFactoryPublishConfigurationTests.cs` for the
acceptance tests.

## Local dotnet commands (D: drive, no Docker)

```bat
set PATH=D:\Codex\dotnet-10;%PATH%
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set NUGET_PACKAGES=D:\Codex\c-drive-relocation\nuget\packages
set DOTNET_CLI_HOME=D:\Codex\dotnet-cli-home
set TEMP=D:\Codex\tmp
set TMP=D:\Codex\tmp

rem Build the NLog library only
dotnet build src/NLog/NLog.csproj -c Release -f netstandard2.0

rem Build and run only the publish-configuration acceptance tests
rem (VisualStudioVersion=18.0 selects the net10.0 test target;
rem  with an SDK 8 + .NET 8 runtime use: -f net8.0 without the extra property)
dotnet test tests/NLog.UnitTests/NLog.UnitTests.csproj -c Release ^
  -p:VisualStudioVersion=18.0 -f net10.0 ^
  --filter "FullyQualifiedName~LogFactoryPublishConfigurationTests"
```
