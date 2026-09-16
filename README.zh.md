# Atlas SDK for .NET

[English](README.md) · [한국어](README.ko.md)

[App Atlas](https://appatlas.dev) 的 Windows 客户端。单个无依赖的
`netstandard2.0` 程序集，在 .NET Framework 4.6.1+、.NET Core / 5+、
UWP、WinUI 和 Unity 上加载的都是同一个二进制。

```sh
dotnet add package AppAtlas.Sdk
```

<!-- guide:start -->
```csharp
Atlas.Start("sdk_…");

AtlasLinks.SetListener(link =>
{
    // link.Payload / link.Path / link.Deferred / link.Match
    // link.Channel / link.Campaign / link.ShortId
});

// URI 协议激活（应用注册的 scheme，或访问 URL）。
AtlasLinks.Handle(activationUri);
```

先于监听器到达的链接会被保留并重放，启动时的激活不会丢失。

## 延迟链接

Microsoft Store 会在安装过程中携带 campaign id。
在你的打包方式允许处读取它，并交付一次：

```csharp
// 打包应用：StoreContext 的 campaign id，或安装程序记录的值。
// 未打包的应用可以完全跳过这一步。
AtlasLinks.ClaimCampaignId(campaignId);
```

SDK 有意不替你读取：读取它需要 WinRT，而 WinRT 在某些打包形态下存在、
在另一些下不存在，让 netstandard2.0 程序集去猜，只会在每种宿主上
以不同方式出错。你的应用知道自己是怎么发布的。

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
