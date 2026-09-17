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
<PackageReference Include="AppAtlas.Sdk" Version="0.1.0" />
```

#### Package Manager Console

```powershell
Install-Package AppAtlas.Sdk
```
<!-- tabs:end -->

<!-- guide:start -->
## Start

<!-- tabs:start -->
#### C#

```csharp title="App.xaml.cs (WinUI 3)"
// App.xaml.cs (WinUI 3): WPF and console apps call Atlas.Start from their
// own startup path the same way.
protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
{
    Atlas.Start("sdk_…");
    // Modules (Links, later Push and Crash) wire in from here.

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
    ' Modules (Links, later Push and Crash) wire in from here.
End Sub
```
<!-- tabs:end -->

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

## The deferred link

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

## What it does with the disk

Envelopes are written before any network attempt, in a directory keyed by
process id — desktop apps run several instances of one executable as a matter
of course, and two senders over one directory is a corruption waiting to
happen. At start, the queue adopts what dead instances left behind, so the
envelope written moments before a crash still leaves.

## Privacy

The SDK mints an install-scoped random id and reads no machine or hardware
identifier. Device context (OS version, architecture, runtime, locale,
timezone, app version) is the standard crash-report set and identifies no one.
<!-- guide:end -->

## Checks

```sh
sh check-core.sh                             # builds, runs the flow against an
                                             # in-process listener, compares goldens
ATLAS_SERVER=../app-atlas sh check-core.sh   # and the server's own parser
```

MIT.
