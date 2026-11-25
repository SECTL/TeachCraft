using Homework.Core.Scanning;

namespace Homework.App.Scanning;

/// <summary>把 WIA 的错误码与设备状态位翻译成中文可操作提示。纯函数，无 COM。</summary>
public static class WiaErrorText
{
    public const int DocumentHandlingStatusProperty = 3087;

    public const int StatusFeedReady = 0x01;
    public const int StatusFlatReady = 0x02;
    public const int StatusDuplexReady = 0x04;
    public const int StatusFlatCoverUp = 0x08;
    public const int StatusPathCoverUp = 0x10;
    public const int StatusPaperJam = 0x20;
    public const int StatusFilmAdapter = 0x40;
    public const int StatusStorageReady = 0x80;
    public const int StatusStorageFull = 0x100;
    public const int StatusMultipleFeed = 0x200;
    public const int StatusDeviceAttention = 0x400;
    public const int StatusLampError = 0x800;

    public static string? DescribeHResult(uint code) => code switch
    {
        0x80210001 => "扫描仪发生未知错误，请重试；仍失败请重连扫描仪。",
        0x80210002 => "输稿器卡纸。请打开输稿器清除卡纸后重新扫描。",
        0x80210003 => "输稿器没有纸。请放好原稿后重新扫描。",
        0x80210004 => "进纸器有问题，请检查原稿是否放正、纸张是否符合要求。",
        0x80210005 => "扫描仪离线。请确认电源已开、连接正常。",
        0x80210006 => "扫描仪正忙。请关闭其它使用这台扫描仪的程序，或稍后重试。",
        0x80210007 => "扫描仪正在预热，请稍候重试。",
        0x80210008 => "扫描仪需要人工处理，请查看设备面板提示。",
        0x8021000A => "与扫描仪通信失败。请重新连接扫描仪或重启后再试。",
        0x8021000B => "扫描仪不支持该命令。",
        0x8021000D => "扫描仪被其它程序锁定，请关闭占用它的程序。",
        0x8021000E => "扫描仪驱动发生错误，请重装或更新驱动。",
        0x8021000F => "扫描仪返回了无效响应，请重试。",
        0x80210015 => "没有可用的扫描仪。",
        0x80210016 => "扫描仪盖板未合，请合上盖板后重新扫描。",
        0x80210020 => "检测到多页同时进纸。请整理原稿后重新扫描。",
        _ => null,
    };

    public static bool IsUserCancelled(uint code) =>
        code is 0x80040200 or 0x80040202;

    public static bool IsBlocking(int status) =>
        (status & (StatusPaperJam | StatusMultipleFeed | StatusDeviceAttention |
                   StatusLampError | StatusFlatCoverUp | StatusPathCoverUp)) != 0;

    public static IReadOnlyList<string> DescribeStatus(int status)
    {
        var messages = new List<string>();
        if ((status & StatusPaperJam) != 0) messages.Add("输稿器卡纸，请清除卡纸。");
        if ((status & StatusMultipleFeed) != 0) messages.Add("多页同时进纸，请整理原稿。");
        if ((status & StatusDeviceAttention) != 0) messages.Add("扫描仪需要人工处理，请查看设备面板。");
        if ((status & StatusLampError) != 0) messages.Add("扫描仪灯故障，请检查设备。");
        if ((status & StatusFlatCoverUp) != 0) messages.Add("稿台盖板未合，请合上盖板。");
        if ((status & StatusPathCoverUp) != 0) messages.Add("走纸通道盖板未合，请合上盖板。");
        if ((status & StatusStorageFull) != 0) messages.Add("扫描仪存储空间已满。");
        return messages;
    }
}
