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
<PackageReference Include="AppAtlas.Sdk" Version="0.4.0" />
```

#### Package Manager Console

```powershell
Install-Package AppAtlas.Sdk
```
<!-- tabs:end -->

<!-- guide:start -->

## 코어

<!-- tabs:start -->
#### C#

```csharp title="App.xaml.cs (WinUI 3)"
// App.xaml.cs (WinUI 3): WPF·콘솔 앱도 각자의 시작 지점에서 같은 방식으로
// Atlas.Start를 호출합니다.
protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
{
    Atlas.Start("sdk_…");
    // 모듈(Links, Crash)은 여기서부터 배선합니다.

    // … 창 생성
}
```

#### Visual Basic

```vb title="Application.xaml.vb (WPF)"
' Application.xaml.vb (WPF): WinForms는 Sub Main이나 ApplicationEvents 시작
' 핸들러에서 같은 방식으로 Atlas.Start를 호출합니다.
Protected Overrides Sub OnStartup(e As StartupEventArgs)
    MyBase.OnStartup(e)
    Atlas.Start("sdk_…")
    ' 모듈(Links, Crash)은 여기서부터 배선합니다.
End Sub
```
<!-- tabs:end -->

프로젝트 파일에 키를 선언하는 방법도 있습니다. 패키지가 키를 실행 파일의 어셈블리
메타데이터에 기록하고, 인자 없는 `Atlas.Start()`가 거기서 읽습니다. .NET Core에서는
런타임이 `Main`보다 먼저 SDK의 startup hook을 호출하므로 앱은 첫 줄부터 수집됩니다.
WPF, WinForms, WinUI, Avalonia 훅은 해당 프레임워크가 로드되는 시점에 연결됩니다.
`Atlas.Start`를 함께 호출해도 무방하며, 두 번째 호출은 무시됩니다. `AtlasAutoStart`를
false로 두면 키는 유지하되 시작은 코드에 맡기고, `AtlasBaseUrl`은 서버 주소를 바꾸며,
환경 변수 `ATLAS_SDK_KEY`로 런처가 키를 넘길 수도 있습니다.

```xml title="App.csproj"
<!-- App.csproj: 키 하나면 됩니다. -->
<PropertyGroup>
  <AtlasSdkKey>sdk_…</AtlasSdkKey>
</PropertyGroup>
```

### 모듈

| 네임스페이스 | 역할 |
|---|---|
| `AppAtlas.Sdk` | 엔벨로프, 디스크 큐, 전송기. 모든 모듈의 바탕입니다. |
| `AppAtlas.Sdk.Links` | 딥링크 유입: 프로토콜 활성화와 스토어 캠페인 id. |
| `AppAtlas.Sdk.Crash` | 크래시 리포팅: 호스트가 가진 모든 훅의 미처리 예외, WER를 통한 네이티브 사망, UI 행, 세션. |

어셈블리 하나에 셋이 다 들어 있으며, 앱이 부르지 않는 모듈은 실행 시
비용이 없습니다.

### 디스크를 다루는 방식

엔벨로프는 네트워크 시도 전에 먼저 디스크에 쓰이며, 디렉터리는 프로세스
id로 구분됩니다. 데스크톱 앱은 한 실행 파일이 여러 인스턴스로 도는 일이
흔하고, 한 디렉터리에 전송기가 둘이면 그것은 예정된 손상이기 때문입니다.
시작할 때 큐는 죽은 인스턴스가 남긴 것을 거둬들이므로, 크래시 직전에
쓰인 엔벨로프도 결국 떠납니다.

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
        // 직접 열림과 디퍼드 링크가 같은 자리로 옵니다.
        // link.Deferred: 설치를 건너온 링크면 true.
        // link.Match: referrer / clipboard / campaign_id / relink.
        // link.Path와 link.Payload로 화면을 이동합니다. 예:
        // if (link.Path != null) OpenScreen(link.Path, link.Payload);
    });

    // URI 프로토콜 활성화(앱이 등록한 스킴, 또는 방문 URL).
    var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
    if (activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Protocol
        && activation.Data is Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs protocol)
    {
        AtlasLinks.Handle(protocol.Uri.ToString());
    }

    // … 창 생성
}
```

#### Visual Basic

```vb title="Application.xaml.vb (WPF)"
' Application.xaml.vb (WPF)
Protected Overrides Sub OnStartup(e As StartupEventArgs)
    MyBase.OnStartup(e)
    Atlas.Start("sdk_…")

    AtlasLinks.SetListener(Sub(link)
                               ' 직접 열림과 디퍼드 링크가 같은 자리로 옵니다.
                               ' link.Deferred: 설치를 건너온 링크면 true.
                               ' link.Match: referrer / clipboard / campaign_id / relink.
                               ' link.Path와 link.Payload로 화면을 이동합니다. 예:
                               ' If link.Path IsNot Nothing Then OpenScreen(link.Path, link.Payload)
                           End Sub)

    ' URI 프로토콜 활성화(앱이 등록한 스킴, 또는 방문 URL).
    ' WPF·WinForms는 명령줄 인자에서 받습니다.
    If e.Args.Length > 0 Then AtlasLinks.Handle(e.Args(0))
End Sub
```
<!-- tabs:end -->

리스너가 붙기 전에 도착한 링크는 보관했다가 다시 전달하므로,
시작 시점의 활성화도 잃지 않습니다.

### 디퍼드 링크

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

## Crash

<!-- tabs:start -->
#### C#

```csharp title="App.xaml.cs (WPF)"
// App.xaml.cs (WPF): WinForms와 콘솔 앱도 각자의 시작 경로에서 같은 방식으로
// Atlas.Start를 부릅니다.
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);
    Atlas.Start("sdk_…");

    // 아래는 선택입니다.
    // 로그인한 사용자를 크래시 옆에 남길 때.
    AtlasCrash.SetUserId("u-123");
    // 이슈를 좁힐 축이 필요할 때. 실험 그룹, 서버 환경, 화면.
    AtlasCrash.SetKey("screen", "checkout");
    // SDK가 모르는 앱 고유의 단계를 남길 때. 화면 전환과 시스템 이벤트는 이미 자동입니다.
    AtlasCrash.LeaveBreadcrumb("cart", "add");
    // 크래시 직전 코드 경로를 문장으로 남길 때.
    AtlasCrash.Log("cart total recomputed");
}
```

```csharp title="CheckoutPage.xaml.cs"
// CheckoutPage.xaml.cs: 예외를 잡았지만 알아 둘 가치가 있는 곳 어디서든.
private void Pay()
{
    try
    {
        cart.Charge();
    }
    catch (PaymentException error)
    {
        AtlasCrash.RecordError(error);
        // 앱 자체의 복구는 여기에. 예:
        // ShowRetry();
    }
}
```

#### Visual Basic

```vb title="Application.xaml.vb (WPF)"
' Application.xaml.vb (WPF): WinForms와 콘솔 앱도 각자의 시작 경로에서 같은
' 방식으로 Atlas.Start를 부릅니다.
Protected Overrides Sub OnStartup(e As StartupEventArgs)
    MyBase.OnStartup(e)
    Atlas.Start("sdk_…")

    ' 아래는 선택입니다.
    ' 로그인한 사용자를 크래시 옆에 남길 때.
    AtlasCrash.SetUserId("u-123")
    ' 이슈를 좁힐 축이 필요할 때. 실험 그룹, 서버 환경, 화면.
    AtlasCrash.SetKey("screen", "checkout")
    ' SDK가 모르는 앱 고유의 단계를 남길 때. 화면 전환과 시스템 이벤트는 이미 자동입니다.
    AtlasCrash.LeaveBreadcrumb("cart", "add")
    ' 크래시 직전 코드 경로를 문장으로 남길 때.
    AtlasCrash.Log("cart total recomputed")
End Sub
```

```vb title="CheckoutPage.xaml.vb"
' CheckoutPage.xaml.vb: 예외를 잡았지만 알아 둘 가치가 있는 곳 어디서든.
Private Sub Pay()
    Try
        cart.Charge()
    Catch err As PaymentException
        AtlasCrash.RecordError(err)
        ' 앱 자체의 복구는 여기에. 예:
        ' ShowRetry()
    End Try
End Sub
```
<!-- tabs:end -->

`Atlas.Start` 외에 아무 호출 없이 잡히는 것:

| 죽는 방식 | 잡는 방법 |
|---|---|
| 어느 스레드든 미처리 예외, async 경로 포함. | 모든 호스트에 있는 백스톱 `AppDomain.UnhandledException`. 핸들러가 돌아오면 프로세스가 끝나므로 그 순간 디스크에 씁니다. |
| 아무도 await하지 않은 Task의 예외. | `TaskScheduler.UnobservedTaskException`. 처리된 오류로 보고합니다. |
| WPF, WinForms, WinUI 3, Avalonia의 UI 스레드 예외. | `Dispatcher.UnhandledException`, `Application.ThreadException`, `Application.UnhandledException`, `Dispatcher.UIThread.UnhandledException`. 그 프레임워크가 로드되는 시점에 이름으로 연결되며, 앱이 처리 여부를 정하기 전이므로 오류로 보고합니다. 아무도 처리하지 않으면 백스톱이 크래시를 씁니다. |
| 네이티브 폴트: 인터롭의 액세스 위반, 힙 손상, 잘못된 명령. | 프로세스의 최상위 예외 필터. 관리 코드에서 등록해 런타임의 필터 앞에 체인으로 두며, 폴트가 발생한 스레드에서 그 즉시 기록합니다. OS 언와인더가 걷는 해당 스레드의 스택(프레임마다 모듈, 오프셋, debug id, 관리 프레임 포함)이 함께 실립니다. |
| 스택 오버플로, `FailFast`: 어떤 필터도 거치지 않는 종료. | Windows Error Reporting의 LocalDumps. 시작 때 이 실행 파일에 대해 사용자 레지스트리 하이브에 등록하며(Windows가 해당 키를 인정하는 환경에서), 남긴 덤프를 다음 실행 때 읽어 예외 코드, 폴트 주소, 해당 모듈을 얻은 뒤 삭제합니다. |
| UI 스레드 행. | 워치독: UI 스레드의 `SynchronizationContext`, 또는 앱이 생성한 뒤 찾아낸 WPF Dispatcher나 WinForms 폼을 통해 5초 동안 응답이 없으면 freeze당 한 번 보고합니다. 그런 스레드가 있을 때만 동작하며, 절전에서 깨어난 직후의 틱은 세지 않습니다. |
| 설명할 수 없는 죽음: kill, 덤프가 못 잡은 스택 오버플로, 전원 차단. | 프로세스 id별로 남기는 실행 기록. 크래시도 덤프도 종료 이벤트도 없으면 세션을 abnormal로 끝내고, 이슈는 만들지 않습니다. |

크래시는 죽어 가는 스레드에서 세션 종료 상태와 함께 디스크에 먼저 기록되고,
끝나는 프로세스가 감당할 수 있는 2초 동안 전송을 시도합니다. crash-free
세션은 이 세션으로 계산합니다. 떠나지 못한 것은 다음 실행 때 떠납니다.
모든 리포트에 최근 브레드크럼 100개, 키 64개, `AtlasCrash.Log`의 최근 64KB,
그리고 그 순간의 프로세스 상태가 실립니다. 워킹 셋, 관리 힙, 남은 디스크,
스레드·핸들 수입니다. 시작 후 5초 안에 난 크래시는 다음 실행에서 가장 먼저
전송됩니다.

프레임은 소스에 쓰인 대로 선언 타입과 메서드를 이름합니다. async 상태 머신,
람다, 로컬 함수는 원래 이름을 돌려받습니다. 빌드가 어셈블리 옆에 PDB를
동봉했으면 파일과 행이 함께 오고, 메서드 토큰·IL 오프셋·모듈의 debug id는
항상 실리므로 PDB를 뺀 빌드도 나중에 풀 수 있습니다. 한 실행 파일의 여러
인스턴스는 큐와 기록을 따로 가지며, 죽은 인스턴스가 남긴 것은 다음에 뜨는
인스턴스가 거둬들입니다.

`AtlasCrash.SetEnabled(false)`는 수집을 멈추고 그 선택을 기억합니다. 동의
화면에 씁니다. `AtlasCrash.CrashedLastRun`은 이전 실행이 이 SDK가 기록한
크래시로 끝났는지 알려 줍니다. 자기 것이든, OS가 남긴 덤프든 마찬가지입니다.

## 프라이버시

SDK는 설치 단위의 난수 id 하나를 만들 뿐, 머신 식별자나 하드웨어
식별자를 읽지 않습니다. 함께 보내는 기기 정보(OS 버전, 아키텍처, 런타임,
로캘, 시간대, 앱 버전)는 일반적인 크래시 리포트 항목이며 누구도 특정하지
않습니다.

<!-- guide:end -->

## 검사

```sh
sh check-core.sh                             # 빌드, 인프로세스 리스너로 흐름 실행,
                                             # 자기 자신을 victim으로 띄워 크래시 훅이
                                             # 잡는 방식마다 죽여 보고, 골든 바이트 비교
ATLAS_SERVER=../app-atlas sh check-core.sh   # 서버의 실제 파서까지
```

MIT.
