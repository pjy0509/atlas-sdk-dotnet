# Atlas SDK for .NET

[English](README.md) · [한국어](README.ko.md)

[App Atlas](https://appatlas.dev) 的 Windows 客户端。零依赖。
`netstandard2.0` 资产在 .NET Framework 4.6.1+、.NET Core / 5+、UWP、WinUI
和 Unity 上加载；`net8.0-windows10.0.17763+` 目标会得到一个能自行兑换
延迟链接的资产。

## 安装

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

## 核心

<!-- tabs:start -->
#### C#

```csharp title="App.xaml.cs (WinUI 3)"
// App.xaml.cs (WinUI 3): WPF 与控制台应用也在各自的启动位置以同样方式
// 调用 Atlas.Start。
protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
{
    Atlas.Start("sdk_…");
    // 各模块（Links、Crash）从这里开始接线。

    // … 创建窗口
}
```

#### Visual Basic

```vb title="Application.xaml.vb (WPF)"
' Application.xaml.vb (WPF): WinForms 在 Sub Main 或 ApplicationEvents 的
' 启动处理器中以同样方式调用 Atlas.Start。
Protected Overrides Sub OnStartup(e As StartupEventArgs)
    MyBase.OnStartup(e)
    Atlas.Start("sdk_…")
    ' 各模块（Links、Crash）从这里开始接线。
End Sub
```
<!-- tabs:end -->

也可以不写代码：把密钥写进项目文件。包会把它盖进可执行文件，`Atlas.Start()`
（无参数）从那里读取；在 .NET Core 上运行时会在你的 `Main` 之前调用 SDK 的 startup hook，
应用从第一行起就被覆盖。WPF、WinForms、WinUI、Avalonia 的钩子在各自框架加载时挂接；
同时调用 `Atlas.Start` 也不会有损失，第二次启动是空操作。`AtlasAutoStart` 为 false 时保留
密钥但把启动交给代码；`AtlasBaseUrl` 覆盖服务器；环境变量 `ATLAS_SDK_KEY` 可由启动器代为设置。

```xml title="App.csproj"
<!-- App.csproj：只需密钥。 -->
<PropertyGroup>
  <AtlasSdkKey>sdk_…</AtlasSdkKey>
</PropertyGroup>
```

### 模块

| 命名空间 | 作用 |
|---|---|
| `AppAtlas.Sdk` | 信封、磁盘队列、发送器。所有模块的基础。 |
| `AppAtlas.Sdk.Links` | 深层链接流入：协议激活与商店活动 id。 |
| `AppAtlas.Sdk.Crash` | 崩溃报告：宿主所有钩子的未处理异常、经 WER 的原生死亡、UI 卡死、会话。 |

三者同在一个程序集中；应用不调用的模块在运行时没有开销。

### 它如何使用磁盘

信封在任何网络尝试之前先写入磁盘，目录按进程 id 区分。桌面应用同一个
可执行文件跑多个实例是常态，两个发送器共用一个目录就是一场等着发生的
损坏。启动时队列会接收死去实例留下的内容，崩溃前一刻写下的信封最终
仍会送达。

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
        // 直接打开与延迟链接都到达这里。
        // link.Deferred: 跨越了安装的链接为 true。
        // link.Match: referrer / clipboard / campaign_id / relink。
        // 用 link.Path 与 link.Payload 做页面跳转，例如：
        // if (link.Path != null) OpenScreen(link.Path, link.Payload);
    });

    // URI 协议激活（应用注册的 scheme，或访问 URL）。
    var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
    if (activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Protocol
        && activation.Data is Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs protocol)
    {
        AtlasLinks.Handle(protocol.Uri.ToString());
    }

    // … 创建窗口
}
```

#### Visual Basic

```vb title="Application.xaml.vb (WPF)"
' Application.xaml.vb (WPF)
Protected Overrides Sub OnStartup(e As StartupEventArgs)
    MyBase.OnStartup(e)
    Atlas.Start("sdk_…")

    AtlasLinks.SetListener(Sub(link)
                               ' 直接打开与延迟链接都到达这里。
                               ' link.Deferred: 跨越了安装的链接为 true。
                               ' link.Match: referrer / clipboard / campaign_id / relink。
                               ' 用 link.Path 与 link.Payload 做页面跳转，例如：
                               ' If link.Path IsNot Nothing Then OpenScreen(link.Path, link.Payload)
                           End Sub)

    ' URI 协议激活（应用注册的 scheme，或访问 URL）。
    ' WPF 与 WinForms 从命令行参数接收。
    If e.Args.Length > 0 Then AtlasLinks.Handle(e.Args(0))
End Sub
```
<!-- tabs:end -->

先于监听器到达的链接会被保留并重放，启动时的激活不会丢失。

### 延迟链接

Microsoft Store 会在安装过程中携带 campaign id。目标为
`net8.0-windows10.0.17763` 及以上且具有包标识的应用会在 Atlas.Start
自动兑换，无需任何调用。其他目标则在打包方式允许处读取并交付一次：

<!-- tabs:start -->
#### C#

```csharp
// 打包应用：StoreContext 的 campaign id，或安装程序记录的值。
// 未打包的应用可以完全跳过这一步。
AtlasLinks.ClaimCampaignId(campaignId);
```

#### Visual Basic

```vb
' 打包应用：StoreContext 的 campaign id，或安装程序记录的值。
' 未打包的应用可以完全跳过这一步。
AtlasLinks.ClaimCampaignId(campaignId)
```
<!-- tabs:end -->

netstandard 资产有意不替你读取：读取它需要 WinRT，而 WinRT 在某些打包
形态下存在、在另一些下不存在，让 netstandard2.0 程序集去猜，只会在每种
宿主上以不同方式出错。windows 资产带有真正的 WinRT 引用，因此自动路径
只在那里。

`AtlasLinks.FirstReferringLink()` 永久返回产生这次安装的链接。

## Crash

<!-- tabs:start -->
#### C#

```csharp title="App.xaml.cs (WPF)"
// App.xaml.cs (WPF)：WinForms 与控制台应用同样在各自的启动路径中调用 Atlas.Start。
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);
    Atlas.Start("sdk_…");
    // 从这一行起，崩溃、卡死与原生死亡都会被捕获。其余均为可选。

    // 你方的已登录用户 id，以及值得与崩溃一起查看的状态。
    AtlasCrash.SetUserId("u-123");
    AtlasCrash.SetKey("screen", "checkout");
    AtlasCrash.LeaveBreadcrumb("cart", "add");
    AtlasCrash.Log("cart total recomputed");
}
```

```csharp title="CheckoutPage.xaml.cs"
// CheckoutPage.xaml.cs：任何捕获了异常却仍值得知晓的地方。
private void Pay()
{
    try
    {
        cart.Charge();
    }
    catch (PaymentException error)
    {
        AtlasCrash.RecordError(error);
        // 应用自身的恢复逻辑放在这里。例如：
        // ShowRetry();
    }
}
```

#### Visual Basic

```vb title="Application.xaml.vb (WPF)"
' Application.xaml.vb (WPF)：WinForms 与控制台应用同样在各自的启动路径中调用 Atlas.Start。
Protected Overrides Sub OnStartup(e As StartupEventArgs)
    MyBase.OnStartup(e)
    Atlas.Start("sdk_…")
    ' 从这一行起，崩溃、卡死与原生死亡都会被捕获。其余均为可选。

    ' 你方的已登录用户 id，以及值得与崩溃一起查看的状态。
    AtlasCrash.SetUserId("u-123")
    AtlasCrash.SetKey("screen", "checkout")
    AtlasCrash.LeaveBreadcrumb("cart", "add")
    AtlasCrash.Log("cart total recomputed")
End Sub
```

```vb title="CheckoutPage.xaml.vb"
' CheckoutPage.xaml.vb：任何捕获了异常却仍值得知晓的地方。
Private Sub Pay()
    Try
        cart.Charge()
    Catch err As PaymentException
        AtlasCrash.RecordError(err)
        ' 应用自身的恢复逻辑放在这里。例如：
        ' ShowRetry()
    End Try
End Sub
```
<!-- tabs:end -->

除 `Atlas.Start` 外无需任何调用即可捕获：

| 死亡方式 | 捕获方式 |
|---|---|
| 任意线程的未处理异常，含 async 路径。 | 每个宿主都有的兜底 `AppDomain.UnhandledException`；处理器返回时进程即结束，因此当场写入磁盘。 |
| 无人 await 的 Task 的异常。 | `TaskScheduler.UnobservedTaskException`，作为已处理错误上报。 |
| WPF、WinForms、WinUI 3、Avalonia UI 线程上的异常。 | `Dispatcher.UnhandledException`、`Application.ThreadException`、`Application.UnhandledException`、`Dispatcher.UIThread.UnhandledException`；在该框架加载时按名称挂接，在应用决定之前作为错误上报；若无人处理，兜底仍会写下崩溃。 |
| 原生故障：互操作中的访问违规、堆损坏、非法指令。 | 进程的顶层异常过滤器，由托管代码接管并链接在运行时的过滤器之前：在故障线程上当场写入，附 OS 展开器走出的该线程堆栈（每帧的模块、偏移与 debug id，含托管帧）。 |
| 栈溢出、`FailFast`：绕过一切过滤器的死亡。 | Windows Error Reporting 的 LocalDumps，启动时在用户自己的注册表配置单元下为本可执行文件注册（在 Windows 承认该键的地方）；留下的转储在下次启动时读取异常代码、故障地址及其模块，随后删除。 |
| UI 线程卡死。 | 看门狗：经 UI 线程的 `SynchronizationContext`，或应用建好后找到的 WPF Dispatcher、WinForms 窗体，5 秒无应答即上报，每次冻结一次；仅当存在这样的线程时，且机器休眠唤醒后不计。 |
| 无法解释的死亡：kill、转储未能捕获的栈溢出、断电。 | 按进程 id 保存的运行记录：无崩溃、无转储、无退出事件即将会话结束为 abnormal，不虚构问题。 |

崩溃在垂死线程上连同其会话的结束一起先写入磁盘，crash-free 会话正是据此统计，
然后在一个即将终止的进程所能承受的 2 秒内尝试发送；未能送出的在下次启动时发送。
每份报告携带最近 100 条面包屑、至多 64 个键、`AtlasCrash.Log` 最新的 64 KB，以及那
一刻的进程状态：工作集、托管堆、剩余磁盘、线程与句柄数。启动后 5 秒内的崩溃会在
下次启动时最先发送。

帧按源码中的写法命名声明类型与方法，async 状态机、lambda 与本地函数会被还原为
原名。只要构建在程序集旁附带了 PDB 便带有文件与行号，并始终携带方法 token、IL
偏移与模块的 debug id，因此剥离了 PDB 的构建以后仍可解析。同一可执行文件的多个
实例各自保有队列与记录，死去实例的遗留由下一个启动的实例接收。

`AtlasCrash.SetEnabled(false)` 停止收集并记住该选择，用于同意界面。
`AtlasCrash.CrashedLastRun` 告知上一次运行是否以本 SDK 记录的崩溃结束，
无论是它自己写下的，还是系统留下的转储。

## 隐私

SDK 只生成一个安装范围内的随机 id，不读取任何机器或硬件标识符。
随附发送的设备信息（系统版本、架构、运行时、区域、时区、应用版本）
是常见的崩溃报告字段，不指向任何人。

<!-- guide:end -->

## 检查

```sh
sh check-core.sh                             # 构建、对进程内监听器跑通流程、
                                             # 把自身作为受害进程按崩溃钩子能捕获的
                                             # 每种方式杀死、比对黄金字节
ATLAS_SERVER=../app-atlas sh check-core.sh   # 再加服务器的真实解析器
```

MIT.
