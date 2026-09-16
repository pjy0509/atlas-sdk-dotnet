# Atlas SDK for .NET

[한국어](README.ko.md) · [中文](README.zh.md)

The client half of [App Atlas](https://appatlas.dev) on Windows. One
`netstandard2.0` assembly with no dependencies, so it loads on .NET Framework
4.6.1+, .NET Core / 5+, UWP, WinUI and Unity alike.

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
<!-- tabs:start -->
#### C#

```csharp
Atlas.Start("sdk_…");

AtlasLinks.SetListener(link =>
{
    // link.Payload / link.Path / link.Deferred / link.Match
    // link.Channel / link.Campaign / link.ShortId
});

// URI protocol activation (your app's registered scheme, or a visit URL).
AtlasLinks.Handle(activationUri);
```

#### Visual Basic

```vb
Atlas.Start("sdk_…")

AtlasLinks.SetListener(Sub(link)
                           ' link.Payload / link.Path / link.Deferred / link.Match
                           ' link.Channel / link.Campaign / link.ShortId
                       End Sub)

' URI protocol activation (your app's registered scheme, or a visit URL).
AtlasLinks.Handle(activationUri)
```
<!-- tabs:end -->

A link that arrives before the listener is attached is queued and replayed, so
an activation at startup is never lost.

## The deferred link

The Microsoft Store carries a campaign id through the install. Read it where
your packaging allows and hand it over once:

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

The SDK does not fetch it for you on purpose: reading it needs WinRT, which is
available under some packaging shapes and not others, and a netstandard2.0
assembly guessing at that breaks differently on every host. Your app knows how
it ships.

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
