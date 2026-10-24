using Govor.Application.Reactions;
using Govor.Domain.Models.Reactions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace Govor.Server.Tests;

[TestFixture]
public class ReactionMediaTests
{
    private readonly ReactionMediaProcessor _processor = new();

    [Test]
    public async Task StaticImageIsResizedReencodedAndMetadataIsRemoved()
    {
        using var source = new Image<Rgba32>(512, 256, Color.Red.ToPixel<Rgba32>());
        source.Metadata.ExifProfile = new ExifProfile();
        source.Metadata.ExifProfile.SetValue(ExifTag.Artist, "private metadata");
        using var input = new MemoryStream();
        await source.SaveAsJpegAsync(input);
        var result = await _processor.ProcessAsync(input.ToArray());
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value.Kind, Is.EqualTo(ReactionKind.Image));
        Assert.That(result.Value.MimeType, Is.EqualTo("image/png"));
        Assert.That(result.Value.Width, Is.LessThanOrEqualTo(128));
        Assert.That(result.Value.Height, Is.LessThanOrEqualTo(128));
        Assert.That(result.Value.Data.Length, Is.LessThanOrEqualTo(256 * 1024));
        using var decoded = Image.Load(result.Value.Data);
        Assert.That(decoded.Metadata.ExifProfile, Is.Null);
    }

    [Test]
    public async Task GifKeepsFramesAndTimingWithinLimits()
    {
        var input = await GifAsync(2, 25, 256);
        var result = await _processor.ProcessAsync(input);
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value.Kind, Is.EqualTo(ReactionKind.Gif));
        Assert.That(result.Value.DurationMilliseconds, Is.EqualTo(500));
        Assert.That(result.Value.Data.Length, Is.LessThanOrEqualTo(512 * 1024));
        using var decoded = Image.Load(result.Value.Data);
        Assert.That(decoded.Frames.Count, Is.EqualTo(2));
        Assert.That(decoded.Width, Is.LessThanOrEqualTo(128));
        Assert.That(decoded.Frames.Select(f => f.Metadata.GetGifMetadata().FrameDelay), Is.EqualTo(new[] { 25, 25 }));
    }

    [Test]
    public async Task LongAndExcessiveFrameAnimationsAreRejected()
    {
        Assert.That((await _processor.ProcessAsync(await GifAsync(2, 300))).IsFailure, Is.True);
        Assert.That((await _processor.ProcessAsync(await GifAsync(61, 2))).IsFailure, Is.True);
    }

    [Test]
    public async Task UnsupportedMalformedAndOversizedInputsAreRejected()
    {
        Assert.That((await _processor.ProcessAsync("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray())).IsFailure, Is.True);
        Assert.That((await _processor.ProcessAsync(new byte[ReactionMediaLimits.MaxUploadBytes + 1])).IsFailure, Is.True);
        using var source = new Image<Rgba32>(4096, 1);
        using var input = new MemoryStream();
        await source.SaveAsPngAsync(input);
        Assert.That((await _processor.ProcessAsync(input.ToArray())).IsFailure, Is.True);
    }

    public static async Task<byte[]> GifAsync(int frames, int delay, int dimension = 16)
    {
        using var image = new Image<Rgba32>(dimension, dimension, Color.Red.ToPixel<Rgba32>());
        image.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = delay;
        for (var i = 1; i < frames; i++)
        {
            using var next = new Image<Rgba32>(dimension, dimension, (i % 2 == 0 ? Color.Red : Color.Blue).ToPixel<Rgba32>());
            next.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = delay;
            image.Frames.AddFrame(next.Frames.RootFrame);
        }
        using var input = new MemoryStream();
        await image.SaveAsGifAsync(input);
        return input.ToArray();
    }
}
