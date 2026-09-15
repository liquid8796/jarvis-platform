using System.IO;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The registry upload_image resolves an id against. Captures are held rather
/// than written, because save_to_disk is the argument that puts a screenshot on
/// disk and taking one should not leave a file behind unasked.
/// </summary>
public sealed class CapturedImagesTests : IDisposable
{
    // A one-pixel PNG, base64 as the extension hands it over.
    private const string Png =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    public CapturedImagesTests() => CapturedImages.Reset();

    public void Dispose() => CapturedImages.Reset();

    [Fact]
    public void Ids_are_handed_out_in_order_and_resolve_to_what_was_registered()
    {
        Assert.Equal("img_1", CapturedImages.Register(Png));
        Assert.Equal("img_2", CapturedImages.Register("other"));

        Assert.Equal(Png, CapturedImages.Find("img_1"));
        Assert.Equal("other", CapturedImages.Find("img_2"));
    }

    [Fact]
    public void An_unknown_id_answers_null_and_has_a_refusal()
    {
        Assert.Null(CapturedImages.Find("img_99"));
        Assert.Contains("No captured image with id \"img_99\"", CapturedImages.NotFound("img_99"), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_last_Keep_captures_stay_resolvable()
    {
        for (var i = 0; i < CapturedImages.Keep + 3; i++)
        {
            CapturedImages.Register($"capture-{i}");
        }

        Assert.Null(CapturedImages.Find("img_1"));
        Assert.Null(CapturedImages.Find("img_3"));
        Assert.Equal("capture-3", CapturedImages.Find("img_4"));
        Assert.Equal($"capture-{CapturedImages.Keep + 2}", CapturedImages.Find($"img_{CapturedImages.Keep + 3}"));
    }

    [Fact]
    public void A_capture_reaches_disk_only_when_an_upload_needs_a_path()
    {
        var id = CapturedImages.Register(Png);
        var path = CapturedImages.WriteToDisk(id, "shot.png");

        Assert.NotNull(path);
        Assert.Equal("shot.png", Path.GetFileName(path));
        Assert.Equal(Convert.FromBase64String(Png).Length, new FileInfo(path!).Length);

        Assert.Null(CapturedImages.WriteToDisk("img_99", "shot.png"));
        File.Delete(path!);
    }
}
