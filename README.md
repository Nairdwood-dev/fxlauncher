# Nairdwood Launcher

![Screenshot of Nairdwood Launcher.](https://www.imghost.onl/uploads/nairdwood/files/6a9461974074b2.06412081.png)

Nairdwood Launcher is a standalone Windows desktop launcher and live console for FXServer. It is a
general-purpose companion application, not a FiveM resource, and keeps team-specific developer
onboarding and private repository setup out of the public build.

## Features

- Select and remember an `FXServer.exe`, `.bat`, or `.cmd` launch target.
- Configure launch arguments and the process working directory.
- Run a guided first-time setup for MariaDB, the server launcher, `server.cfg`, and optional RCON.
- Read, insert, or update `rcon_password` and preserve the original config as
  `server.cfg.nairdwood-backup` before the first edit.
- Start, gracefully stop (`quit`), restart, or explicitly force-kill the complete owned process tree.
- View colourised ANSI and FiveM `^0`-`^9` console output.
- Send authenticated FXServer commands over localhost UDP RCON, with command history.
- Search, copy, clear, and export console output.
- Save complete timestamped logs under `%LocalAppData%\Nairdwood Launcher\logs`.
- Display PID, live status, runtime, crash exit codes, and optionally restart unexpected exits.
- Detect and start or stop an installed MariaDB Windows service, requesting elevation only when needed.
- Open the official MariaDB download page if no MariaDB service is installed.
- Use the white Nairdwood wordmark and blue application icon over the original grey shell, with dark steel
  blue (`#245A8D`) and secondary blue (`#3E7CB1`) accents.
- Persist settings under `%LocalAppData%\Nairdwood Launcher\settings.json`.

## Running from source

Requires the .NET 7 SDK on Windows:

```powershell
dotnet run --project '.\Nairdwood.Launcher.csproj'
```

## Building the distributable executable

Run:

```powershell
.\build.ps1
```

The self-contained 64-bit build is written to `publish\win-x64`. It does not require .NET to be
installed on the machine that runs it.

## FXServer and txAdmin

- For txAdmin monitor mode, select `FXServer.exe` and normally leave Arguments empty.
- For a traditional configuration launch, supply the same arguments used by the server startup
  script, such as `+exec server.cfg`.
- A batch launcher is supported when it performs environment setup before starting FXServer.

txAdmin's monitor process does not accept interactive FXServer commands from its Windows console.
Nairdwood Launcher instead uses FXServer's UDP RCON transport. Add a private value to the server
configuration and enter the same value in the launcher's RCON settings:

```cfg
set rcon_password "replace-with-a-long-private-password"
```

The default destination is `127.0.0.1:30120`. Keep it on localhost when the launcher and server run
on the same machine. The password is saved in the current Windows user's local launcher settings;
do not share that settings file or commit passwords to a repository.

txAdmin emits terminal-title control sequences that are not log messages. Nairdwood Launcher strips
those sequences and other non-display cursor controls before rendering or persisting output.

Nairdwood Launcher owns the process it starts. Closing it while FXServer is running opens a
confirmation and exits only after stopping the complete owned process tree, preventing orphaned
FXServer or txAdmin processes.

## MariaDB control

MariaDB is detected from registered Windows services rather than a hard-coded service name. The
launcher prioritises a service named `MariaDB` and also recognises versioned or custom services whose
name, display name, or executable path contains `MariaDB`.

Starting or stopping a Windows service requires administrator rights. Nairdwood Launcher requests
UAC elevation only for that action; running and viewing FXServer does not require the launcher to
remain elevated.
