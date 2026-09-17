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

```csharp title="App.xaml.cs (WinUI 3)"
// App.xaml.cs (WinUI 3): OnLaunched。WPF 与控制台应用在各自的启动
// 位置调用同样的两个方法。
Atlas.Start("sdk_…");

AtlasLinks.SetListener(link =>
{
    // 直接打开与延迟链接都到达这里。
    // link.Deferred: 跨越了安装的链接为 true。
    // link.Match: referrer / clipboard / campaign_id / relink。
    // 用 link.Path 与 link.Payload 做页面跳转，例如：
    // if (link.Path != null) OpenScreen(link.Path, link.Payload);
});
// windows 目标且具有包标识时，延迟链接到此为止：
// SDK 会自行兑换 campaign id。

// URI 协议激活（应用注册的 scheme，或访问 URL）。
// WinUI 3 从 AppInstance.GetCurrent().GetActivatedEventArgs() 读取；
// WPF 与 WinForms 从命令行参数接收。
AtlasLinks.Handle(activationUri);
```

#### Visual Basic

```vb
Atlas.Start("sdk_…")

AtlasLinks.SetListener(Sub(link)
                           ' link.Payload / link.Path / link.Deferred / link.Match
                           ' link.Channel / link.Campaign / link.ShortId
                       End Sub)

' URI 协议激活（应用注册的 scheme，或访问 URL）。
AtlasLinks.Handle(activationUri)
```
<!-- tabs:end -->

先于监听器到达的链接会被保留并重放，启动时的激活不会丢失。

## 延迟链接

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

## 它如何使用磁盘

信封在任何网络尝试之前先写入磁盘，目录按进程 id 区分。桌面应用同一个
可执行文件跑多个实例是常态，两个发送器共用一个目录就是一场等着发生的
损坏。启动时队列会接收死去实例留下的内容，崩溃前一刻写下的信封最终
仍会送达。

## 隐私

SDK 只生成一个安装范围内的随机 id，不读取任何机器或硬件标识符。
随附发送的设备信息（系统版本、架构、运行时、区域、时区、应用版本）
是常见的崩溃报告字段，不指向任何人。
<!-- guide:end -->

## 检查

```sh
sh check-core.sh                             # 构建、对进程内监听器跑通流程、
                                             # 比对黄金字节
ATLAS_SERVER=../app-atlas sh check-core.sh   # 再加服务器的真实解析器
```

MIT.
