# Atlas SDK for .NET

[English](README.md) · [中文](README.zh.md)

[App Atlas](https://appatlas.dev)의 Windows 클라이언트. 의존성이 없습니다.
`netstandard2.0` 자산은 .NET Framework 4.6.1+, .NET Core / 5+, UWP, WinUI,
Unity에서 로드되고, `net8.0-windows10.0.17763+` 타깃에는 디퍼드 링크를
스스로 클레임하는 자산이 갑니다.

## 설치

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

// URI 프로토콜 활성화(앱이 등록한 스킴, 또는 방문 URL).
AtlasLinks.Handle(activationUri);
```

#### Visual Basic

```vb
Atlas.Start("sdk_…")

AtlasLinks.SetListener(Sub(link)
                           ' link.Payload / link.Path / link.Deferred / link.Match
                           ' link.Channel / link.Campaign / link.ShortId
                       End Sub)

' URI 프로토콜 활성화(앱이 등록한 스킴, 또는 방문 URL).
AtlasLinks.Handle(activationUri)
```
<!-- tabs:end -->

리스너가 붙기 전에 도착한 링크는 보관했다가 다시 전달하므로,
시작 시점의 활성화도 잃지 않습니다.

## 디퍼드 링크

Microsoft Store는 설치 과정에 campaign id를 실어 보냅니다.
`net8.0-windows10.0.17763` 이상을 타깃하고 패키지 신원이 있는 앱은
Atlas.Start에서 자동으로 클레임하며, 호출할 것이 없습니다.
그 외 타깃에서는 패키징이 허락하는 곳에서 읽어 한 번 넘겨주세요.

<!-- tabs:start -->
#### C#

```csharp
// 패키지 앱: StoreContext의 campaign id, 또는 설치 프로그램이 기록한 값.
// 패키지가 아닌 앱은 이 호출을 건너뛰어도 됩니다.
AtlasLinks.ClaimCampaignId(campaignId);
```

#### Visual Basic

```vb
' 패키지 앱: StoreContext의 campaign id, 또는 설치 프로그램이 기록한 값.
' 패키지가 아닌 앱은 이 호출을 건너뛰어도 됩니다.
AtlasLinks.ClaimCampaignId(campaignId)
```
<!-- tabs:end -->

netstandard 자산이 대신 읽지 않는 것은 의도된 선택입니다. 그 값을
읽으려면 WinRT가 필요한데, WinRT는 패키징 형태에 따라 있기도 하고 없기도
해서, netstandard2.0 어셈블리가 그것을 추측하면 호스트마다 다르게
깨집니다. windows 자산은 진짜 WinRT 참조를 갖고 있어서 자동 경로가
거기에만 있습니다.

`AtlasLinks.FirstReferringLink()`는 설치를 만든 링크를 언제까지나
돌려줍니다.

## 디스크를 다루는 방식

엔벨로프는 네트워크 시도 전에 먼저 디스크에 쓰이며, 디렉터리는 프로세스
id로 구분됩니다. 데스크톱 앱은 한 실행 파일이 여러 인스턴스로 도는 일이
흔하고, 한 디렉터리에 전송기가 둘이면 그것은 예정된 손상이기 때문입니다.
시작할 때 큐는 죽은 인스턴스가 남긴 것을 거둬들이므로, 크래시 직전에
쓰인 엔벨로프도 결국 떠납니다.

## 프라이버시

SDK는 설치 단위의 난수 id 하나를 만들 뿐, 머신 식별자나 하드웨어
식별자를 읽지 않습니다. 함께 보내는 기기 정보(OS 버전, 아키텍처, 런타임,
로캘, 시간대, 앱 버전)는 일반적인 크래시 리포트 항목이며 누구도 특정하지
않습니다.
<!-- guide:end -->

## 검사

```sh
sh check-core.sh                             # 빌드, 인프로세스 리스너로 흐름 실행,
                                             # 골든 바이트 비교
ATLAS_SERVER=../app-atlas sh check-core.sh   # 서버의 실제 파서까지
```

MIT.
