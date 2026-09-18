# Atlas SDK for .NET

[한국어](README.ko.md) · [中文](README.zh.md)

The client half of [App Atlas](https://appatlas.dev) on Windows. No
dependencies. The `netstandard2.0` asset loads on .NET Framework 4.6.1+,
.NET Core / 5+, UWP, WinUI and Unity; a `net8.0-windows10.0.17763+` target
gets an asset that also claims the deferred link by itself.

## Install

<!-- tabs:start -->
#### CLI

```sh
dotnet add package AppAtlas.Sdk
```

#### .csproj

```xml
<PackageReference Include="AppAtlas.Sdk" Version="0.3.0" />
```

#### Package Manager Console

```powershell
Install-Package AppAtlas.Sdk
```
<!-- tabs:end -->

<!-- guide:start -->

## Core

<!-- tabs:start -->
#### C#

```csharp title="App.xaml.cs (WinUI 3)"
// App.xaml.cs (WinUI 3): WPF and console apps call Atlas.Start from their
// own startup path the same way.
protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
{
    Atlas.Start("sdk_…");
    // Modules (Links, Crash) wire in from here.

    // … window creation
}
```

#### Visual Basic

```vb title="Application.xaml.vb (WPF)"
' Application.xaml.vb (WPF): WinForms calls Atlas.Start from Sub Main or the
' ApplicationEvents startup handler the same way.
Protected Overrides Sub OnStartup(e As StartupEventArgs)
    MyBase.OnStartup(e)
    Atlas.Start("sdk_…")
    ' Modules (Links, Crash) wire in from here.
End Sub
```
<!-- tabs:end -->

### Modules

| Namespace | What it is |
|---|---|
| `AppAtlas.Sdk` | Envelopes, the disk queue, the sender. Every module rides it. |
| `AppAtlas.Sdk.Links` | Deep-link inflow: protocol activation and the store campaign id. |
| `AppAtlas.Sdk.Crash` | Crash reporting: unhandled exceptions from every hook the host has, native deaths through WER, UI hangs, sessions. |

One assembly holds all three; a module the app never calls costs nothing at
run time.

### What it does with the disk

Envelopes are written before any network attempt, in a directory keyed by
process id — desktop apps run several instances of one executable as a matter
of course, and two senders over one directory is a corruption waiting to
happen. At start, the queue adopts what dead instances left behind, so the
envelope written moments before a crash still leaves.

## Links

<!-- tabs:start -->
#### C#

```csharp title="App.xaml.cs (WinUI 3)"
// App.xaml.cs (WinUI 3)
protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
{
    Atlas.Start("sdk_…");

    AtlasLinks.SetListener(link =>
    {
        // Direct opens and the deferred link arrive here alike.
        // link.Deferred: true when the link crossed the install.
        // link.Match: referrer / clipboard / campaign_id / relink.
        // Route with link.Path and link.Payload, e.g.:
        // if (link.Path != null) OpenScreen(link.Path, link.Payload);
    });

    // URI protocol activation (the app's registered scheme, or a visit URL).
    var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
    if (activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Protocol
        && activation.Data is Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs protocol)
    {
        AtlasLinks.Handle(protocol.Uri.ToString());
    }

    // … window creation
}
```

#### Visual Basic

```vb title="Application.xaml.vb (WPF)"
' Application.xaml.vb (WPF)
Protected Overrides Sub OnStartup(e As StartupEventArgs)
    MyBase.OnStartup(e)
    Atlas.Start("sdk_…")

    AtlasLinks.SetListener(Sub(link)
                               ' Direct opens and the deferred link arrive here alike.
                               ' link.Deferred: true when the link crossed the install.
                               ' link.Match: referrer / clipboard / campaign_id / relink.
                               ' Route with link.Path and link.Payload, e.g.:
                               ' If link.Path IsNot Nothing Then OpenScreen(link.Path, link.Payload)
                           End Sub)

    ' URI protocol activation (your app's registered scheme, or a visit URL);
    ' WPF and WinForms receive it in the command-line arguments.
    If e.Args.Length > 0 Then AtlasLinks.Handle(e.Args(0))
End Sub
```
<!-- tabs:end -->

A link that arrives before the listener is attached is queued and replayed, so
an activation at startup is never lost.

### The deferred link

The Microsoft Store carries a campaign id through the install. An app
targeting `net8.0-windows10.0.17763` or later with package identity claims it
at Atlas.Start, with nothing to call. On other targets, read it where your
packaging allows and hand it over once:

<!-- tabs:start -->
#### C#

```csharp
// Packaged apps: StoreContext's campaign id, or whatever your installer
// recorded. Unpackaged apps can skip this entirely.
AtlasLinks.ClaimCampaignId(campaignId);
```

#### Visual Basic

```vb
' Packaged apps: StoreContext's campaign id, or whatever your installer
' recorded. Unpackaged apps can skip this entirely.
AtlasLinks.ClaimCampaignId(campaignId)
```
<!-- tabs:end -->

The netstandard asset does not fetch it on purpose: reading it needs WinRT,
which is available under some packaging shapes and not others, and a
netstandard2.0 assembly guessing at that breaks differently on every host.
The windows asset carries real WinRT references instead, which is why the
automatic path lives there alone.

`AtlasLinks.FirstReferringLink()` returns the link that produced the install,
forever.

## Crash

<!-- tabs:start -->
#### C#

```csharp title="App.xaml.cs (WPF)"
// App.xaml.cs (WPF): WinForms and console apps call Atlas.Start from their own
// startup path the same way.
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);
    Atlas.Start("sdk_…");
    // Crashes, hangs and native deaths are caught from this line on. The rest is optional.

    // Your own id for the signed-in user, and the state worth seeing beside a crash.
    AtlasCrash.SetUserId("u-123");
    AtlasCrash.SetKey("screen", "checkout");
    AtlasCrash.LeaveBreadcrumb("cart", "add");
    AtlasCrash.Log("cart total recomputed");
}
```

```csharp title="CheckoutPage.xaml.cs"
// CheckoutPage.xaml.cs: anywhere an exception is caught but still worth knowing about.
private void Pay()
{
    try
    {
        cart.Charge();
    }
    catch (PaymentException error)
    {
        AtlasCrash.RecordError(error);
        // The app's own recovery goes here. Example:
        // ShowRetry();
    }
}
```

#### Visual Basic

```vb title="Application.xaml.vb (WPF)"
' Application.xaml.vb (WPF): WinForms and console apps call Atlas.Start from
' their own startup path the same way.
Protected Overrides Sub OnStartup(e As StartupEventArgs)
    MyBase.OnStartup(e)
    Atlas.Start("sdk_…")
    ' Crashes, hangs and native deaths are caught from this line on. The rest is optional.

    ' Your own id for the signed-in user, and the state worth seeing beside a crash.
    AtlasCrash.SetUserId("u-123")
    AtlasCrash.SetKey("screen", "checkout")
    AtlasCrash.LeaveBreadcrumb("cart", "add")
    AtlasCrash.Log("cart total recomputed")
End Sub
```

```vb title="CheckoutPage.xaml.vb"
' CheckoutPage.xaml.vb: anywhere an exception is caught but still worth knowing about.
Private Sub Pay()
    Try
        cart.Charge()
    Catch err As PaymentException
        AtlasCrash.RecordError(err)
        ' The app's own recovery goes here. Example:
        ' ShowRetry()
    End Try
End Sub
```
<!-- tabs:end -->

What is caught, with no call beyond `Atlas.Start`:

| Death | How it is caught |
|---|---|
| An unhandled exception on any thread, async paths included | `AppDomain.UnhandledException`, the backstop every host has — written to disk at that instant, since the process ends when the handler returns |
| An exception a task nobody awaited | `TaskScheduler.UnobservedTaskException`, reported as a handled error |
| An exception on the UI thread of WPF, WinForms or WinUI 3 | `Dispatcher.UnhandledException`, `Application.ThreadException`, `Application.UnhandledException` — attached by name when that framework is loaded, reported as an error before the app decides; the backstop still writes the crash if nothing handles it |
| A native death: an access violation in interop, a stack overflow, `FailFast`, heap corruption | Windows Error Reporting's LocalDumps, registered for this executable under the user's own registry hive at start; the dump it leaves is read at the next start for the exception code, the faulting address and its module, then deleted |
| A UI-thread hang | A watchdog: five seconds without an answer through the UI thread's `SynchronizationContext`, once per freeze; only where such a thread exists |
| A death nothing explains — a kill, a stack overflow no dump caught, a power cut | The run's own record, kept per process id: no crash, no dump, no exit event ends the session as abnormal, and invents no issue |

A crash is written to disk on the dying thread together with the end of its
session, which is what crash-free sessions are counted from, then flushed for
the two seconds a terminating process can afford; whatever did not leave goes
at the next start. Every report carries the last 100 breadcrumbs, up to 64
keys, the newest 64 KB of `AtlasCrash.Log` lines, and the process's state at
that moment: working set, managed heap, free disk, thread and handle counts.
A crash within five seconds of start is sent first thing at the next start.

Frames name the declaring type and method as the source spells them — async
state machines, lambdas and local functions are given back their names — with
the file and line whenever the build shipped its PDB beside the assembly, and
always with the method token, IL offset and the module's debug id, so a build
that strips its PDBs can still be resolved later. Several instances of one
executable keep separate queues and records, and a dead instance's leftovers
are adopted by the next one to start.

`AtlasCrash.SetEnabled(false)` stops collection and remembers the choice, for
a consent screen. `AtlasCrash.CrashedLastRun` says whether a previous run
ended in a crash this SDK recorded — its own, or the dump the OS left.

## Privacy

The SDK mints an install-scoped random id and reads no machine or hardware
identifier. Device context (OS version, architecture, runtime, locale,
timezone, app version) is the standard crash-report set and identifies no one.

<!-- guide:end -->

## Checks

```sh
sh check-core.sh                             # builds, runs the flow against an
                                             # in-process listener, spawns itself as a
                                             # victim and dies every way the crash hooks
                                             # catch, compares goldens
ATLAS_SERVER=../app-atlas sh check-core.sh   # and the server's own parser
```

MIT.
