# EasyHomeServer — Architecture

A web management tool for a single Debian home server: Docker, disks, system info, DNS,
file sharing and mDNS. Each feature is a module, and each module installs as its own `.deb`.

This document records the decisions that shape the codebase and explains how to write a new
module. If you are here to start the Docker module, you should not need to read the host's
source — [Writing a module](#writing-a-module) is the contract.

---

## 1. Shape of the system

**A modular monolith with a plugin model.** One process, one port, one systemd unit. Features
are plugin assemblies discovered on disk at startup and loaded into the host.

```
┌─ easyhomeserver.deb ────────────────────────────────────────────┐
│  EasyHomeServer.Host          ASP.NET Core + Blazor Server      │
│    ├── module loader          scans, version-checks, loads      │
│    ├── EventBus               in-process typed pub/sub          │
│    ├── SystemRunner           the privileged seam               │
│    ├── SQLite                 host settings + admin password    │
│    └── MudBlazor shell        nav built from module manifests   │
│  EasyHomeServer.Sdk           the versioned plugin contract     │
│  Provides: easyhomeserver-sdk-1                                 │
└─────────────────────────────────────────────────────────────────┘
              ▲ loaded at startup from /usr/lib/easyhomeserver/modules/
┌─ easyhomeserver-module-systeminfo.deb ──────────────────────────┐
│  EasyHomeServer.Modules.SystemInfo.dll                          │
│    IModule + Blazor pages + embedded wwwroot                    │
│  Depends: easyhomeserver-sdk-1                                  │
└─────────────────────────────────────────────────────────────────┘
```

### Why a plugin model rather than one project

Because the packaging requirement drives it. "Each module independently installable as its own
`.deb`" means a module must be *absent* at host build time and *appear* at runtime. That rules
out project references and any compile-time knowledge of modules. Everything awkward in this
codebase — the load contexts, the embedded static assets, the runtime nav — follows from that
one requirement.

### Why in-process rather than separate services

A home server is one modest box. Six services would mean six runtimes, six ports and an IPC
story, to isolate code that is all equally trusted and all root anyway. One process keeps the
memory footprint and the operational surface small. The cost is that a module can take the host
down; the loader spends most of its effort making that hard.

### Repository layout

```
EasyHomeServer.slnx
  src/EasyHomeServer.Sdk/                    contracts only — the plugin API
  src/EasyHomeServer.Host/                   the web app and module loader
  src/modules/EasyHomeServer.Modules.SystemInfo/   reference module
  packaging/                                 deb build, systemd unit, maintainer scripts
  scripts/                                   dev helpers
  docs/ARCHITECTURE.md                       this file
```

### Fixed decisions

| Decision | Choice | Why |
|---|---|---|
| Runtime | .NET 10 (LTS), self-contained | The target needs no .NET installed; Debian's packaged .NET lags |
| UI | Blazor Server + MudBlazor | Server-side state suits live metrics; no API layer to maintain |
| State | SQLite via EF Core | One small `Settings` table; migrations run at startup |
| Auth | Cookie, single admin password | LAN-facing, one user. No default password — first run sets one |
| Endpoints | Minimal APIs | Only where needed; modules are mostly pure Blazor |
| Deliberately absent | MediatR, MassTransit, any broker | `IEventBus` is ~90 lines and does the whole job |

---

## 2. Module loading

`ModuleLoader` scans `/usr/lib/easyhomeserver/modules/` (configurable; `./modules` in dev) and
treats each immediate subdirectory as one published module.

```
scan dir ─▶ find assembly ─▶ load in PluginLoadContext ─▶ check SDK version
         ─▶ find IModule ─▶ construct ─▶ validate manifest ─▶ ConfigureServices
```

Each step converts its own failures into a `ModuleLoadFailure`. **Nothing a module does at load
time can stop the host from starting.** Failures are logged and shown on the dashboard, because
a module that silently vanished from the nav is the worst possible outcome — the reason it
failed is exactly what the operator needs.

### Finding the assembly

A published module directory contains exactly one `*.deps.json`, named after the module
assembly. That is a more reliable marker than guessing among the DLLs. Falls back to the
`EasyHomeServer.Modules.*.dll` naming convention.

### Load contexts and the sharing rule

Each module gets its own `AssemblyLoadContext`, so two modules can depend on different versions
of the same library. But resolution **prefers the host**:

```csharp
try { return Default.LoadFromAssemblyName(name); }   // framework, SDK, MudBlazor
catch (FileNotFoundException) { /* module-private dep */ }
return LoadFromAssemblyPath(_resolver.ResolveAssemblyToPath(name));
```

This is the single most important rule in the codebase. **Type identity in .NET includes the
load context.** If a module loaded its own copy of `EasyHomeServer.Sdk`, the `IModule` it
implements would be a *different type* from the `IModule` the host searches for, and discovery
would find nothing — with no error. The same applies to MudBlazor: a private copy renders
components that the host's `MudThemeProvider` knows nothing about.

So modules reference the SDK and MudBlazor **compile-time only**, and the host ships the only
runtime copy:

```xml
<ProjectReference Include="../../EasyHomeServer.Sdk/EasyHomeServer.Sdk.csproj"
                  Private="false" ExcludeAssets="runtime" />
<PackageReference Include="MudBlazor" Version="9.7.0" ExcludeAssets="runtime" />
```

Both `packaging/build.sh` and `scripts/dev-publish-modules.sh` **fail the build** if
`EasyHomeServer.Sdk.dll` or `MudBlazor.dll` appears in a module's output. The failure it
prevents is silent, so it is caught mechanically rather than left to review.

### SDK contract versioning

The **assembly version of `EasyHomeServer.Sdk` is the single source of truth.**

A module records the SDK version it compiled against in its assembly references. The host reads
that reference and compares majors — before instantiating anything:

```csharp
var sdkRef = assembly.GetReferencedAssemblies().FirstOrDefault(a => a.Name == "EasyHomeServer.Sdk");
if (sdkRef.Version.Major != HostSdkVersion.Major) { /* reject with a clear reason */ }
```

This needs no cooperation from the module, cannot be faked by the manifest, and runs before any
module code does. A mismatch is reported, not crashed on:

> **systeminfo** — incompatible SDK version
> Built against SDK contract version 2 (=2.0.0.0), but this host provides version 1 (=1.0.0.0).
> Install a build of this module for SDK 1, or upgrade the host.

At the package layer the same contract appears as a Debian virtual package: the host declares
`Provides: easyhomeserver-sdk-1`, modules declare `Depends: easyhomeserver-sdk-1`. So dpkg
refuses an incompatible module at install time, and the loader is the backstop for anything that
gets past it.

**Bumping the contract** (a breaking SDK change) means changing two things in lockstep:
`EasyHomeServerSdkAssemblyVersion` in `Directory.Build.props`, and `SDK_CONTRACT_MAJOR` in
`packaging/build.sh`. Additive changes — a new optional member, a default interface method —
do not need a bump.

To see the rejection for yourself:

```bash
SDK_VERSION=2.0.0.0 scripts/dev-publish-modules.sh systeminfo
dotnet build src/EasyHomeServer.Host   # rebuild the host at SDK 1
dotnet run --project src/EasyHomeServer.Host
```

### No hot reload

Modules load once, at startup, into non-collectible contexts. Installing or removing a module
restarts the service; the maintainer scripts do this for you. Unloadable contexts would mean
every module author had to get teardown exactly right to avoid leaking a circuit, in exchange
for saving a two-second restart on a box that reboots monthly.

---

## 3. Static assets from plugins

**This is the sharp edge of the whole design.** Read this before adding CSS or JS to a module.

A Razor class library normally gets its `wwwroot` published to `_content/{AssemblyName}/` by the
static web assets pipeline. That pipeline resolves everything at the **host's build time**. A
module discovered at runtime was never part of the host's build, so its assets are invisible and
every request 404s.

The fix has three parts:

**1. The module embeds `wwwroot` into its assembly, with a manifest:**

```xml
<GenerateEmbeddedFilesManifest>true</GenerateEmbeddedFilesManifest>
<StaticWebAssetsEnabled>false</StaticWebAssetsEnabled>
...
<Content Remove="wwwroot\**" />
<EmbeddedResource Include="wwwroot\**" />
<PackageReference Include="Microsoft.Extensions.FileProviders.Embedded" Version="10.0.10"
                  ExcludeAssets="runtime" PrivateAssets="all" />
```

A *manifest* is required — plain `EmbeddedFileProvider` mangles paths by replacing separators
with dots, which breaks any filename containing a dot or dash.
`Content Remove` stops the Razor SDK also copying the files loose into the module directory,
where nothing serves them.

**2. The host maps them** (`PluginStaticFileProvider`): one `ManifestEmbeddedFileProvider` per
module assembly, routed by assembly name under `/_content/`. Registered *before* the component
endpoints so `/_content/*` is served as a file rather than falling through to routing.

**3. The page links them normally:**

```razor
<link rel="stylesheet" href="_content/EasyHomeServer.Modules.SystemInfo/systeminfo.css" />
```

A module with no `wwwroot` is simply absent from the map and misses cleanly — shipping no assets
is not an error.

> **Gotcha:** `ManifestEmbeddedFileProvider.GetDirectoryContents("")` reports `Exists = false`
> even when the root is populated. Probe with `"/"`. This cost an afternoon.

Verify with:
```bash
curl -i http://localhost:5000/_content/EasyHomeServer.Modules.SystemInfo/systeminfo.css
```

---

## 4. The SDK surface

Everything in `EasyHomeServer.Sdk`. Small on purpose: every type here is a compatibility
commitment.

### `IModule`

```csharp
public interface IModule
{
    ModuleManifest Manifest { get; }
    void ConfigureServices(IServiceCollection services, IModuleContext context);
    void MapEndpoints(IEndpointRouteBuilder endpoints) { }   // default no-op
}
```

Exactly one public, non-abstract implementation per module assembly, with a **parameterless
constructor** — modules are constructed before the DI container exists.

### `ModuleManifest`

`Id` (lowercase, unique — keys the data directory and nav), `DisplayName`, `Version`,
`RoutePath` (matches an `@page`), `Icon` (SVG path; MudBlazor's `Icons.Material.*` constants are
plain strings), `NavOrder`, `Description`.

### `IModuleContext`

`Configuration` — scoped to `Modules:{Id}`, so modules cannot read or collide with each other's
settings. `DataDirectory` — `/var/lib/easyhomeserver/modules/{Id}`, created for you.
`IsLinux` — for graceful degradation on a developer's machine.

### `IEventBus`

```csharp
ValueTask PublishAsync<TEvent>(TEvent payload, CancellationToken ct = default);
IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler);
```

In-process, typed, no broker. Handlers run concurrently; one that throws is logged and cannot
affect the publisher or other subscribers. Subscription is **by exact type** — base types and
interfaces do not receive derived events.

This is how modules talk without referencing each other. Publisher and subscriber only need to
share the event *type*. For a cross-module event — the planned Avahi module reacting to Docker
container events — put the event type in a small contracts assembly both reference, not in
either module. An event used only within one module (like `SystemSnapshot`) just lives there.

**Dispose your subscription.** A Blazor component that does not will be held alive by the bus
for the life of the process, and every closed tab leaks a circuit that still re-renders.

### `ISystemRunner`

```csharp
Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default);
Task<string> ReadFileAsync(string path, CancellationToken ct = default);
Task WriteFileAsync(string path, string content, CancellationToken ct = default);   // atomic
Task<ProcessResult> SystemctlAsync(SystemctlAction action, string? unit = null, CancellationToken ct = default);
```

**The only way module code touches the OS.** No `Process.Start` in a module, ever.

Today it runs in-process as root. The interface exists so that the privileged-worker split — web
process dropped to an unprivileged user, privileged operations over IPC — can happen without
touching a single module. That refactor is only possible if the seam holds *now*, while it is
free to enforce.

Arguments are passed as a list and never concatenated into a shell command line, so a value
containing spaces or `;` cannot be reinterpreted as syntax. There is deliberately no
"run this string in a shell" overload. `WriteFileAsync` is write-then-rename, so a crash
mid-write cannot leave a half-written config.

Reading `/proc` and `/sys` for *metrics* is exempt — use `System.IO` directly. Those are
unprivileged, and routing 2-second polling through a process abstraction buys nothing.

> **`/proc/self` is not the machine.** The unit sets `StateDirectory=` and `PrivateTmp=`, so the
> host process runs in its **own mount namespace** with bind mounts the rest of the machine does
> not have. Read `/proc/1/…` for anything describing the machine rather than this process, and
> use `mountinfo` rather than `mounts` — only mountinfo carries the root field that tells a bind
> mount apart from the filesystem it shadows. Reporting `/proc/self/mounts` listed
> `/var/lib/easyhomeserver` and `/var/tmp` as if they were real disks, each duplicating `/`.

### `ModuleBackgroundService`

Base class for periodic work. Register with `services.AddModuleWorker<T>()`, which registers it
as a singleton *and* as a hosted service, so components can inject the worker to read its
latest state.

It exists instead of plain `BackgroundService` for one reason: **since .NET 6, an unhandled
exception in a `BackgroundService` stops the entire host by default.** A misbehaving module must
never take the server down, so this class catches what escapes `ExecuteAsync`, logs it, and lets
the host carry on.

---

## 5. Writing a module

Worked example: a Docker module. Compare against
`src/modules/EasyHomeServer.Modules.SystemInfo/`, which exercises every pattern below.

### 1. Project

`src/modules/EasyHomeServer.Modules.Docker/EasyHomeServer.Modules.Docker.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <PropertyGroup>
    <RootNamespace>EasyHomeServer.Modules.Docker</RootNamespace>
    <AssemblyName>EasyHomeServer.Modules.Docker</AssemblyName>
    <Version>$(EasyHomeServerVersion)</Version>
    <GenerateEmbeddedFilesManifest>true</GenerateEmbeddedFilesManifest>
    <StaticWebAssetsEnabled>false</StaticWebAssetsEnabled>
  </PropertyGroup>

  <ItemGroup>
    <Content Remove="wwwroot\**" />
    <EmbeddedResource Include="wwwroot\**" />
  </ItemGroup>

  <ItemGroup>
    <!-- Compile-time only. The host ships the runtime copy. Non-negotiable — see §2. -->
    <ProjectReference Include="../../EasyHomeServer.Sdk/EasyHomeServer.Sdk.csproj"
                      Private="false" ExcludeAssets="runtime" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="MudBlazor" Version="9.7.0" ExcludeAssets="runtime" />
    <PackageReference Include="Microsoft.Extensions.FileProviders.Embedded" Version="10.0.10"
                      ExcludeAssets="runtime" PrivateAssets="all" />
    <!-- A genuinely module-private dependency: ships in this module's .deb, loads into its
         own context. This is the case the per-module load context exists for. -->
    <PackageReference Include="Docker.DotNet" Version="3.125.15" />
  </ItemGroup>

</Project>
```

Add it to `EasyHomeServer.slnx` under `/src/modules/`.

### 2. `_Imports.razor`

A module is a Razor class library and inherits none of the host's imports:

```razor
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Web
@using MudBlazor
@using EasyHomeServer.Sdk
@using EasyHomeServer.Modules.Docker
```

### 3. Entry point

```csharp
public sealed class DockerModule : IModule
{
    public ModuleManifest Manifest { get; } = new()
    {
        Id = "docker",                       // lowercase, unique
        DisplayName = "Docker",
        Version = "0.1.0",
        RoutePath = "/docker",               // must match an @page below
        Icon = Icons.Material.Filled.Dns,
        NavOrder = 20,                       // SystemInfo is 10
        Description = "Manage containers, images and volumes.",
    };

    public void ConfigureServices(IServiceCollection services, IModuleContext context)
    {
        var options = context.Configuration.Get<DockerOptions>() ?? new DockerOptions();
        services.AddSingleton(options);
        services.AddSingleton<DockerClientFactory>();
        services.AddModuleWorker<ContainerEventListener>();
    }
}
```

### 4. Page

`Pages/DockerPage.razor` — route must match `Manifest.RoutePath`:

```razor
@page "/docker"
@attribute [Authorize]
@implements IDisposable
@inject IEventBus EventBus

<link rel="stylesheet" href="_content/EasyHomeServer.Modules.Docker/docker.css" />

<PageTitle>Docker</PageTitle>
<MudText Typo="Typo.h4">Docker</MudText>

@code {
    private IDisposable? _subscription;

    protected override void OnInitialized()
    {
        _subscription = EventBus.Subscribe<ContainerChanged>(OnChanged);
    }

    private Task OnChanged(ContainerChanged e, CancellationToken ct)
    {
        // Events arrive on a background thread. InvokeAsync marshals back onto the circuit's
        // synchronisation context, which is required before touching render state.
        return InvokeAsync(StateHasChanged);
    }

    public void Dispose() => _subscription?.Dispose();
}
```

Pages are interactive automatically — the host applies `InteractiveServer` globally, except on
pages marked `[ExcludeFromInteractiveRouting]` (login and setup, which need a real form POST).
Modules do not declare a render mode.

### 5. Privileged work

```csharp
public sealed class ComposeService(ISystemRunner runner)
{
    public Task<ProcessResult> UpAsync(string projectDir, CancellationToken ct = default) =>
        runner.RunAsync("docker", ["compose", "--project-directory", projectDir, "up", "-d"], ct);
}
```

### 6. Publish and run

```bash
scripts/dev-publish-modules.sh docker
dotnet run --project src/EasyHomeServer.Host
```

The module appears in the nav. If it does not, the dashboard says why.

### 7. Package

`packaging/build.sh` picks up every directory under `src/modules/` automatically — no edit
needed. It derives the module id from the project name
(`EasyHomeServer.Modules.Docker` → `docker`) and produces
`easyhomeserver-module-docker_<version>_all.deb`.

Module packages are `Architecture: all`: a module is pure IL running on the runtime the host
already ships, so one package serves amd64 and arm64.

### Checklist

- [ ] SDK and MudBlazor referenced with `Private="false"` / `ExcludeAssets="runtime"`
- [ ] Exactly one public `IModule`, parameterless constructor
- [ ] `Manifest.Id` lowercase and unique; `RoutePath` matches an `@page`
- [ ] `wwwroot` embedded, `Content Remove`, `GenerateEmbeddedFilesManifest`
- [ ] Event subscriptions disposed in `Dispose`
- [ ] No `Process.Start` — `ISystemRunner` only
- [ ] Background loops observe their `CancellationToken`
- [ ] Stateful readers (counter deltas, history) registered as singletons
- [ ] Degrades gracefully when `IModuleContext.IsLinux` is false

---

## 6. Host internals

Only relevant if you change the host itself.

**Startup order** is forced by the plugin model — modules must be discovered *before* the DI
container is built, because each contributes registrations. Hence the bootstrap `ILoggerFactory`
in `Program.cs`: the scan produces log output before the real logging pipeline exists.

```
scan modules → register host services → module.ConfigureServices(...) → build()
             → migrate SQLite → static files → auth → map components (+ module assemblies)
             → module.MapEndpoints(...) → run
```

**Routing.** The host has no compile-time reference to any module, so the router is told about
their assemblies at runtime — `Router.AdditionalAssemblies` in `Routes.razor` for in-circuit
navigation, and `AddAdditionalAssemblies` on `MapRazorComponents` for endpoint routing. Both are
needed.

**Auth.** Cookie, one admin, deny-by-default via a fallback authorization policy; pages opt out
with `[AllowAnonymous]`. Data protection keys are persisted to
`/var/lib/easyhomeserver/keys` — without that, keys regenerate on every start and every session
is silently signed out on restart. There is no default password: `FirstRunSetupMiddleware`
funnels a fresh install to `/setup` until one is set.

**Dev paths.** `appsettings.Development.json` points `ModulesPath` and `DataPath` at repository
root (`../../modules`, `../../.devdata`) — *not* at the host project. On a case-insensitive
filesystem (macOS by default) `./modules` and the host's own `Modules/` source directory are the
same directory, and `./data` collides with `Data/`. Publishing into the source tree, or
`rm -rf`-ing it, is exactly as bad as it sounds.

---

## 7. Packaging

```bash
packaging/build.sh                        # amd64
packaging/build.sh --arch arm64           # arm64
packaging/build.sh --module systeminfo    # host + one module
```

Needs `dotnet` and `dpkg-deb` (`brew install dpkg` on macOS); builds on any OS that can
cross-publish for Linux. Only *installing* needs Debian.

| | `easyhomeserver` | `easyhomeserver-module-*` |
|---|---|---|
| Architecture | `amd64` / `arm64` | `all` |
| Publish | self-contained, untrimmed | framework-dependent |
| Installs to | `/usr/lib/easyhomeserver/` | `/usr/lib/easyhomeserver/modules/{id}/` |
| Contract | `Provides: easyhomeserver-sdk-1` | `Depends: easyhomeserver-sdk-1` |
| Size | ~34 MB | ~30 KB |

Self-contained so the target needs no .NET. **Untrimmed** because Blazor and the plugin loader
resolve types by reflection, which the trimmer cannot see and would silently strip.

Install order is host first — modules depend on the contract it provides:

```bash
sudo dpkg -i easyhomeserver_0.1.0_arm64.deb
sudo dpkg -i easyhomeserver-module-systeminfo_0.1.0_all.deb
```

**Service.** `easyhomeserver.service`, `Type=notify` (the host calls `UseSystemd()`, so systemd
learns it is ready when Kestrel is actually listening), `Restart=on-failure`, port 5000, root.
`StateDirectory=easyhomeserver` creates `/var/lib/easyhomeserver` at 0700.

Root is deliberate and declared: this tool manages the machine — systemd, BIND zone files,
mounts, Samba. `ISystemRunner` is the path to fixing that properly.

**Maintainer scripts.** Host postinst enables and restarts, preserving a deliberate `disable`
via `deb-systemd-helper`. Module postinst restarts the host *only if already running* (there is
no hot-load). Purge — not remove — deletes `/var/lib/easyhomeserver`, so a remove/reinstall
cycle does not reset the machine to first-run setup.

```bash
journalctl -u easyhomeserver -f
```

---

## 8. Development

```bash
scripts/dev-publish-modules.sh            # publish modules into ./modules/
dotnet run --project src/EasyHomeServer.Host
```

**On macOS or Windows, the SystemInfo module cannot show metrics** — it reads `/proc`, and the
page will honestly say so. To see it working, run it on Linux:

```bash
scripts/run-docker.sh          # http://localhost:5199
scripts/run-docker.sh --fresh  # wipe the database first
```

That builds a self-contained Linux publish laid out exactly as the `.deb` installs it, so it
also rehearses packaging.

**Style.** Nullable enabled, warnings as errors, file-scoped namespaces, braces on every
control-flow body, a blank line between properties.

**A note on comments.** Comments in this codebase explain *why*, and are concentrated where the
reason is not recoverable from the code: the sharing rule in `PluginLoadContext`, the probe path
in `PluginStaticFileProvider`, the `BackgroundService` exception behaviour, the case-insensitive
path collision. Those are the things that cost time to rediscover.
