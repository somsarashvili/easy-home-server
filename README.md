# EasyHomeServer

A web management tool for a single Debian home server. Modular: each feature installs as its own
`.deb` and is discovered by the host at startup.

This phase ships the foundation plus one reference module (System Info). Docker, disks, DNS,
file sharing and mDNS are still to come.

## Run it

```bash
scripts/dev-publish-modules.sh                  # publish modules into ./modules/
dotnet run --project src/EasyHomeServer.Host    # http://localhost:5000
```

First visit asks you to set an admin password — there is no default.

**On macOS or Windows the System Info module cannot show metrics**: it reads `/proc`, and the
page says so rather than inventing numbers. To see it working, run it on Linux:

```bash
scripts/run-docker.sh                           # http://localhost:5199
```

That builds a self-contained Linux publish laid out exactly as the `.deb` installs it.

## Build the packages

```bash
packaging/build.sh --arch arm64                 # or --arch amd64 (default)
```

Needs `dotnet` and `dpkg-deb` (`brew install dpkg` on macOS). Install host first:

```bash
sudo dpkg -i artifacts/deb/easyhomeserver_0.1.0_arm64.deb
sudo dpkg -i artifacts/deb/easyhomeserver-module-systeminfo_0.1.0_all.deb
journalctl -u easyhomeserver -f
```

The service listens on port 5000.

## Layout

```
src/EasyHomeServer.Sdk/                            the versioned plugin contract
src/EasyHomeServer.Host/                           web app + module loader
src/modules/EasyHomeServer.Modules.SystemInfo/     reference module
packaging/                                         deb build, systemd unit
docs/ARCHITECTURE.md                               decisions + how to write a module
```

## Writing a module

See **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** — it covers the plugin model, the SDK
surface and a worked example, and is meant to be enough on its own without reading host source.

Two things that will cost you an afternoon if you skip them:

- Reference the SDK and MudBlazor **compile-time only**
  (`Private="false"` / `ExcludeAssets="runtime"`). A second runtime copy loads as a *different
  type* and module discovery silently finds nothing. The build scripts fail if this is wrong.
- Module static assets must be **embedded with a file manifest**, not published as static web
  assets — the host has no build-time knowledge of a module discovered at runtime.

## License

Apache License 2.0 — see [LICENSE](LICENSE).
