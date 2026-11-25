using SkiaSharp;

namespace Homework.Core.Recognition;

/// <summary>
/// 可注入的填涂检测引擎。App 层编排只依赖此接口，测试可注入假引擎返回确定结果，
/// 不依赖真实扫描件或图像算法。
/// </summary>
public interface IFillInEngine
{
    FillInDetectionResult Detect(SKBitmap bitmap, NormalizedRect region, int columns);
}

/// <summary>默认引擎：委托给确定性的 <see cref="FillInDetector"/>，全部在本机完成。</summary>
public sealed class SkiaFillInEngine : IFillInEngine
{
    public FillInDetectionResult Detect(SKBitmap bitmap, NormalizedRect region, int columns) =>
        FillInDetector.Detect(bitmap, region, columns);
}
